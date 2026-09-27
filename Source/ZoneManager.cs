using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PlaygroundMod
{
    /// <summary>
    /// 遊び場エリアの管理。変更はすべてシミュレーションスレッドで行い、Lock内で書き換える。
    /// メインスレッド(描画・ツール)はLock内で読むだけ。
    /// </summary>
    public static class ZoneManager
    {
        public static readonly object Lock = new object();
        public static readonly List<PlaygroundZone> Zones = new List<PlaygroundZone>();
        static int s_nextId = 1;

        const float MinParkPlanArea = 600f; // これより狭いエリアは公園レイアウトにせずランダム配置

        public static void Clear()
        {
            lock (Lock) { Zones.Clear(); s_nextId = 1; }
        }

        static PlaygroundZone Get(int id)
        {
            foreach (var z in Zones) if (z.Id == id) return z;
            return null;
        }

        // ---- シミュレーションスレッド ----

        public static void CreateZone(Vector2[] polygon)
        {
            var zone = new PlaygroundZone(s_nextId, polygon);
            if (zone.Area < 20f || zone.BoundsXZ.width > 600f || zone.BoundsXZ.height > 600f) return;
            if (PropCatalog.PlayCount == 0)
            {
                Debug.LogWarning("[PlaygroundMod] no playground props loaded");
                return;
            }
            s_nextId++;
            Populate(zone, null);
            if (zone.Props.Count == 0) { ReleaseAll(zone); return; } // 何も置けなければ柵・木・園路も撤去
            lock (Lock) Zones.Add(zone);
            ParkGuard.Unzone(zone); // 敷地内の区画を消す(以後は建物も建たない)
        }

        public static void RemoveZone(int id)
        {
            var z = Get(id);
            if (z == null) return;
            ActorManager.ReleaseZone(id);
            ReleaseAll(z);
            lock (Lock) Zones.Remove(z);
        }

        /// <summary>同じ範囲のまま、別の組み合わせ・別のゲート位置で作り直す</summary>
        public static void Reroll(int id)
        {
            var z = Get(id);
            if (z == null) return;
            var previous = new HashSet<string>(z.PropNames.Concat(z.FenceNetNames).Concat(z.TreeNames));
            ActorManager.ReleaseZone(id);
            ReleaseAll(z);
            Populate(z, previous);
        }

        static void Populate(PlaygroundZone zone, ICollection<string> avoid)
        {
            var sm = SimulationManager.instance;
            var rng = new System.Random(unchecked((int)DateTime.Now.Ticks ^ (int)sm.m_currentFrameIndex ^ zone.Id * 7919));
            var s = ModSettings.Instance;
            avoid = avoid ?? new HashSet<string>(PropCatalog.LastUsed);

            // 柵: ネットワーク優先、無ければProp
            NetInfo fenceNet = s.PlaceFence && s.UseFenceNetwork ? PropCatalog.PickFenceNet(rng, avoid) : null;
            CatalogEntry fenceProp = s.PlaceFence && fenceNet == null ? PropCatalog.PickFence(rng, avoid) : null;

            List<Placement> placements;
            List<Gate> gates;
            float gateWidth = s.GateWidth;
            var paths = new List<Vector2[]>();
            var ring = new Vector2[0];
            var trees = new List<KeyValuePair<TreeInfo, Vector3>>();
            NetInfo pathNet = null;
            bool parkPlan = s.LayoutMode == 0 && zone.Area >= MinParkPlanArea;

            if (parkPlan)
            {
                pathNet = s.UsePathNetwork ? PropCatalog.PathNet : null;
                float half = pathNet != null ? Mathf.Clamp(pathNet.m_halfWidth, 1f, 4f) : s.PathWidth * 0.5f;
                var lay = LayoutGenerator.Generate(zone, rng, avoid, half);
                placements = lay.Props;
                gates = lay.Gates;
                gateWidth = lay.GateWidth;
                paths = lay.Paths;
                ring = lay.Ring;
                trees = lay.Trees;
                if (fenceProp != null && gates.Count > 0)
                    PlacementGenerator.PlaceFence(zone, fenceProp.Info, gates, gateWidth, placements);
            }
            else
            {
                int types = Mathf.Clamp(2 + (int)(zone.Area / 500f), 2, 6);
                var play = PropCatalog.PickPlay(rng, types, avoid);
                var benches = s.BenchDensity > 0f ? PropCatalog.PickBenches(rng, avoid) : new List<CatalogEntry>();
                CatalogEntry toilet = s.PlaceToilet && zone.Area >= s.ToiletMinArea ? PropCatalog.PickToilet(rng, avoid) : null;
                placements = PlacementGenerator.Generate(zone, play, benches, toilet, fenceProp, fenceNet != null, rng, out gates);
            }

            // ---- Prop ----
            var pm = PropManager.instance;
            var ids = new List<ushort>();
            var names = new List<string>();
            foreach (var p in placements)
            {
                if (pm.CreateProp(out ushort propId, ref sm.m_randomizer, p.Info, p.Position, p.Angle, true))
                {
                    ids.Add(propId);
                    names.Add(p.Info.name);
                }
            }

            // ---- 園路 → 柵(園路の端に合わせて切れる) ----
            var pathSegs = pathNet != null ? NetBuilder.BuildPaths(pathNet, paths, ring) : new List<ushort>();
            var fenceSegs = fenceNet != null && gates.Count > 0
                ? FenceBuilder.Build(zone, fenceNet, gates, gateWidth)
                : new List<ushort>();

            // ---- 木 ----
            var tm = TreeManager.instance;
            var treeIds = new List<uint>();
            var treeNames = new List<string>();
            foreach (var t in trees)
            {
                if (tm.CreateTree(out uint treeId, ref sm.m_randomizer, t.Key, t.Value, true))
                {
                    treeIds.Add(treeId);
                    treeNames.Add(t.Key.name);
                }
            }

            lock (Lock)
            {
                zone.Props.AddRange(ids);
                zone.PropNames.AddRange(names);
                foreach (var seg in fenceSegs) { zone.FenceSegments.Add(seg); zone.FenceNetNames.Add(fenceNet.name); }
                foreach (var seg in pathSegs) { zone.PathSegments.Add(seg); zone.PathNetNames.Add(pathNet.name); }
                zone.Trees.AddRange(treeIds);
                zone.TreeNames.AddRange(treeNames);

                // 公園レイアウトは柵が無くてもゲート=入口として使う。ランダム配置は柵がある時だけ
                bool fenced = fenceSegs.Count > 0 || (fenceProp != null && names.Contains(fenceProp.Info.name));
                if (parkPlan || fenced)
                {
                    zone.Gates.AddRange(gates);
                    zone.Paths.AddRange(paths);
                    zone.Ring = ring;
                }
            }
            zone.RebuildSpots();

            var used = new List<string>(names);
            used.AddRange(treeNames);
            if (fenceNet != null) used.Add(fenceNet.name);
            PropCatalog.SetLastUsed(used);
        }

        static void ReleaseAll(PlaygroundZone z)
        {
            var pm = PropManager.instance;
            for (int i = 0; i < z.Props.Count; i++)
            {
                ushort id = z.Props[i];
                var p = pm.m_props.m_buffer[id];
                if ((p.m_flags & (ushort)PropInstance.Flags.Created) != 0 && p.Info != null && p.Info.name == z.PropNames[i])
                    pm.ReleaseProp(id);
            }
            NetBuilder.Release(z.FenceSegments, z.FenceNetNames);
            NetBuilder.Release(z.PathSegments, z.PathNetNames);

            var tm = TreeManager.instance;
            for (int i = 0; i < z.Trees.Count; i++)
                if (IsOurTree(z.Trees[i], z.TreeNames[i])) tm.ReleaseTree(z.Trees[i]);

            lock (Lock)
            {
                z.Props.Clear(); z.PropNames.Clear();
                z.FenceSegments.Clear(); z.FenceNetNames.Clear();
                z.PathSegments.Clear(); z.PathNetNames.Clear();
                z.Trees.Clear(); z.TreeNames.Clear();
                z.Gates.Clear(); z.Paths.Clear();
                z.Ring = new Vector2[0];
            }
            z.Spots.Clear();
        }

        static bool IsOurTree(uint id, string name)
        {
            var t = TreeManager.instance.m_trees.m_buffer[id];
            return (t.m_flags & (ushort)TreeInstance.Flags.Created) != 0 && t.Info != null && t.Info.name == name;
        }

        /// <summary>ロード後：消されたProp/ネットワーク/木を管理対象から外し、スポットを再構築。保存時に来ていた住民は帰宅</summary>
        public static void OnLevelLoadedSim()
        {
            var buf = PropManager.instance.m_props.m_buffer;
            lock (Lock)
            {
                foreach (var z in Zones)
                {
                    for (int i = z.Props.Count - 1; i >= 0; i--)
                    {
                        var p = buf[z.Props[i]];
                        bool ok = (p.m_flags & (ushort)PropInstance.Flags.Created) != 0 && p.Info != null && p.Info.name == z.PropNames[i];
                        if (!ok) { z.Props.RemoveAt(i); z.PropNames.RemoveAt(i); }
                    }
                    Prune(z.FenceSegments, z.FenceNetNames, NetBuilder.IsOurs);
                    Prune(z.PathSegments, z.PathNetNames, NetBuilder.IsOurs);
                    Prune(z.Trees, z.TreeNames, IsOurTree);
                }
            }
            foreach (var z in Zones) z.RebuildSpots();
            ActorManager.CleanupStale();
            ParkGuard.UnzoneAll();
        }

        static void Prune<T>(List<T> ids, List<string> names, Func<T, string, bool> alive)
        {
            for (int i = ids.Count - 1; i >= 0; i--)
                if (!alive(ids[i], names[i])) { ids.RemoveAt(i); names.RemoveAt(i); }
        }

        // ---- メインスレッド ----

        public static int FindAt(Vector3 pos)
        {
            var p = new Vector2(pos.x, pos.z);
            lock (Lock)
                foreach (var z in Zones)
                    if (z.Contains(p)) return z.Id;
            return 0;
        }

        // ---- セーブデータ ----
        // v2: ゲート / v3: フェンスのネットワーク / v4: 園路・木・園路の中心線

        const int Version = 4;

        public static byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Version);
                lock (Lock)
                {
                    w.Write(s_nextId);
                    w.Write(Zones.Count);
                    foreach (var z in Zones)
                    {
                        w.Write(z.Id);
                        WritePoints(w, z.Polygon);
                        w.Write(z.Props.Count);
                        for (int i = 0; i < z.Props.Count; i++) { w.Write(z.Props[i]); w.Write(z.PropNames[i] ?? ""); }
                        w.Write(z.Gates.Count);
                        foreach (var g in z.Gates) { w.Write(g.Pos.x); w.Write(g.Pos.y); w.Write(g.Inward.x); w.Write(g.Inward.y); }
                        w.Write(z.FenceSegments.Count);
                        for (int i = 0; i < z.FenceSegments.Count; i++) { w.Write(z.FenceSegments[i]); w.Write(z.FenceNetNames[i] ?? ""); }
                        // v4
                        w.Write(z.PathSegments.Count);
                        for (int i = 0; i < z.PathSegments.Count; i++) { w.Write(z.PathSegments[i]); w.Write(z.PathNetNames[i] ?? ""); }
                        w.Write(z.Trees.Count);
                        for (int i = 0; i < z.Trees.Count; i++) { w.Write(z.Trees[i]); w.Write(z.TreeNames[i] ?? ""); }
                        w.Write(z.Paths.Count);
                        foreach (var path in z.Paths) WritePoints(w, path);
                        WritePoints(w, z.Ring);
                    }
                }
                var actors = ActorManager.SnapshotForSave();
                w.Write(actors.Count);
                foreach (var a in actors) { w.Write(a.Key); w.Write(a.Value); }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void Deserialize(byte[] data)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data)))
                {
                    int version = r.ReadInt32();
                    if (version < 1 || version > Version) { Debug.LogWarning("[PlaygroundMod] unknown data version " + version); return; }
                    lock (Lock)
                    {
                        Zones.Clear();
                        s_nextId = r.ReadInt32();
                        int zc = r.ReadInt32();
                        for (int zi = 0; zi < zc; zi++)
                        {
                            int id = r.ReadInt32();
                            var z = new PlaygroundZone(id, ReadPoints(r));
                            int propCount = r.ReadInt32();
                            for (int i = 0; i < propCount; i++) { z.Props.Add(r.ReadUInt16()); z.PropNames.Add(r.ReadString()); }
                            if (version >= 2)
                            {
                                int gc = r.ReadInt32();
                                for (int i = 0; i < gc; i++)
                                    z.Gates.Add(new Gate
                                    {
                                        Pos = new Vector2(r.ReadSingle(), r.ReadSingle()),
                                        Inward = new Vector2(r.ReadSingle(), r.ReadSingle()),
                                    });
                            }
                            if (version >= 3)
                            {
                                int sc = r.ReadInt32();
                                for (int i = 0; i < sc; i++) { z.FenceSegments.Add(r.ReadUInt16()); z.FenceNetNames.Add(r.ReadString()); }
                            }
                            if (version >= 4)
                            {
                                int pc = r.ReadInt32();
                                for (int i = 0; i < pc; i++) { z.PathSegments.Add(r.ReadUInt16()); z.PathNetNames.Add(r.ReadString()); }
                                int tc = r.ReadInt32();
                                for (int i = 0; i < tc; i++) { z.Trees.Add(r.ReadUInt32()); z.TreeNames.Add(r.ReadString()); }
                                int lc = r.ReadInt32();
                                for (int i = 0; i < lc; i++) z.Paths.Add(ReadPoints(r));
                                z.Ring = ReadPoints(r);
                            }
                            Zones.Add(z);
                        }
                    }
                    int ac = r.ReadInt32();
                    var stale = new List<KeyValuePair<ushort, uint>>();
                    for (int i = 0; i < ac; i++) stale.Add(new KeyValuePair<ushort, uint>(r.ReadUInt16(), r.ReadUInt32()));
                    ActorManager.SetStale(stale);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[PlaygroundMod] load failed: " + e);
                Clear();
            }
        }

        static void WritePoints(BinaryWriter w, Vector2[] pts)
        {
            pts = pts ?? new Vector2[0];
            w.Write(pts.Length);
            foreach (var v in pts) { w.Write(v.x); w.Write(v.y); }
        }

        static Vector2[] ReadPoints(BinaryReader r)
        {
            int n = r.ReadInt32();
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++) pts[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
            return pts;
        }
    }
}
