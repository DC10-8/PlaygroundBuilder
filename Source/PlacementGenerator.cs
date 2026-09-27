using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PlaygroundMod
{
    public struct Placement
    {
        public PropInfo Info;
        public Vector3 Position;
        public float Angle;
        public float Radius;
    }

    /// <summary>
    /// 範囲内にPropをランダム配置(重なり・はみ出し・道路/建物との衝突を回避)。
    /// 順番: ゲート位置決め → トイレ(外周・内向き) → ベンチ(外周・内向き) → 遊具(残りの空間) → 柵(外周、ゲート部分は空ける)
    /// </summary>
    public static class PlacementGenerator
    {
        static MethodInfo s_check;
        static bool s_checkResolved;
        static readonly ulong[] s_segBuf = new ulong[576];   // 36864 segments / 64
        static readonly ulong[] s_bldBuf = new ulong[768];   // 49152 buildings / 64

        /// <param name="fence">柵Prop(ネットワークを使う場合はnull)</param>
        /// <param name="fenceNetwork">フェンスのネットワークを後で敷く(ゲートだけ決める)</param>
        public static List<Placement> Generate(PlaygroundZone zone, List<CatalogEntry> play,
                                               List<CatalogEntry> benches, CatalogEntry toilet, CatalogEntry fence,
                                               bool fenceNetwork, System.Random rng, out List<Gate> gates)
        {
            var result = new List<Placement>();
            var s = ModSettings.Instance;
            gates = new List<Gate>();

            if (fence != null || fenceNetwork)
            {
                int gateCount = s.GateCount > 0 ? s.GateCount : Mathf.Clamp(Mathf.RoundToInt(zone.Perimeter / 120f), 1, 4);
                gates = ComputeGates(zone, rng, gateCount, s.GateWidth);
                // ゲートの内側にはベンチや遊具を置かない(通り道を塞がない)ためのダミー
                foreach (var g in gates)
                {
                    Vector2 c = g.Pos + g.Inward * (s.GateWidth * 0.5f + 0.5f);
                    result.Add(new Placement { Info = null, Position = new Vector3(c.x, 0f, c.y), Radius = s.GateWidth * 0.5f + 1f });
                }
            }

            if (toilet != null)
                TryEdge(zone, result, toilet.Info, rng, 80);

            if (benches.Count > 0 && s.BenchDensity > 0f)
            {
                int n = Mathf.Clamp(Mathf.RoundToInt(zone.Perimeter / 35f * s.BenchDensity), 1, 16);
                for (int i = 0; i < n; i++)
                    TryEdge(zone, result, benches[rng.Next(benches.Count)].Info, rng, 30);
            }

            PlacePlay(zone, play, rng, result);
            result.RemoveAll(p => p.Info == null);

            if (fence != null && gates.Count > 0)
                PlaceFence(zone, fence.Info, gates, s.GateWidth, result);
            return result;
        }

        // ================= 柵 =================

        /// <summary>外周を等間隔に分けてゲートを置く(角は避ける)</summary>
        public static List<Gate> ComputeGates(PlaygroundZone zone, System.Random rng, int n, float gateWidth)
        {
            var gates = new List<Gate>();
            float offset = (float)rng.NextDouble() * zone.Perimeter;
            for (int k = 0; k < n; k++)
            {
                float d = offset + k * zone.Perimeter / n;
                if (zone.PointAtDistance(d, gateWidth * 0.5f + 1f, out Vector2 p, out Vector2 inward))
                    gates.Add(new Gate { Pos = p, Inward = inward });
            }
            return gates;
        }

        /// <summary>各辺に柵Propを敷き詰める。長辺方向を辺に合わせ、ゲートにかかる区間は空ける</summary>
        public static void PlaceFence(PlaygroundZone zone, PropInfo info, List<Gate> gates, float gateWidth, List<Placement> result)
        {
            Vector3 size = PropCatalog.GetSize(info);
            bool alongX = size.x >= size.z;
            float pieceLen = Mathf.Max(alongX ? size.x : size.z, 0.5f);
            float halfGate = gateWidth * 0.5f;

            var poly = zone.Polygon;
            for (int e = 0; e < poly.Length; e++)
            {
                Vector2 a = poly[e], b = poly[(e + 1) % poly.Length];
                float len = (b - a).magnitude;
                if (len < 0.5f) continue;
                Vector2 dir = (b - a) / len;

                int count = Mathf.Max(1, Mathf.RoundToInt(len / pieceLen));
                float step = len / count;

                // Prop回転 q=AngleAxis(θ, down): right=(cosθ, 0, sinθ), forward=(−sinθ, 0, cosθ)
                float angle = alongX ? Mathf.Atan2(dir.y, dir.x) : Mathf.Atan2(-dir.x, dir.y);
                if (angle < 0f) angle += Mathf.PI * 2f;

                for (int i = 0; i < count; i++)
                {
                    Vector2 c = a + dir * (step * (i + 0.5f));
                    Vector2 s0 = c - dir * (step * 0.5f), s1 = c + dir * (step * 0.5f);

                    bool inGate = false;
                    foreach (var g in gates)
                        if (DistToSegment(g.Pos, s0, s1) < halfGate) { inGate = true; break; }
                    if (inGate) continue;

                    Vector3 pos = new Vector3(c.x, 0f, c.y);
                    pos.y = TerrainManager.instance.SampleDetailHeight(pos);
                    // 道路に重なる区間は置かない(建物の敷地は設定でONの時だけ避ける)
                    if (!PlacementOk(info, pos, !ModSettings.Instance.FenceAvoidBuildings)) continue;

                    result.Add(new Placement { Info = info, Position = pos, Angle = angle, Radius = 0f });
                }
            }
        }

        static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (a + ab * t - p).magnitude;
        }

        /// <summary>外周沿いに、前面を園内に向けて置く</summary>
        static bool TryEdge(PlaygroundZone zone, List<Placement> placed, PropInfo info, System.Random rng, int attempts)
        {
            Vector3 size = PropCatalog.GetSize(info);
            float radius = Mathf.Max(size.x, size.z) * 0.5f;
            float depth = size.z * 0.5f + 0.8f;
            bool flip = ModSettings.Instance.AmenityFrontIsMinusZ;

            for (int a = 0; a < attempts; a++)
            {
                if (!zone.RandomEdgePoint(rng, out Vector2 p, out Vector2 inward)) continue;
                Vector2 c = p + inward * depth;
                Vector2 front = flip ? -inward : inward;
                // Prop回転 q=AngleAxis(θ, down) で forward=(−sinθ, 0, cosθ) → θ = atan2(−fx, fz)
                float angle = Mathf.Atan2(-front.x, front.y);
                if (angle < 0f) angle += Mathf.PI * 2f;

                if (TryAccept(zone, placed, info, size, radius, c.x, c.y, angle, out Placement pl))
                {
                    placed.Add(pl);
                    return true;
                }
            }
            return false;
        }

        static void PlacePlay(PlaygroundZone zone, List<CatalogEntry> set, System.Random rng, List<Placement> placed)
        {
            if (set.Count == 0) return;

            int target = Mathf.Clamp(Mathf.RoundToInt(zone.Area / 150f * ModSettings.Instance.Density), 1, 60);
            Rect b = zone.BoundsXZ;
            int cursor = 0, fails = 0, count = 0;

            for (int n = 0; n < target * 40 && count < target; n++)
            {
                // まず全種類を1つずつ置き、その後はランダム
                bool coverage = cursor < set.Count;
                CatalogEntry entry = coverage ? set[cursor] : set[rng.Next(set.Count)];

                Vector3 size = PropCatalog.GetSize(entry.Info);
                float radius = Mathf.Max(size.x, size.z) * 0.5f;
                float x = b.xMin + (float)rng.NextDouble() * b.width;
                float z = b.yMin + (float)rng.NextDouble() * b.height;
                float angle = (float)(rng.NextDouble() * Math.PI * 2);

                if (TryAccept(zone, placed, entry.Info, size, radius, x, z, angle, out Placement p))
                {
                    placed.Add(p);
                    count++;
                    if (coverage) cursor++;
                    fails = 0;
                }
                else if (coverage && ++fails > 40)
                {
                    cursor++; // この種類は大きすぎて入らない
                    fails = 0;
                }
            }
        }

        static bool TryAccept(PlaygroundZone zone, List<Placement> placed, PropInfo info, Vector3 size,
                              float radius, float x, float z, float angle, out Placement p)
        {
            p = default(Placement);
            if (!zone.ContainsFootprint(new Vector2(x, z), size, angle, 0.3f)) return false;

            foreach (var o in placed)
            {
                float dx = o.Position.x - x, dz = o.Position.z - z;
                float min = radius + o.Radius + 1.5f; // 間に通路を確保
                if (dx * dx + dz * dz < min * min) return false;
            }

            Vector3 pos = new Vector3(x, 0f, z);
            pos.y = TerrainManager.instance.SampleDetailHeight(pos);
            if (!PlacementOk(info, pos)) return false;

            p = new Placement { Info = info, Position = pos, Angle = angle, Radius = radius };
            return true;
        }

        // PropTool.CheckPlacementErrors(PropInfo, Vector3, bool, ushort, ulong[], ulong[]) をリフレクションで呼ぶ
        // (バージョン差でシグネチャが違う場合はチェック無しで通す)
        /// <param name="allowBuildings">trueなら建物との衝突は無視し、道路などネットワークとの衝突だけ見る</param>
        public static bool PlacementOk(PropInfo info, Vector3 pos, bool allowBuildings = false)
        {
            if (!s_checkResolved)
            {
                s_checkResolved = true;
                s_check = typeof(PropTool).GetMethod("CheckPlacementErrors",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (s_check != null && s_check.GetParameters().Length != 6) s_check = null;
                if (s_check == null) Debug.LogWarning("[PlaygroundMod] PropTool.CheckPlacementErrors not found; collision check disabled");
            }
            if (s_check == null) return true;
            try
            {
                Array.Clear(s_segBuf, 0, s_segBuf.Length);
                Array.Clear(s_bldBuf, 0, s_bldBuf.Length);
                var r = (ToolBase.ToolErrors)s_check.Invoke(null, new object[] { info, pos, false, (ushort)0, s_segBuf, s_bldBuf });
                if (r == ToolBase.ToolErrors.None) return true;
                if (!allowBuildings) return false;
                // 衝突が「物体」だけで、ぶつかったネットワークが無い(=建物だけ)ならOK
                if ((r & ~ToolBase.ToolErrors.ObjectCollision) != ToolBase.ToolErrors.None) return false;
                foreach (ulong bits in s_segBuf) if (bits != 0) return false;
                return true;
            }
            catch { return true; }
        }
    }
}
