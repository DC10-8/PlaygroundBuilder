using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ColossalFramework.Math;
using UnityEngine;

namespace PlaygroundMod
{
    /// <summary>ネットワーク(フェンス・園路)の作成・削除の共通処理</summary>
    public static class NetBuilder
    {
        public const float MaxSegLen = 48f;   // 1セグメントの最大長
        const float MinTerrainSegLen = 4f;    // 地形に合わせて割るときの最短
        const float HeightSampleStep = 2f;

        static MethodInfo s_createSegment;
        static int s_createSegmentArgs;
        static bool s_resolved;

        public static Vector3 Ground(Vector2 p)
        {
            var v = new Vector3(p.x, 0f, p.y);
            v.y = TerrainManager.instance.SampleRawHeightSmooth(v);
            return v;
        }

        // ================= 地形追従 =================

        /// <summary>
        /// a→b の直線を、地形に沿うように区切る位置(0〜1)を返す(両端を含む)。
        /// 区間の直線(両端の地面の高さを結んだ線)と地面の高さのズレが許容値を超えたら、
        /// 一番ズレる所で割る。最大長を超える区間も割る
        /// </summary>
        public static List<float> SplitByTerrain(Vector2 a, Vector2 b)
        {
            var s = ModSettings.Instance;
            float len = (b - a).magnitude;
            var ts = new List<float> { 0f, 1f };
            if (len < 1e-3f) return ts;

            var stack = new Stack<KeyValuePair<float, float>>();
            stack.Push(new KeyValuePair<float, float>(0f, 1f));
            int guard = 0;
            while (stack.Count > 0 && ++guard < 512)
            {
                var iv = stack.Pop();
                float t0 = iv.Key, t1 = iv.Value;
                float segLen = (t1 - t0) * len;
                float split = -1f;

                if (segLen > MaxSegLen)
                    split = (t0 + t1) * 0.5f;
                else if (s.FollowTerrain && segLen > MinTerrainSegLen * 2f)
                {
                    float h0 = Ground(Vector2.Lerp(a, b, t0)).y, h1 = Ground(Vector2.Lerp(a, b, t1)).y;
                    int n = Mathf.Max(2, Mathf.CeilToInt(segLen / HeightSampleStep));
                    float worst = s.TerrainTolerance, worstT = -1f;
                    for (int i = 1; i < n; i++)
                    {
                        float u = i / (float)n;
                        float t = Mathf.Lerp(t0, t1, u);
                        float dev = Mathf.Abs(Ground(Vector2.Lerp(a, b, t)).y - Mathf.Lerp(h0, h1, u));
                        if (dev > worst) { worst = dev; worstT = t; }
                    }
                    if (worstT >= 0f)
                    {
                        // 端に寄りすぎた分割は避ける
                        float minT = MinTerrainSegLen / len;
                        split = Mathf.Clamp(worstT, t0 + minT, t1 - minT);
                    }
                }

                if (split > t0 && split < t1)
                {
                    ts.Add(split);
                    stack.Push(new KeyValuePair<float, float>(t0, split));
                    stack.Push(new KeyValuePair<float, float>(split, t1));
                }
            }
            ts.Sort();
            return ts;
        }

        /// <summary>
        /// a(キーka)→b(キーkb) を地形に沿って区切りながらつなぐ。途中のノードは新しいキー
        /// </summary>
        public static void Polyline(Dictionary<int, ushort> nodes, NetInfo info, Vector2 a, int ka, Vector2 b, int kb,
                                    ref int nextKey, List<ushort> result, ref int failed)
        {
            var ts = SplitByTerrain(a, b);
            int prev = ka;
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                int key = i + 2 == ts.Count ? kb : nextKey++;
                Vector3 p0 = Ground(Vector2.Lerp(a, b, ts[i]));
                Vector3 p1 = Ground(Vector2.Lerp(a, b, ts[i + 1]));
                if (Connect(nodes, info, prev, p0, key, p1, out ushort seg)) result.Add(seg);
                else failed++;
                prev = key;
            }
        }

        /// <summary>keyごとにノードを1つ作って共有する</summary>
        public static bool GetNode(Dictionary<int, ushort> nodes, int key, Vector3 pos, NetInfo info, out ushort node)
        {
            if (nodes.TryGetValue(key, out node)) return true;
            var sm = SimulationManager.instance;
            if (!NetManager.instance.CreateNode(out node, ref sm.m_randomizer, info, pos, sm.m_currentBuildIndex))
                return false;
            sm.m_currentBuildIndex++;
            nodes[key] = node;
            return true;
        }

        /// <summary>直線セグメントを1本作る</summary>
        public static bool Connect(Dictionary<int, ushort> nodes, NetInfo info, int k0, Vector3 p0, int k1, Vector3 p1, out ushort seg)
        {
            seg = 0;
            if (!GetNode(nodes, k0, p0, info, out ushort n0) || !GetNode(nodes, k1, p1, info, out ushort n1)) return false;
            Vector3 dir = p1 - p0;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return false;
            dir.Normalize();
            return CreateSegment(out seg, info, n0, n1, dir, -dir);
        }

