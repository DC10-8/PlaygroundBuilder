using System.Collections.Generic;
using UnityEngine;

namespace PlaygroundMod
{
    /// <summary>
    /// フェンスのネットワーク(NetInfo)をエリア外周に敷く。
    /// 角ごとにノードを共有してつなぎ、ゲート部分はノードで切って空ける。
    /// 道路・建物に重なる所は、重なっている数mだけ空ける(辺ごと捨てない)。
    /// </summary>
    public static class FenceBuilder
    {
        const float MinSegLen = 2f;    // これより短い区間は作らない
        const float SampleStep = 2f;   // 重なり判定の刻み

        /// <summary>辺上の一続きの区間(ゲートで切った後)。K0/K1はノード共有用キー</summary>
        struct Span
        {
            public Vector2 A, Dir;
            public float T0, T1;
            public int K0, K1;
        }

        class Stats
        {
            public int Spans, Pieces, Created, Failed, BlockedRoad, BlockedBuilding;
        }

        public static List<ushort> Build(PlaygroundZone zone, NetInfo info, List<Gate> gates, float gateWidth)
        {
            var result = new List<ushort>();
            if (!NetBuilder.Resolve()) return result;

            var st = new Stats();
            int nextKey = zone.Polygon.Length; // 0..n-1 は角のノード
            var spans = MakeSpans(zone.Polygon, gates, gateWidth, ref nextKey);
            st.Spans = spans.Count;

            bool avoid = ModSettings.Instance.FenceAvoidObstacles;
            var nodes = new Dictionary<int, ushort>();

            foreach (var sp in spans)
            {
                var intervals = avoid ? FreeIntervals(sp, st) : new List<Vector2> { new Vector2(sp.T0, sp.T1) };
                foreach (var iv in intervals)
                {
                    int k0 = iv.x <= sp.T0 + 0.01f ? sp.K0 : nextKey++;
                    int k1 = iv.y >= sp.T1 - 0.01f ? sp.K1 : nextKey++;

                    // 地形に沿って区切りながらつなぐ
                    int before = result.Count, failed = 0;
                    NetBuilder.Polyline(nodes, info, sp.A + sp.Dir * iv.x, k0, sp.A + sp.Dir * iv.y, k1,
                                        ref nextKey, result, ref failed);
                    st.Created += result.Count - before;
                    st.Failed += failed;
                    st.Pieces += result.Count - before + failed;
                }
            }

            NetBuilder.CleanupNodes(nodes);

            Debug.Log($"[PlaygroundMod] fence '{info.name}' zone#{zone.Id}: edges={zone.Polygon.Length} gates={gates.Count} " +
                      $"spans={st.Spans} pieces={st.Pieces} created={st.Created} failed={st.Failed} " +
                      $"blockedSamples(road={st.BlockedRoad}, building={st.BlockedBuilding})");
            return result;
        }

        /// <summary>各辺をゲートで切った区間リスト</summary>
        static List<Span> MakeSpans(Vector2[] poly, List<Gate> gates, float gateWidth, ref int nextKey)
        {
            var spans = new List<Span>();
            int n = poly.Length;
            float half = gateWidth * 0.5f;

            for (int e = 0; e < n; e++)
            {
                Vector2 a = poly[e], b = poly[(e + 1) % n];
                float len = (b - a).magnitude;
                if (len < 0.5f) continue;
                Vector2 dir = (b - a) / len;

                var cuts = new List<KeyValuePair<float, float>>();
                foreach (var g in gates)
                {
                    float t = Vector2.Dot(g.Pos - a, dir);
                    if (t < 0f || t > len) continue;
                    if ((a + dir * t - g.Pos).sqrMagnitude > 0.25f) continue;
                    cuts.Add(new KeyValuePair<float, float>(Mathf.Max(0f, t - half), Mathf.Min(len, t + half)));
                }
                cuts.Sort((x, y) => x.Key.CompareTo(y.Key));

                float cur = 0f;
                int curKey = e;
                foreach (var c in cuts)
                {
                    if (c.Key > cur + MinSegLen)
                        spans.Add(new Span { A = a, Dir = dir, T0 = cur, T1 = c.Key, K0 = curKey, K1 = nextKey++ });
                    cur = Mathf.Max(cur, c.Value);
                    curKey = nextKey++;
                }
                if (len > cur + MinSegLen)
                    spans.Add(new Span { A = a, Dir = dir, T0 = cur, T1 = len, K0 = curKey, K1 = (e + 1) % n });
            }
            return spans;
        }

