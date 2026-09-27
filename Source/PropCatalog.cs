using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PlaygroundMod
{
    public enum PlayKind
    {
        // 遊具
        Swing, Slide, Seesaw, Sandbox, Spinner, Climber, Generic, WaterPlay,
        // 設備
        Bench, Toilet, Fence, Shelter, Picnic, Drink, Sign,
    }

    public class CatalogEntry
    {
        public PropInfo Info;
        public PlayKind Kind;
        public Vector3 Size;
        public float Footprint => Size.x * Size.z;
    }

    /// <summary>
    /// 遊具のメッシュから読み取った形(モデル空間)。長辺方向をL軸とする
    /// </summary>
    public struct PropGeom
    {
        public bool Valid;
        public float LongMin, LongMax;   // L軸方向の端(モデル空間)
        public float TopSign;            // 高い方の端 (+1: LongMax側, -1: LongMin側)。滑り台の上端
        public float TopHeight;          // 高い方の半分の最高点(地面から)
        public float MaxHeight;          // 全体の最高点(地面から)。ブランコの横棒の高さ
    }

    /// <summary>
    /// 読み込み済みのProp/木/ネットワークから、公園に使うものを名前キーワードで抽出・分類し、
    /// 設置のたびに違う組み合わせを抽選する
    /// </summary>
    public static class PropCatalog
    {
        // ---- キーワード(判定順: 案内板 → 除外 → トイレ → 水飲み → シェルター → 柵 → ピクニック → 遊具 → ベンチ → 汎用遊具) ----
        static readonly string[] s_sign = { "park sign", "info board", "information board", "notice board", "map board", "park map", "案内板", "公園案内" };
        static readonly string[] s_exclude =
        {
            "sign", "decal", "billboard", "marker", "landslide", "workbench", "work bench", "benchmark",
            "toilet paper", "guardrail", "guard rail", "fence post", "gate", "bus stop", "bus shelter", "timetable",
            // 実際のアセット名で誤判定が出たもの(遊具の防護柵・回転ドア・筐体・筋トレ・犬用・ホテル)
            "guard", "revolving", "arcade", "weighlifting", "weightlifting", "dog-park", "dog park", "hotel",
        };
        /// <summary>案内板から外すもの(公園以外の施設の案内板)</summary>
        static readonly string[] s_signExclude = { "hotel", "station", "airport", "mall", "shop" };
        /// <summary>シェルター判定から外すもの(駅・ホームの屋根、カート置き場、旗竿など)</summary>
        static readonly string[] s_shelterExclude = { "platform", "station", "shopping", "cart", "flag", "bus", "tram", "taxi", "metro", "train" };
        /// <summary>柵(Prop・ネットワーク)から外すもの(産業・警備・動物園・駐車場用や、支柱・角などの部品)</summary>
        static readonly string[] s_fenceExclude =
        {
            "industry", "industrial", "security", "warehouse", "forestry", "farm", "zoo", "prison", "enclosure",
            "reindeer", "bison", "antelope", "arena", "parking", "pillar", "corner", "cage", "queue", "curb only",
        };
        static readonly string[] s_toilet =
        {
            "toilet", "restroom", "rest room", "lavatory", "public wc", "wc building",
            "porta potty", "portapotty", "porta-potty", "port-a-potty", "portaloo", "outhouse",
            "トイレ", "便所", "厠",
        };
        static readonly string[] s_drink = { "drinking fountain", "water fountain", "drinking water", "bubbler", "水飲み" };
        static readonly string[] s_shelter =
        {
            "shelter", "gazebo", "pavilion", "pergola", "arbor", "arbour", "canopy", "sunshade", "shade sail",
            "東屋", "あずまや", "四阿", "シェルター", "パーゴラ", "藤棚",
        };
        static readonly string[] s_fence = { "fence", "railing", "picket", "palisade", "hedge", "柵", "フェンス", "垣根", "生垣" };
        static readonly string[] s_picnic = { "picnic", "park table", "table set", "テーブル" };
        static readonly KeyValuePair<PlayKind, string[]>[] s_rules =
        {
            new KeyValuePair<PlayKind, string[]>(PlayKind.WaterPlay, new[] { "splash", "spray", "water play", "waterplay", "水遊び", "じゃぶじゃぶ" }),
            new KeyValuePair<PlayKind, string[]>(PlayKind.Seesaw,  new[] { "seesaw", "see-saw", "see saw", "teeter", "シーソー" }),
            new KeyValuePair<PlayKind, string[]>(PlayKind.Swing,   new[] { "swing", "ブランコ", "ぶらんこ" }),
            new KeyValuePair<PlayKind, string[]>(PlayKind.Slide,   new[] { "slide", "滑り台", "すべり台" }),
            new KeyValuePair<PlayKind, string[]>(PlayKind.Sandbox, new[] { "sandbox", "sandpit", "sand pit", "砂場" }),
            new KeyValuePair<PlayKind, string[]>(PlayKind.Spinner, new[] { "merry", "roundabout", "spinner", "carousel", "回転" }),
            new KeyValuePair<PlayKind, string[]>(PlayKind.Climber, new[] { "jungle", "climb", "monkey bar", "monkeybar", "spring rider", "springrider", "rocking", "ジャングルジム", "うんてい", "雲梯" }),
        };
        static readonly string[] s_bench = { "bench", "park seat", "ベンチ", "腰掛" };
        static readonly string[] s_generic = { "playground", "play ground", "play equipment", "playset", "play set", "play structure", "kids play", "遊具" };

        static readonly Dictionary<PlayKind, List<CatalogEntry>> s_by = new Dictionary<PlayKind, List<CatalogEntry>>();
        static readonly List<CatalogEntry> s_play = new List<CatalogEntry>();
        static readonly HashSet<string> s_lastUsed = new HashSet<string>();

        public static int PlayCount => s_play.Count;
        public static int Count(PlayKind k) => s_by.TryGetValue(k, out var l) ? l.Count : 0;
        public static ICollection<string> LastUsed => s_lastUsed;

        public static bool IsPlayKind(PlayKind k) => k <= PlayKind.WaterPlay;
        public static bool IsAmenity(PlayKind k) => k >= PlayKind.Bench;
        public static bool IsRest(PlayKind k) => k == PlayKind.Bench || k == PlayKind.Shelter || k == PlayKind.Picnic;
        public static bool IsSpotless(PlayKind k) => k == PlayKind.Fence || k == PlayKind.Sign;

        /// <summary>次の設置で避ける名前(直前に使った組み合わせ)</summary>
        public static void SetLastUsed(IEnumerable<string> names)
        {
            s_lastUsed.Clear();
            foreach (var n in names) if (!string.IsNullOrEmpty(n)) s_lastUsed.Add(n);
        }

        public static void Build()
        {
            s_by.Clear(); s_play.Clear(); s_lastUsed.Clear();

            int n = PrefabCollection<PropInfo>.LoadedCount();
            for (uint i = 0; i < n; i++)
            {
                var info = PrefabCollection<PropInfo>.GetLoaded(i);
                if (info == null) continue;
                var kind = Classify(info);
                if (!kind.HasValue) continue;
                var e = new CatalogEntry { Info = info, Kind = kind.Value, Size = GetSize(info) };
                if (!s_by.TryGetValue(e.Kind, out var list)) s_by[e.Kind] = list = new List<CatalogEntry>();
                list.Add(e);
                if (IsPlayKind(e.Kind)) s_play.Add(e);
            }

            Debug.Log("[PlaygroundMod] found: " + string.Join(", ",
                ((PlayKind[])Enum.GetValues(typeof(PlayKind))).Select(k => $"{k}={Count(k)}").ToArray()));
            foreach (var kv in s_by)
                foreach (var e in kv.Value)
                    Debug.Log($"[PlaygroundMod]   {e.Kind}: {e.Info.name} ({e.Size.x:0.0}x{e.Size.z:0.0})");

            BuildNets();
            BuildTrees();
            BuildGeometry();
        }

        // ================= 遊具の形 (メインスレッドで読み取る) =================

        static readonly Dictionary<string, PropGeom> s_geom = new Dictionary<string, PropGeom>();

        public static PropGeom GetGeom(PropInfo info)
        {
            PropGeom g;
            return info != null && s_geom.TryGetValue(info.name, out g) ? g : default(PropGeom);
        }

        static void BuildGeometry()
        {
            s_geom.Clear();
            int ok = 0, total = 0;
            foreach (var kind in new[] { PlayKind.Slide, PlayKind.Swing, PlayKind.Seesaw })
            {
                if (!s_by.TryGetValue(kind, out var list)) continue;
                foreach (var e in list)
                {
                    total++;
                    var g = ComputeGeom(e.Info, e.Size);
                    if (!g.Valid) continue;
                    s_geom[e.Info.name] = g;
                    ok++;
                    Debug.Log($"[PlaygroundMod]   geom {e.Kind} {e.Info.name}: long {g.LongMin:0.0}..{g.LongMax:0.0} top={(g.TopSign > 0 ? "+" : "-")} h={g.TopHeight:0.0}/{g.MaxHeight:0.0}");
                }
            }
            Debug.Log($"[PlaygroundMod] play equipment shape read from mesh: {ok}/{total}");
        }

        /// <summary>
        /// メッシュの頂点から、長辺方向の端と、どちらの端が高いか(滑り台の上端)を調べる。
        /// 読めないメッシュ(読み取り不可のもの)は Valid=false で、形からの推定に任せる
        /// </summary>
        static PropGeom ComputeGeom(PropInfo info, Vector3 size)
        {
            var g = new PropGeom();
            Vector3[] v;
            try { v = info.m_mesh != null ? info.m_mesh.vertices : null; }
            catch { return g; }
            if (v == null || v.Length < 8) return g;

            bool xLong = size.x >= size.z;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minL = float.MaxValue, maxL = float.MinValue;
            foreach (var p in v)
            {
                float l = xLong ? p.x : p.z;
                minL = Mathf.Min(minL, l); maxL = Mathf.Max(maxL, l);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            float mid = (minL + maxL) * 0.5f;
            float topPos = float.MinValue, topNeg = float.MinValue;
            foreach (var p in v)
            {
                float l = xLong ? p.x : p.z;
                if (l >= mid) topPos = Mathf.Max(topPos, p.y); else topNeg = Mathf.Max(topNeg, p.y);
            }
            float ground = Mathf.Min(minY, 0f);
            g.Valid = maxL - minL > 0.5f && maxY - ground > 0.3f;
            g.LongMin = minL; g.LongMax = maxL;
            g.TopSign = topPos >= topNeg ? 1f : -1f;
            g.TopHeight = Mathf.Max(topPos, topNeg) - ground;
            g.MaxHeight = maxY - ground;
            return g;
        }

        public static PlayKind? Classify(PropInfo info)
        {
            string text = Text(info);
            var s = ModSettings.Instance;

            if (Any(text, s_sign, s.ExtraSignKeywords) && !Any(text, s_signExclude, null)) return PlayKind.Sign;
            if (Any(text, s_exclude, s.ExcludeKeywords)) return null;
            if (Any(text, s_toilet, s.ExtraToiletKeywords)) return PlayKind.Toilet;
            if (Any(text, s_drink, s.ExtraDrinkKeywords)) return PlayKind.Drink;
            if (Any(text, s_shelter, s.ExtraShelterKeywords) && !Any(text, s_shelterExclude, null)) return PlayKind.Shelter;
            if (Any(text, s_fence, s.ExtraFenceKeywords)) return Any(text, s_fenceExclude, null) ? (PlayKind?)null : PlayKind.Fence;
            if (Any(text, s_picnic, s.ExtraPicnicKeywords)) return PlayKind.Picnic;
            if (Any(text, new string[0], s.ExtraWaterPlayKeywords)) return PlayKind.WaterPlay;
            foreach (var rule in s_rules)
                if (Any(text, rule.Value, null)) return rule.Key;
            if (Any(text, s_bench, s.ExtraBenchKeywords)) return PlayKind.Bench;
            if (Any(text, s_generic, s.ExtraKeywords)) return PlayKind.Generic;
            return null;
        }

        static bool Any(string text, string[] builtIn, string[] extra)
        {
            foreach (var kw in builtIn) if (Has(text, kw)) return true;
            if (extra != null) foreach (var kw in extra) if (Has(text, kw)) return true;
            return false;
        }

        static bool Has(string text, string kw) => !string.IsNullOrEmpty(kw) && text.Contains(kw.ToLowerInvariant());

        static string Text(PrefabInfo info)
        {
            string t = info.name ?? "";
            try { t += " " + info.GetUncheckedLocalizedTitle(); } catch { }
            return t.ToLowerInvariant();
        }

        public static Vector3 GetSize(PropInfo info)
        {
            Vector3 s = new Vector3(4f, 3f, 4f);
            try { if (info.m_generatedInfo != null) s = info.m_generatedInfo.m_size; } catch { }
            s.x = Mathf.Clamp(s.x, 1f, 32f);
            s.y = Mathf.Clamp(s.y, 0.5f, 16f);
            s.z = Mathf.Clamp(s.z, 1f, 32f);
            return s;
        }

        // ================= 抽選 =================

        /// <summary>
        /// 条件に合うものからcount個。avoid(前回の組み合わせ)を優先的に外し、diverseKindsなら種類がバラけるように選ぶ
        /// </summary>
        public static List<CatalogEntry> Pick(System.Random rng, Func<CatalogEntry, bool> filter, int count,
                                              ICollection<string> avoid, bool diverseKinds)
        {
            var src = s_by.Values.SelectMany(l => l).Where(filter).ToList();
            var result = new List<CatalogEntry>();
            if (src.Count == 0 || count <= 0) return result;
            count = Mathf.Min(count, src.Count);
            avoid = avoid ?? s_lastUsed;

            var fresh = src.Where(e => !avoid.Contains(e.Info.name)).ToList();
            var old = src.Where(e => avoid.Contains(e.Info.name)).ToList();
            Shuffle(fresh, rng);
            Shuffle(old, rng);

            var kinds = new HashSet<PlayKind>();
            foreach (var list in new[] { fresh, old })
            {
                if (diverseKinds)
                    foreach (var e in list) { if (result.Count >= count) break; if (kinds.Add(e.Kind)) result.Add(e); }
                foreach (var e in list) { if (result.Count >= count) break; if (!result.Contains(e)) result.Add(e); }
            }
            return result;
        }

        public static CatalogEntry PickOne(System.Random rng, PlayKind kind, ICollection<string> avoid)
        {
            var l = Pick(rng, e => e.Kind == kind, 1, avoid, false);
            return l.Count > 0 ? l[0] : null;
        }

        public static CatalogEntry PickOne(System.Random rng, Func<CatalogEntry, bool> filter, ICollection<string> avoid)
        {
            var l = Pick(rng, filter, 1, avoid, false);
            return l.Count > 0 ? l[0] : null;
        }

        // ---- ランダム配置モード用 ----
        public static List<CatalogEntry> PickPlay(System.Random rng, int count, ICollection<string> avoid)
            => Pick(rng, e => IsPlayKind(e.Kind), count, avoid, true);

        public static List<CatalogEntry> PickBenches(System.Random rng, ICollection<string> avoid)
            => Pick(rng, e => e.Kind == PlayKind.Bench, Count(PlayKind.Bench) >= 2 && rng.NextDouble() < 0.3 ? 2 : 1, avoid, false);

        public static CatalogEntry PickToilet(System.Random rng, ICollection<string> avoid) => PickOne(rng, PlayKind.Toilet, avoid);
        public static CatalogEntry PickFence(System.Random rng, ICollection<string> avoid) => PickOne(rng, PlayKind.Fence, avoid);

        static void Shuffle<T>(IList<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                T tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        // ================= ネットワーク(フェンス・園路) =================

        static readonly List<NetInfo> s_fenceNets = new List<NetInfo>();
        static readonly HashSet<NetInfo> s_fenceNetSet = new HashSet<NetInfo>();
        static readonly List<NetInfo> s_pathNets = new List<NetInfo>();
        // "rail" だと railing(手すり柵) まで弾くので railway/train に限定。道路は車線チェックで除外する
        static readonly string[] s_netExclude = { "elevated", "bridge", "slope", "tunnel", "railway", "train", "track" };

        public static int FenceNetCount => s_fenceNets.Count;
        public static bool IsFenceNet(NetInfo info) => info != null && s_fenceNetSet.Contains(info);

        static void BuildNets()
        {
            s_fenceNets.Clear(); s_fenceNetSet.Clear(); s_pathNets.Clear();
            var s = ModSettings.Instance;
            int n = PrefabCollection<NetInfo>.LoadedCount();
            for (uint i = 0; i < n; i++)
            {
                var info = PrefabCollection<NetInfo>.GetLoaded(i);
                if (info == null) continue;
                if (IsFenceNetCandidate(info, s)) { s_fenceNets.Add(info); s_fenceNetSet.Add(info); }
                else if (IsPathNetCandidate(info)) s_pathNets.Add(info);
            }
            Debug.Log($"[PlaygroundMod] fence networks: {s_fenceNets.Count}, path networks: {s_pathNets.Count}");
            foreach (var f in s_fenceNets) Debug.Log($"[PlaygroundMod]   FenceNet: {f.name}");
            foreach (var f in s_pathNets) Debug.Log($"[PlaygroundMod]   PathNet: {f.name}");
        }

        static bool HasVehicleLane(NetInfo info)
        {
            if (info.m_lanes == null) return false;
            foreach (var lane in info.m_lanes)
                if (lane != null && (lane.m_laneType & NetInfo.LaneType.Vehicle) != 0 && lane.m_vehicleType != VehicleInfo.VehicleType.None)
                    return true;
            return false;
        }

        static bool IsFenceNetCandidate(NetInfo info, ModSettings s)
        {
            string name = (info.name ?? "").ToLowerInvariant();
            string text = Text(info);
            if (Any(text, new string[0], s.ExtraFenceNetKeywords)) return true;
            if (!Any(text, s_fence, null)) return false;
            if (Any(name, s_netExclude, null) || Any(name, s_exclude, s.ExcludeKeywords) || Any(text, s_fenceExclude, null)) return false;
            return !HasVehicleLane(info);
        }

        static bool IsPathNetCandidate(NetInfo info)
        {
            string name = (info.name ?? "").ToLowerInvariant();
            if (!name.Contains("pedestrian") && !name.Contains("footpath") && !name.Contains("park path")) return false;
            if (Any(name, s_netExclude, null)) return false;
            if (name.Contains("connection") || name.Contains("invisible")) return false; // 建物内部の接続用・見えない歩道
            if (HasVehicleLane(info)) return false;
            if (info.m_lanes == null || !info.m_lanes.Any(l => (l.m_laneType & NetInfo.LaneType.Pedestrian) != 0)) return false;
            return true;
        }

        public static NetInfo PickFenceNet(System.Random rng, ICollection<string> avoid)
        {
            if (s_fenceNets.Count == 0) return null;
            avoid = avoid ?? s_lastUsed;
            var fresh = s_fenceNets.Where(x => !avoid.Contains(x.name)).ToList();
            var list = fresh.Count > 0 ? fresh : s_fenceNets;
            return list[rng.Next(list.Count)];
        }

        /// <summary>園路。設定の名前に一致するものを優先、無ければ "pavement" を含むもの、それも無ければ最初の候補</summary>
        public static NetInfo PathNet
        {
            get
            {
                if (s_pathNets.Count == 0) return null;
                string want = (ModSettings.Instance.PathNetName ?? "").ToLowerInvariant();
                return s_pathNets.FirstOrDefault(x => want.Length > 0 && x.name.ToLowerInvariant() == want)
                    ?? s_pathNets.FirstOrDefault(x => want.Length > 0 && x.name.ToLowerInvariant().Contains(want))
                    ?? s_pathNets.FirstOrDefault(x => x.name.ToLowerInvariant().Contains("pavement"))
                    ?? s_pathNets[0];
            }
        }

        // ================= 木 =================

        static readonly List<TreeInfo> s_trees = new List<TreeInfo>();
        static readonly string[] s_treeExclude = { "bush", "shrub", "hedge", "flower", "grass", "dead", "stump", "log", "rock", "crop", "palm" };

        static void BuildTrees()
        {
            s_trees.Clear();
            int n = PrefabCollection<TreeInfo>.LoadedCount();
            for (uint i = 0; i < n; i++)
            {
                var info = PrefabCollection<TreeInfo>.GetLoaded(i);
                if (info == null) continue;
                string name = (info.name ?? "").ToLowerInvariant();
                if (Any(name, s_treeExclude, null)) continue;
                float h = 10f;
                try { if (info.m_generatedInfo != null) h = info.m_generatedInfo.m_size.y; } catch { }
                if (h < 5f || h > 40f) continue;
                s_trees.Add(info);
            }
            Debug.Log($"[PlaygroundMod] trees: {s_trees.Count}");
        }

        /// <summary>1エリアで使う木を1〜2種類</summary>
        public static List<TreeInfo> PickTrees(System.Random rng, ICollection<string> avoid)
        {
            var result = new List<TreeInfo>();
            if (s_trees.Count == 0) return result;
            avoid = avoid ?? s_lastUsed;
            var fresh = s_trees.Where(t => !avoid.Contains(t.name)).ToList();
            var src = fresh.Count > 0 ? fresh : s_trees.ToList();
            Shuffle(src, rng);
            int count = src.Count >= 2 && rng.NextDouble() < 0.5 ? 2 : 1;
            result.AddRange(src.Take(count));
            return result;
        }
    }
}