        /// <summary>セグメントが付かなかったノードを消す</summary>
        public static void CleanupNodes(Dictionary<int, ushort> nodes)
        {
            var nm = NetManager.instance;
            foreach (var n in nodes.Values)
                if ((nm.m_nodes.m_buffer[n].m_flags & NetNode.Flags.Created) != 0 && nm.m_nodes.m_buffer[n].CountSegments() == 0)
                    nm.ReleaseNode(n);
        }

        public static void Release(List<ushort> segs, List<string> names)
        {
            var nm = NetManager.instance;
            for (int i = 0; i < segs.Count; i++)
                if (IsOurs(segs[i], names[i]))
                    nm.ReleaseSegment(segs[i], false); // 孤立したノードも一緒に消える
        }

        public static bool IsOurs(ushort seg, string name)
        {
            var s = NetManager.instance.m_segments.m_buffer[seg];
            return (s.m_flags & NetSegment.Flags.Created) != 0 && s.Info != null && s.Info.name == name;
        }

        // ================= 園路 =================

        /// <summary>
        /// 園路(ゲート→広場の各線)と広場の周回路を作る。周回路の点に終わる園路はノードを共有してつなぐ
        /// </summary>
        public static List<ushort> BuildPaths(NetInfo info, List<Vector2[]> paths, Vector2[] ring)
        {
            var ctx = new PathCtx { Info = info, Ring = ring };
            if (info == null || !Resolve()) return ctx.Result;

            if (ring != null && ring.Length >= 3)
                for (int i = 0; i < ring.Length; i++)
                    ctx.Line(ring[i], PathCtx.RingKey + i, ring[(i + 1) % ring.Length], PathCtx.RingKey + (i + 1) % ring.Length);

            foreach (var path in paths)
            {
                if (path == null || path.Length < 2) continue;
                int prevKey = ctx.KeyOf(path[0]);
                for (int i = 1; i < path.Length; i++)
                {
                    int key = ctx.KeyOf(path[i]);
                    ctx.Line(path[i - 1], prevKey, path[i], key);
                    prevKey = key;
                }
            }

            CleanupNodes(ctx.Nodes);
            Debug.Log($"[PlaygroundMod] paths '{info.name}': created={ctx.Result.Count} failed={ctx.Failed}");
            return ctx.Result;
        }

        class PathCtx
        {
            public const int RingKey = 1000000;
            public NetInfo Info;
            public Vector2[] Ring;
            public readonly List<ushort> Result = new List<ushort>();
            public readonly Dictionary<int, ushort> Nodes = new Dictionary<int, ushort>();
            public int NextKey;
            public int Failed;

            /// <summary>周回路の点と同じ位置ならそのノードを共有</summary>
            public int KeyOf(Vector2 p)
            {
                if (Ring != null)
                    for (int i = 0; i < Ring.Length; i++)
                        if ((Ring[i] - p).sqrMagnitude < 0.01f) return RingKey + i;
                return NextKey++;
            }

            /// <summary>a→b を地形に沿って区切ってつなぐ</summary>
            public void Line(Vector2 a, int ka, Vector2 b, int kb)
            {
                if ((b - a).magnitude < 1f) return;
                Polyline(Nodes, Info, a, ka, b, kb, ref NextKey, Result, ref Failed);
            }
        }

        // ================= CreateSegment(バージョン差吸収) =================
        // 旧: (out seg, ref rnd, NetInfo, startNode, endNode, startDir, endDir, buildIndex, modifiedIndex, invert)
        // 新: (out seg, ref rnd, NetInfo, TreeInfo, startNode, endNode, startDir, endDir, buildIndex, modifiedIndex, invert)

        public static bool Resolve()
        {
            if (s_resolved) return s_createSegment != null;
            s_resolved = true;
            s_createSegment = typeof(NetManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "CreateSegment")
                .FirstOrDefault(m => m.GetParameters().Length == 11 || m.GetParameters().Length == 10);
            if (s_createSegment == null)
                Debug.LogError("[PlaygroundMod] NetManager.CreateSegment not found; networks disabled");
            else
            {
                s_createSegmentArgs = s_createSegment.GetParameters().Length;
                Debug.Log("[PlaygroundMod] CreateSegment: " + s_createSegment);
            }
            return s_createSegment != null;
        }

        static bool CreateSegment(out ushort seg, NetInfo info, ushort n0, ushort n1, Vector3 d0, Vector3 d1)
        {
            seg = 0;
            if (!Resolve()) return false;
            var sm = SimulationManager.instance;
            uint idx = sm.m_currentBuildIndex;
            object[] args = s_createSegmentArgs == 11
                ? new object[] { (ushort)0, sm.m_randomizer, info, null, n0, n1, d0, d1, idx, idx, false }
                : new object[] { (ushort)0, sm.m_randomizer, info, n0, n1, d0, d1, idx, idx, false };
            try
            {
                bool ok = (bool)s_createSegment.Invoke(NetManager.instance, args);
                sm.m_randomizer = (Randomizer)args[1];
                if (!ok) return false;
                seg = (ushort)args[0];
                sm.m_currentBuildIndex++;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PlaygroundMod] CreateSegment failed: " + e);
                return false;
            }
        }
    }
}
