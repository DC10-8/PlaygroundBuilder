using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlaygroundMod
{
    public enum ActorState { Enter, Play, Errand, Leave }

    public class Actor
    {
        public ushort Instance;
        public uint Citizen;
        public int ZoneId;
        public PlaygroundZone Zone;
        public int Gate = -1;          // 出入りに使うゲート
        public PlaySpot Spot;
        public int Slot;
        public ActorState State;
        public Vector3 Exit;
        public List<Vector3> Route = new List<Vector3>(); // 園路の経由地
        public int RouteIdx;
        public int Timer, Wait, Sub;
        public float Phase;
        public Vector3 WanderTarget;
        public bool Finished;
        public bool Started;          // 一度でもPlayに入ったか(用事から戻った時にタイマーを再設定しない)
        public bool Seated;           // 直前のステップで座っていたか
        public int Played;            // 今の遊具で遊んだステップ数(ブランコのこぎ始め用)
        public int StandUp;           // 立ち上がり中の残りステップ

        // 用事(トイレ・水飲み)
        public PlayKind ErrandKind;
        public int ErrandAt = -1;     // Timerがこの値になったら用事へ
        public PlaySpot ErrandSpot;
        public int ErrandSlot = -1;
        public bool Hidden;

        public void FreeSlot()
        {
            if (Spot != null && Slot >= 0 && Slot < Spot.Used.Length) Spot.Used[Slot] = false;
            FreeErrand();
        }

        public void FreeErrand()
        {
            if (ErrandSpot != null && ErrandSlot >= 0 && ErrandSlot < ErrandSpot.Used.Length) ErrandSpot.Used[ErrandSlot] = false;
            ErrandSpot = null;
            ErrandSlot = -1;
        }
    }

    /// <summary>
    /// 近所の家にいる住民をCitizenInstanceとして出現させ、園路を通って遊具・ベンチなどへ行き、使ってから帰宅させる。
    /// 使っている間の移動・姿勢はHarmonyパッチ(HumanAI.SimulationStepのフレーム版)で乗っ取る。
    /// 1インスタンスは16フレームに1回シミュレートされる前提(= velocity*0.5 で位置更新)。
    /// </summary>
    public static class ActorManager
    {
        static readonly object s_lock = new object();
        static readonly Dictionary<ushort, Actor> s_actors = new Dictionary<ushort, Actor>();
        static readonly List<ushort> s_remove = new List<ushort>();
        static readonly List<KeyValuePair<ushort, uint>> s_stale = new List<KeyValuePair<ushort, uint>>();
        static readonly List<ushort> s_buildings = new List<ushort>();
        static readonly List<uint> s_candidates = new List<uint>();
        static readonly System.Random s_rng = new System.Random();

        /// <summary>
        /// 座りポーズ。ResidentAI.SetRenderParameters は AtTarget と SittingDown が両方立っている時だけ座りモーションにする
        /// </summary>
        const CitizenInstance.Flags SitFlags = CitizenInstance.Flags.SittingDown | CitizenInstance.Flags.AtTarget;
        const int StandUpSteps = 2; // 立ち上がる間の待ち(バニラの RequireSlowStart 相当)

        static readonly Func<PlaySpot, bool> IsPlay = s => PropCatalog.IsPlayKind(s.Kind);
        static readonly Func<PlaySpot, bool> IsRest = s => PropCatalog.IsRest(s.Kind);
        static readonly Func<PlaySpot, bool> IsVisit = s => PropCatalog.IsPlayKind(s.Kind) || PropCatalog.IsRest(s.Kind);

        public static void Clear()
        {
            lock (s_lock) { s_actors.Clear(); s_stale.Clear(); }
        }

        // ================= Tick (シミュレーションスレッド) =================

        public static void SimulationTick()
        {
            if (s_actors.Count > 0) Maintain();
            if ((SimulationManager.instance.m_currentFrameIndex & 63) == 0) TrySpawnAll();
        }

        static void Maintain()
        {
            var inst = CitizenManager.instance.m_instances.m_buffer;
            lock (s_lock)
            {
                s_remove.Clear();
                foreach (var kv in s_actors)
                {
                    var a = kv.Value;
                    bool alive = (inst[kv.Key].m_flags & CitizenInstance.Flags.Created) != 0 && inst[kv.Key].m_citizen == a.Citizen;
                    if (!alive)
                    {
                        a.FreeSlot();
                        s_remove.Add(kv.Key);
                    }
                    else if (inst[kv.Key].m_targetBuilding != 0 || (inst[kv.Key].m_flags & CitizenInstance.Flags.WaitingPath) != 0)
                    {
                        // 本来のAIが用事(病院など)を割り当てた → バニラに返す
                        inst[kv.Key].m_flags &= ~SitFlags;
                        if (a.Hidden)
                        {
                            inst[kv.Key].m_flags &= ~CitizenInstance.Flags.InsideBuilding;
                            a.Hidden = false;
                        }
                        a.FreeSlot();
                        s_remove.Add(kv.Key);
                    }
                    else if (a.Finished)
                    {
                        ReleaseActor(a);
                        s_remove.Add(kv.Key);
                    }
                }
                foreach (var id in s_remove) s_actors.Remove(id);
            }
        }

        static void TrySpawnAll()
        {
            var settings = ModSettings.Instance;
            if (!settings.AllowNight && SimulationManager.instance.m_isNightTime) return;
            if (WeatherManager.instance.m_currentRain > 0.3f) return;

            foreach (var zone in ZoneManager.Zones)
            {
                if (zone.Spots.Count == 0) continue;
                if (CountInZone(zone.Id) >= settings.MaxActorsPerZone) continue;
                if (zone.FreeSlotCount(IsVisit) == 0) continue;
                if (s_rng.NextDouble() > 0.5) continue;
                TrySpawn(zone, settings);
            }
        }

        static int CountInZone(int zoneId)
        {
            int n = 0;
            lock (s_lock) foreach (var a in s_actors.Values) if (a.ZoneId == zoneId) n++;
            return n;
        }

        static void TrySpawn(PlaygroundZone zone, ModSettings settings)
        {
            var bm = BuildingManager.instance;
            float r = settings.SpawnRadius;
            Vector3 c = zone.Center;

            // 建物グリッド: 270x270, 1セル64m
            int minX = Mathf.Max((int)((c.x - r) / 64f + 135f), 0);
            int maxX = Mathf.Min((int)((c.x + r) / 64f + 135f), 269);
            int minZ = Mathf.Max((int)((c.z - r) / 64f + 135f), 0);
            int maxZ = Mathf.Min((int)((c.z + r) / 64f + 135f), 269);

            s_buildings.Clear();
            const Building.Flags bad = Building.Flags.Abandoned | Building.Flags.BurnedDown | Building.Flags.Collapsed;
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                {
                    ushort b = bm.m_buildingGrid[z * 270 + x];
                    int guard = 0;
                    while (b != 0)
                    {
                        var bd = bm.m_buildings.m_buffer[b];
                        if ((bd.m_flags & Building.Flags.Created) != 0 && (bd.m_flags & bad) == 0 &&
                            bd.Info != null && bd.Info.m_class.m_service == ItemClass.Service.Residential)
                        {
                            float dx = bd.m_position.x - c.x, dz = bd.m_position.z - c.z;
                            if (dx * dx + dz * dz < r * r) s_buildings.Add(b);
                        }
                        b = bd.m_nextGridBuilding;
                        if (++guard > 49152) break;
                    }
                }
            if (s_buildings.Count == 0) return;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                ushort home = s_buildings[s_rng.Next(s_buildings.Count)];
                uint cid = PickCitizen(home, settings);
                if (cid != 0) { Spawn(zone, cid, home, settings); return; }
            }
        }

        static uint PickCitizen(ushort home, ModSettings settings)
        {
            var cm = CitizenManager.instance;
            s_candidates.Clear();
            uint unit = BuildingManager.instance.m_buildings.m_buffer[home].m_citizenUnits;
            int guard = 0;
            while (unit != 0)
            {
                var u = cm.m_units.m_buffer[unit];
                if ((u.m_flags & CitizenUnit.Flags.Home) != 0)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        uint cid = u.GetCitizen(i);
                        if (cid == 0) continue;
                        int w = Weight(cid, home, settings);
                        for (int k = 0; k < w; k++) s_candidates.Add(cid);
                    }
                }
                unit = u.m_nextUnit;
                if (++guard > 524288) break;
            }
            return s_candidates.Count == 0 ? 0u : s_candidates[s_rng.Next(s_candidates.Count)];
        }

        /// <summary>子ども・10代を優先。大人(付き添い・休憩)は設定次第</summary>
        static int Weight(uint cid, ushort home, ModSettings settings)
        {
            var cz = CitizenManager.instance.m_citizens.m_buffer[cid];
            if ((cz.m_flags & Citizen.Flags.Created) == 0) return 0;
            if ((cz.m_flags & Citizen.Flags.MovingIn) != 0) return 0;
            if (cz.Dead || cz.Sick) return 0;
            if (cz.m_instance != 0 || cz.m_vehicle != 0) return 0;
            if (cz.CurrentLocation != Citizen.Location.Home || cz.m_homeBuilding != home) return 0;

            switch (Citizen.GetAgeGroup(cz.Age))
            {
                case Citizen.AgeGroup.Child: return 6;
                case Citizen.AgeGroup.Teen: return 3;
                case Citizen.AgeGroup.Young:
                case Citizen.AgeGroup.Adult: return settings.AllowAdults ? 2 : 0;
                case Citizen.AgeGroup.Senior: return settings.AllowAdults ? 1 : 0; // ベンチやシェルターで休むお年寄り
                default: return 0;
            }
        }

        /// <summary>大人は主にベンチ/シェルター/ピクニックテーブル、子どもは遊具(埋まっていれば休憩場所)</summary>
        static bool TakeVisitSlot(PlaygroundZone zone, bool adult, ModSettings settings, out PlaySpot spot, out int slot)
        {
            bool preferRest = adult ? s_rng.NextDouble() < settings.AdultBenchChance : s_rng.NextDouble() < 0.1;
            if (preferRest && zone.TakeRandomFreeSlot(s_rng, IsRest, out spot, out slot)) return true;
            if (zone.TakeRandomFreeSlot(s_rng, IsPlay, out spot, out slot)) return true;
            return zone.TakeRandomFreeSlot(s_rng, IsRest, out spot, out slot);
        }

        static void Spawn(PlaygroundZone zone, uint cid, ushort home, ModSettings settings)
        {
            var cm = CitizenManager.instance;
            var sm = SimulationManager.instance;
            var age = Citizen.GetAgeGroup(cm.m_citizens.m_buffer[cid].Age);
            bool adult = age == Citizen.AgeGroup.Young || age == Citizen.AgeGroup.Adult || age == Citizen.AgeGroup.Senior;

            if (!TakeVisitSlot(zone, adult, settings, out PlaySpot spot, out int slot)) return;

            CitizenInfo info = cm.m_citizens.m_buffer[cid].GetCitizenInfo(cid);
            if (info == null || !cm.CreateCitizenInstance(out ushort inst, ref sm.m_randomizer, info, cid))
            {
                spot.Used[slot] = false;
                return;
            }

            // 家に一番近いゲート(無ければ境界)の外から入って、園路を通って目的地へ
            Vector3 homePos = BuildingManager.instance.m_buildings.m_buffer[home].m_position;
            Vector3 entry = zone.EdgePointToward(homePos);
            int gate = zone.NearestGateIndex(homePos);
            var route = zone.RouteFromGate(gate, GoalOf(spot, slot));

            var fr = new CitizenInstance.Frame
            {
                m_position = entry,
                m_velocity = Vector3.zero,
                m_rotation = Quaternion.identity,
                m_underground = false,
                m_insideBuilding = false,
            };
            cm.m_instances.m_buffer[inst].m_frame0 = fr;
            cm.m_instances.m_buffer[inst].m_frame1 = fr;
            cm.m_instances.m_buffer[inst].m_frame2 = fr;
            cm.m_instances.m_buffer[inst].m_frame3 = fr;
            cm.m_instances.m_buffer[inst].m_targetPos = new Vector4(entry.x, entry.y, entry.z, 1f);

            cm.m_citizens.m_buffer[cid].m_instance = inst;
            cm.m_citizens.m_buffer[cid].CurrentLocation = Citizen.Location.Moving;
            cm.m_instances.m_buffer[inst].Spawn(inst);

            lock (s_lock)
            {
                s_actors[inst] = new Actor
                {
                    Instance = inst, Citizen = cid, ZoneId = zone.Id, Zone = zone, Gate = gate,
                    Spot = spot, Slot = slot, State = ActorState.Enter, Exit = entry, Route = route,
                };
            }
        }

        static Vector3 GoalOf(PlaySpot spot, int slot)
        {
            if (spot.Kind == PlayKind.Slide) { spot.GetSlide(slot, out Vector3 foot, out _, out _, out _); return foot; }
            return spot.GetSlot(slot, out _);
        }

        static void ReleaseActor(Actor a)
        {
            a.FreeSlot();
            var cm = CitizenManager.instance;
            if ((cm.m_instances.m_buffer[a.Instance].m_flags & CitizenInstance.Flags.Created) == 0) return;
            if (cm.m_instances.m_buffer[a.Instance].m_citizen != a.Citizen) return;

            cm.m_citizens.m_buffer[a.Citizen].CurrentLocation = Citizen.Location.Home;
            cm.ReleaseCitizenInstance(a.Instance);
            if (cm.m_citizens.m_buffer[a.Citizen].m_instance == a.Instance)
                cm.m_citizens.m_buffer[a.Citizen].m_instance = 0;
        }

        public static void ReleaseZone(int zoneId)
        {
            lock (s_lock)
            {
                s_remove.Clear();
                foreach (var kv in s_actors)
                    if (kv.Value.ZoneId == zoneId) { ReleaseActor(kv.Value); s_remove.Add(kv.Key); }
                foreach (var id in s_remove) s_actors.Remove(id);
            }
        }

        // ================= セーブ/ロード =================

        public static List<KeyValuePair<ushort, uint>> SnapshotForSave()
        {
            var list = new List<KeyValuePair<ushort, uint>>();
            lock (s_lock) foreach (var a in s_actors.Values) list.Add(new KeyValuePair<ushort, uint>(a.Instance, a.Citizen));
            return list;
        }

        public static void SetStale(List<KeyValuePair<ushort, uint>> list)
        {
            lock (s_lock) { s_stale.Clear(); s_stale.AddRange(list); }
        }

        /// <summary>セーブ時点で来ていた住民は、ロード直後に帰宅扱いにする</summary>
        public static void CleanupStale()
        {
            var cm = CitizenManager.instance;
            lock (s_lock)
            {
                foreach (var kv in s_stale)
                {
                    ushort inst = kv.Key; uint cid = kv.Value;
                    if ((cm.m_instances.m_buffer[inst].m_flags & CitizenInstance.Flags.Created) == 0) continue;
                    if (cm.m_instances.m_buffer[inst].m_citizen != cid) continue;
                    if (cm.m_instances.m_buffer[inst].m_targetBuilding != 0) continue; // 別用途で使われている
                    cm.m_citizens.m_buffer[cid].CurrentLocation = Citizen.Location.Home;
                    cm.ReleaseCitizenInstance(inst);
                    if (cm.m_citizens.m_buffer[cid].m_instance == inst) cm.m_citizens.m_buffer[cid].m_instance = 0;
                }
                s_stale.Clear();
            }
        }

        // ================= フレーム更新 (Harmonyから呼ばれる) =================

        /// <returns>trueなら処理済み(バニラをスキップ)</returns>
        public static bool StepActor(ushort id, ref CitizenInstance data, ref CitizenInstance.Frame f)
        {
            if (s_actors.Count == 0) return false;
            Actor a;
            lock (s_lock) { if (!s_actors.TryGetValue(id, out a)) return false; }
            if (data.m_citizen != a.Citizen) return false;

            data.m_flags &= ~SitFlags;
            if (a.Finished) { f.m_velocity = Vector3.zero; return true; }

            // 座っていた人が動き出す時は、その場で少し待って立ち上がる
            if (a.StandUp > 0)
            {
                a.StandUp--;
                f.m_velocity = Vector3.zero;
                data.m_targetPos = new Vector4(f.m_position.x, f.m_position.y, f.m_position.z, 1f);
                return true;
            }
            a.Seated = false; // このステップで Sit() が呼ばれたら true になる

            float walk = data.Info != null ? Mathf.Clamp(data.Info.m_walkSpeed, 0.6f, 2.5f) : 1f;

            switch (a.State)
            {
                case ActorState.Enter:
                    if (FollowRoute(a, ref f, walk) && WalkTo(ref f, GoalOf(a.Spot, a.Slot), walk)) BeginPlay(a);
                    break;
                case ActorState.Play:
                    PlayStep(a, ref data, ref f, walk);
                    a.Timer--;
                    if (a.Timer == a.ErrandAt && TryStartErrand(a))
                    {
                        data.m_flags &= ~SitFlags;
                        if (a.Seated) { a.Seated = false; a.StandUp = StandUpSteps; }
                        break;
                    }
                    if (a.Timer <= 0)
                    {
                        BeginLeave(a, ref data, f.m_position);
                        if (a.Seated) { a.Seated = false; a.StandUp = StandUpSteps; }
                    }
                    break;
                case ActorState.Errand:
                    ErrandStep(a, ref data, ref f, walk);
                    break;
                case ActorState.Leave:
                    if (FollowRoute(a, ref f, walk) && WalkTo(ref f, a.Exit, walk)) a.Finished = true;
                    break;
            }

            data.m_targetPos = new Vector4(f.m_position.x, f.m_position.y, f.m_position.z, 1f);
            return true;
        }

        /// <summary>経由地を順にたどる。全部通過したらtrue</summary>
        static bool FollowRoute(Actor a, ref CitizenInstance.Frame f, float walk)
        {
            while (a.RouteIdx < a.Route.Count)
            {
                if (!WalkTo(ref f, a.Route[a.RouteIdx], walk)) return false;
                a.RouteIdx++;
            }
            return true;
        }

        /// <summary>帰り: 園路に出てゲートへ(来た道を逆にたどる)</summary>
        static void BeginLeave(Actor a, ref CitizenInstance data, Vector3 from)
        {
            a.State = ActorState.Leave;
            data.m_flags &= ~SitFlags;
            var route = a.Zone != null ? a.Zone.RouteFromGate(a.Gate, from) : new List<Vector3>();
            route.Reverse();
            a.Route = route;
            a.RouteIdx = 0;
        }

        static void BeginPlay(Actor a)
        {
            a.State = ActorState.Play;
            a.Sub = 0; a.Wait = 0;
            a.Played = 0;
            if (a.Started) return; // 用事から戻っただけ
            a.Started = true;

            bool rest = PropCatalog.IsRest(a.Spot.Kind);
            a.Timer = rest ? s_rng.Next(160, 401) : s_rng.Next(90, 241); // 休憩は長め
            a.Phase = (float)(s_rng.NextDouble() * Math.PI * 2);

            // 滞在中の用事(トイレ or 水飲み)
            var s = ModSettings.Instance;
            if (a.Timer <= 30 || a.Zone == null) return;
            double roll = s_rng.NextDouble();
            if (a.Zone.HasKind(PlayKind.Toilet) && roll < s.ToiletVisitChance) a.ErrandKind = PlayKind.Toilet;
            else if (a.Zone.HasKind(PlayKind.Drink) && roll < s.ToiletVisitChance + s.DrinkChance) a.ErrandKind = PlayKind.Drink;
            else return;
            a.ErrandAt = s_rng.Next(15, a.Timer - 10);
        }

        static bool TryStartErrand(Actor a)
        {
            PlayKind kind = a.ErrandKind;
            if (a.Zone == null || !a.Zone.TakeRandomFreeSlot(s_rng, sp => sp.Kind == kind, out PlaySpot t, out int slot))
            {
                // 使用中なら少し待ってから再挑戦
                if (a.Timer > 20) a.ErrandAt = a.Timer - 8;
                return false;
            }
            a.ErrandSpot = t;
            a.ErrandSlot = slot;
            a.ErrandAt = -1;
            a.State = ActorState.Errand;
            a.Sub = 0;
            return true;
        }

        /// <summary>
        /// トイレ: 入口まで歩く → 中に入る(非表示) → 出てきて元の場所へ
        /// 水飲み: 水飲み場まで歩く → 少し立ち止まる → 元の場所へ
        /// </summary>
        static void ErrandStep(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, float walk)
        {
            var t = a.ErrandSpot;
            if (t == null) { ResumeAfterErrand(a); return; }
            bool toilet = t.Kind == PlayKind.Toilet;

            switch (a.Sub)
            {
                case 0:
                {
                    Vector3 at = t.GetSlot(a.ErrandSlot, out Vector3 face);
                    if (WalkTo(ref f, at, walk))
                    {
                        a.Sub = 1;
                        a.Wait = toilet ? s_rng.Next(8, 21) : s_rng.Next(3, 7);
                        f.m_rotation = Look(face);
                    }
                    break;
                }
                case 1:
                    if (toilet) SetHidden(a, ref data, ref f, true);
                    f.m_velocity = Vector3.zero;
                    if (--a.Wait <= 0)
                    {
                        if (toilet) SetHidden(a, ref data, ref f, false);
                        a.Sub = 2;
                    }
                    break;
                default:
                    ResumeAfterErrand(a);
                    break;
            }
        }

        static void ResumeAfterErrand(Actor a)
        {
            Vector3 here = a.ErrandSpot != null ? a.ErrandSpot.Pos : a.Exit;
            a.FreeErrand();
            a.Sub = 0;
            a.RouteIdx = a.Route.Count; // 園内は直接歩く
            a.State = a.Timer > 0 ? ActorState.Enter : ActorState.Leave;
            if (a.State == ActorState.Leave)
            {
                var route = a.Zone != null ? a.Zone.RouteFromGate(a.Gate, here) : new List<Vector3>();
                route.Reverse();
                a.Route = route;
                a.RouteIdx = 0;
            }
        }

        static void SetHidden(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, bool hidden)
        {
            a.Hidden = hidden;
            f.m_insideBuilding = hidden;
            if (hidden) data.m_flags |= CitizenInstance.Flags.InsideBuilding;
            else data.m_flags &= ~CitizenInstance.Flags.InsideBuilding;
        }

        static void PlayStep(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, float walk)
        {
            var s = a.Spot;
            float frame = SimulationManager.instance.m_currentFrameIndex;

            switch (s.Kind)
            {
                case PlayKind.Swing:
                    SwingStep(a, ref data, ref f);
                    break;
                case PlayKind.Seesaw:
                    SeesawStep(a, ref data, ref f, frame);
                    break;
                case PlayKind.Spinner:
                {
                    float ang = frame * 0.0125f + a.Slot * Mathf.PI * 2f / s.Capacity;
                    Vector3 p = s.Pos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * s.CircleRadius;
                    p.y = PlaySpot.Ground(p);
                    Sit(a, ref data, ref f, p, new Vector3(-Mathf.Sin(ang), 0f, Mathf.Cos(ang)));
                    break;
                }
                case PlayKind.Sandbox:
                    SitLookAround(a, ref data, ref f, 1.2f, 10, 30);
                    break;
                case PlayKind.Bench:
                case PlayKind.Shelter:
                case PlayKind.Picnic:
                    SitLookAround(a, ref data, ref f, 0.8f, 15, 45); // 座って周りを眺める
                    break;
                case PlayKind.Slide:
                    SlideStep(a, ref data, ref f, walk);
                    break;
                default: // ジャングルジム・複合遊具・水遊びなど
                    WanderStep(a, ref f, walk);
                    break;
            }
        }

        static void SitLookAround(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, float range, int minWait, int maxWait)
        {
            Vector3 p = a.Spot.GetSlot(a.Slot, out Vector3 face);
            if (--a.Wait <= 0)
            {
                a.Wait = s_rng.Next(minWait, maxWait);
                a.Phase = (float)(s_rng.NextDouble() - 0.5) * range;
            }
            Sit(a, ref data, ref f, p, Quaternion.AngleAxis(a.Phase * Mathf.Rad2Deg, Vector3.up) * face);
        }

        // ================= 遊具の動き =================
        // 1インスタンスは16フレームに1回更新され、描画はその間を補間する。
        // 1周期を12ステップ以上にして、動きがカクつかないようにしている

        const float SwingMaxAngle = 0.6f;             // ブランコの最大の振れ(約34°)
        const float SwingStepAngle = Mathf.PI * 2f / 14f; // 1往復14ステップ(約3.7秒)
        const float SeesawMaxRise = 0.4f;             // シーソーの上下(m)
        /// <summary>
        /// 座りモーションの腰の高さ。バニラのベンチは「座面の下の地面」に住民を置くので、
        /// 高さHの面に座らせるには位置を H − SeatHeight にする
        /// </summary>
        const float SeatHeight = 0.42f;

        /// <summary>
        /// ブランコ: 横棒を支点にした振り子。こぎ始めは小さく、だんだん大きく振り、降りる前はゆっくり止まる。
        /// 振れに合わせて体を傾ける(前に振れた時は後ろにそる)
        /// </summary>
        static void SwingStep(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f)
        {
            var s = a.Spot;
            Vector3 seat = s.GetSlot(a.Slot, out Vector3 face);
            face.y = 0f;
            face = face.sqrMagnitude > 1e-4f ? face.normalized : Vector3.forward;
            float L = s.SwingLength;

            a.Played++;
            float amp = SwingMaxAngle * Mathf.Clamp01(a.Played / 8f) * Mathf.Clamp01(a.Timer / 6f);
            a.Phase += SwingStepAngle;
            float th = amp * Mathf.Sin(a.Phase);

            Vector3 p = seat + face * (L * Mathf.Sin(th));
            p.y = seat.y + L * (1f - Mathf.Cos(th));
            Vector3 look = face * Mathf.Cos(th) + Vector3.up * Mathf.Sin(th);
            SitRot(a, ref data, ref f, p, Quaternion.LookRotation(look));
        }

        /// <summary>
        /// シーソー: 2人そろった時だけ交互に上下(フレーム番号で2人の動きを合わせる)。
        /// 1人の時は自分の側を下げて待つ。板の傾きに合わせて体を傾ける
        /// </summary>
        static void SeesawStep(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, float frame)
        {
            var s = a.Spot;
            Vector3 seat = s.GetSlot(a.Slot, out _);
            s.SeesawArm(a.Slot, seat, out float side, out float arm);

            uint now = SimulationManager.instance.m_currentFrameIndex;
            s.SeatedFrame[a.Slot] = now;
            int other = 1 - a.Slot;
            bool partner = s.Capacity == 2 && other >= 0 && other < s.SeatedFrame.Length && s.SeatedFrame[other] != 0 &&
                           now - s.SeatedFrame[other] < 64;

            float maxTilt = Mathf.Asin(Mathf.Clamp(SeesawMaxRise / arm, 0.05f, 0.5f));
            float tilt = partner ? maxTilt * Mathf.Sin(frame * 0.02f) : -side * maxTilt; // 支点の+側が上がる向き
            float rise = side * arm * Mathf.Sin(tilt);

            Vector3 p = seat;
            p.y = seat.y + rise;
            Vector3 pivot = s.Pos;
            pivot.y = seat.y;                      // 板が水平の時の高さ
            Vector3 look = pivot - p;              // 支点の方を向く(高さの差で体が傾く)
            SitRot(a, ref data, ref f, p, Quaternion.LookRotation(look.sqrMagnitude > 1e-4f ? look : Vector3.forward));
        }

        /// <summary>
        /// 滑り台: はしごの下まで歩く → 登る → 上で座る → だんだん速く滑り降りる → 立ち上がって、またはしごへ
        /// </summary>
        static void SlideStep(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, float walk)
        {
            a.Spot.GetSlide(a.Slot, out Vector3 foot, out Vector3 top, out Vector3 bottom, out float h);
            Vector3 topUp = top; topUp.y += h;                        // 上で立つ位置(足が滑り面の高さ)
            Vector3 topSit = top; topSit.y += Mathf.Max(0f, h - SeatHeight); // 上で座る位置
            Vector3 bottomSit = bottom; bottomSit.y += 0.3f - SeatHeight;  // 下端(滑り面の高さ約0.3m)に座る位置
            const int ClimbSteps = 4, SlideSteps = 6;

            switch (a.Sub)
            {
                case 0: // はしごの下へ
                    if (WalkTo(ref f, foot, walk)) { a.Sub = 1; a.Wait = 0; }
                    break;
                case 1: // 登る(立ったまま)
                {
                    a.Wait++;
                    float t = Mathf.Clamp01(a.Wait / (float)ClimbSteps);
                    Vector3 p = Vector3.Lerp(foot, top, t);
                    p.y = Mathf.Lerp(foot.y, topUp.y, t);
                    f.m_velocity = (p - f.m_position) * 2f;
                    f.m_position = p;
                    f.m_rotation = Look(top - foot);
                    if (a.Wait >= ClimbSteps) { a.Sub = 2; a.Wait = 1; }
                    break;
                }
                case 2: // 上で座る
                    SitRot(a, ref data, ref f, topSit, Quaternion.LookRotation(Flat(bottom - top)));
                    if (--a.Wait <= 0) { a.Sub = 3; a.Phase = 0f; }
                    break;
                case 3: // 滑る(だんだん速く)
                {
                    a.Phase = Mathf.Min(1f, a.Phase + 1f / SlideSteps);
                    float t = a.Phase * (0.5f + 0.5f * a.Phase); // 少しずつ加速(最後で秒速5m程度)
                    Vector3 p = Vector3.Lerp(topSit, bottomSit, t);
                    p.y = Mathf.Lerp(topSit.y, bottomSit.y, t);
                    SitRot(a, ref data, ref f, p, Quaternion.LookRotation(bottomSit - topSit));
                    if (a.Phase >= 1f) { a.Sub = 4; a.Wait = 1; }
                    break;
                }
                default: // 下で少し座ったまま → 立ち上がって、またはしごへ
                    SitRot(a, ref data, ref f, bottomSit, Quaternion.LookRotation(Flat(bottom - top)));
                    if (--a.Wait <= 0) { a.Sub = 0; a.StandUp = StandUpSteps; }
                    break;
            }
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        /// <summary>ジャングルジム・複合遊具など：遊具の周りを歩き回って時々立ち止まる</summary>
        static void WanderStep(Actor a, ref CitizenInstance.Frame f, float walk)
        {
            if (a.Sub == 0) { a.WanderTarget = a.Spot.RandomPoint(s_rng); a.Sub = 1; }
            if (a.Sub == 1)
            {
                if (WalkTo(ref f, a.WanderTarget, walk * 0.7f)) { a.Sub = 2; a.Wait = s_rng.Next(4, 14); }
            }
            else
            {
                f.m_velocity = Vector3.zero;
                if (--a.Wait <= 0) a.Sub = 0;
            }
        }

        static bool WalkTo(ref CitizenInstance.Frame f, Vector3 target, float speed)
        {
            Vector3 d = target - f.m_position;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist < 0.25f)
            {
                target.y = PlaySpot.Ground(target);
                f.m_position = target;
                f.m_velocity = Vector3.zero;
                return true;
            }
            Vector3 dir = d / dist;
            f.m_velocity = dir * Mathf.Min(speed, dist * 2f);
            Vector3 p = f.m_position + f.m_velocity * 0.5f;
            p.y = PlaySpot.Ground(p);
            f.m_position = p;
            f.m_rotation = Quaternion.LookRotation(dir);
            return false;
        }

        /// <summary>向き(傾きを含む)を指定して座らせる</summary>
        static void SitRot(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, Vector3 pos, Quaternion rot)
        {
            a.Seated = true;
            data.m_flags |= SitFlags;
            f.m_velocity = Vector3.zero;
            f.m_position = pos;
            f.m_rotation = rot;
        }

        static void Sit(Actor a, ref CitizenInstance data, ref CitizenInstance.Frame f, Vector3 pos, Vector3 face)
        {
            a.Seated = true;
            data.m_flags |= SitFlags;
            f.m_position = pos;
            f.m_velocity = Vector3.zero;
            f.m_rotation = Look(face);
        }

        static Quaternion Look(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-4f ? Quaternion.identity : Quaternion.LookRotation(v.normalized);
        }
    }
}