        /// <summary>SampleStepごとに重なりを調べ、重なっていない一続きの区間を返す(建物の敷地は設定でONの時だけ避ける)</summary>
        static List<Vector2> FreeIntervals(Span sp, Stats st)
        {
            var list = new List<Vector2>();
            float len = sp.T1 - sp.T0;
            int count = Mathf.Max(1, Mathf.CeilToInt(len / SampleStep));
            float step = len / count;
            bool avoidBuildings = ModSettings.Instance.FenceAvoidBuildings;

            float start = -1f;
            for (int i = 0; i <= count; i++)
            {
                float t = sp.T0 + step * i;
                Vector2 p = sp.A + sp.Dir * t;
                bool blocked = false;
                if (HitsNetwork(p)) { blocked = true; st.BlockedRoad++; }
                else if (avoidBuildings && HitsBuilding(p)) { blocked = true; st.BlockedBuilding++; }

                if (!blocked && start < 0f) start = t;
                if (blocked && start >= 0f)
                {
                    float end = t - step; // 重なり直前のサンプルまで
                    if (end - start >= MinSegLen) list.Add(new Vector2(start, end));
                    start = -1f;
                }
            }
            if (start >= 0f && sp.T1 - start >= MinSegLen) list.Add(new Vector2(start, sp.T1));
            return list;
        }

        // ================= 重なり判定 =================
        // 「中に入り込んでいる」ときだけ重なりとみなす(道路の歩道の外縁や、敷地の境界線上はOK)

        const float Tolerance = 0.5f;

        static bool HitsNetwork(Vector2 p2)
        {
            var p = new Vector3(p2.x, 0f, p2.y);
            var nm = NetManager.instance;
            int gx = Mathf.Clamp((int)(p.x / 64f + 135f), 0, 269);
            int gz = Mathf.Clamp((int)(p.z / 64f + 135f), 0, 269);
            for (int z = Mathf.Max(gz - 1, 0); z <= Mathf.Min(gz + 1, 269); z++)
                for (int x = Mathf.Max(gx - 1, 0); x <= Mathf.Min(gx + 1, 269); x++)
                {
                    ushort s = nm.m_segmentGrid[z * 270 + x];
                    int guard = 0;
                    while (s != 0)
                    {
                        var seg = nm.m_segments.m_buffer[s];
                        NetInfo info = seg.Info;
                        if ((seg.m_flags & NetSegment.Flags.Created) != 0 && info != null && !PropCatalog.IsFenceNet(info))
                        {
                            seg.GetClosestPositionAndDirection(p, out Vector3 pos, out _);
                            float dx = pos.x - p.x, dz = pos.z - p.z;
                            float r = info.m_halfWidth - Tolerance;
                            if (r > 0f && dx * dx + dz * dz < r * r) return true;
                        }
                        s = seg.m_nextGridSegment;
                        if (++guard > 36864) break;
                    }
                }
            return false;
        }

        static bool HitsBuilding(Vector2 p)
        {
            var bm = BuildingManager.instance;
            int gx = Mathf.Clamp((int)(p.x / 64f + 135f), 0, 269);
            int gz = Mathf.Clamp((int)(p.y / 64f + 135f), 0, 269);
            for (int z = Mathf.Max(gz - 1, 0); z <= Mathf.Min(gz + 1, 269); z++)
                for (int x = Mathf.Max(gx - 1, 0); x <= Mathf.Min(gx + 1, 269); x++)
                {
                    ushort b = bm.m_buildingGrid[z * 270 + x];
                    int guard = 0;
                    while (b != 0)
                    {
                        var bd = bm.m_buildings.m_buffer[b];
                        if ((bd.m_flags & Building.Flags.Created) != 0 && bd.Info != null)
                        {
                            float dx = p.x - bd.m_position.x, dz = p.y - bd.m_position.z;
                            float c = Mathf.Cos(bd.m_angle), s = Mathf.Sin(bd.m_angle);
                            float lx = dx * c + dz * s;      // 建物のright方向
                            float lz = -dx * s + dz * c;     // 建物のforward方向
                            float hw = bd.Width * 4f - Tolerance, hl = bd.Length * 4f - Tolerance;
                            if (Mathf.Abs(lx) < hw && Mathf.Abs(lz) < hl) return true;
                        }
                        b = bd.m_nextGridBuilding;
                        if (++guard > 49152) break;
                    }
                }
            return false;
        }
    }
}
