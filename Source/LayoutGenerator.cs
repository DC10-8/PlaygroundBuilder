using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PlaygroundMod
{
    public enum ParkZoneType { Active, Toddler, Water, Lawn }

    public class LayoutResult
    {
        public readonly List<Placement> Props = new List<Placement>();
        public readonly List<KeyValuePair<TreeInfo, Vector3>> Trees = new List<KeyValuePair<TreeInfo, Vector3>>();
        public readonly List<Vector2[]> Paths = new List<Vector2[]>();   // Gatesと同じ並び (ゲート→広場)
        public Vector2[] Ring = new Vector2[0];                          // 広場の周回路
        public readonly List<Gate> Gates = new List<Gate>();
        public float GateWidth;
    }

    /// <summary>
    /// 公園レイアウト:
    ///  - 範囲の一番広い所に広場(中央にシェルター、周りにベンチ)
    ///  - 各入口から広場へ園路、広場の周りに周回路
    ///  - 園路で区切られた区画を役割別ゾーンに割り当て
    ///      アクティブ(大型複合遊具+遊具+日陰棚とベンチ) / 幼児用(ブランコ+小型遊具) / 水遊び・砂場 / 芝生(何も置かない)
    ///  - 入口の脇にトイレと案内板、広場の近くにピクニックテーブルと水飲み
    ///  - 外周に木を植える
    /// </summary>
    public class LayoutGenerator
    {
        const int PlazaSector = -1;   // 広場の中
        const int AnySector = -2;     // 広場の外ならどの区画でも

        readonly PlaygroundZone m_zone;
        readonly System.Random m_rng;
        readonly ICollection<string> m_avoid;
        readonly HashSet<string> m_avoidHere;   // 前回の公園 + この公園で既に使ったもの
        readonly ModSettings m_s;
        readonly LayoutResult m_res = new LayoutResult();
        readonly List<Placement> m_placed = new List<Placement>();   // Info==null は通り道確保用のダミー
        readonly List<KeyValuePair<Vector2, Vector2>> m_corridors = new List<KeyValuePair<Vector2, Vector2>>();
        readonly bool m_flip;

        Vector2 m_c;           // 広場の中心
        float m_rp;            // 広場の半径 (0なら広場なし)
        float m_pathHalf;
        float m_treeBand;      // 外周から空ける幅(木を植える帯)
        float[] m_bounds;      // 区画の境界角 (昇順, 0..2π)
        List<Vector2>[] m_samples;
        Vector2[] m_sectorCenter;
        ParkZoneType[] m_types;

        CatalogEntry m_bench;  // 公園全体で同じベンチ

        public static LayoutResult Generate(PlaygroundZone zone, System.Random rng, ICollection<string> avoid, float pathHalfWidth)
        {
            var g = new LayoutGenerator(zone, rng, avoid, pathHalfWidth);
            g.Run();
            return g.m_res;
        }

        LayoutGenerator(PlaygroundZone zone, System.Random rng, ICollection<string> avoid, float pathHalfWidth)
        {
            m_zone = zone; m_rng = rng; m_avoid = avoid; m_s = ModSettings.Instance;
            m_avoidHere = new HashSet<string>(avoid ?? new string[0]);
            m_pathHalf = pathHalfWidth;
            m_treeBand = m_s.TreeRing ? 5f : 1.5f;
            m_flip = m_s.AmenityFrontIsMinusZ;
        }

        void Run()
        {
            FindCenter();
            MakeGatesAndPaths();
            MakeSectors();

            m_bench = m_s.BenchDensity > 0f ? PropCatalog.PickOne(m_rng, PlayKind.Bench, m_avoidHere) : null;

            PlaceGateFacilities();
            PlacePlaza();
            for (int i = 0; i < m_types.Length; i++)
            {
                switch (m_types[i])
                {
                    case ParkZoneType.Active: FillActive(i); break;
                    case ParkZoneType.Toddler: FillToddler(i); break;
                    case ParkZoneType.Water: FillWater(i); break;
                    default: FillLawn(i); break;
                }
            }
            PlacePicnicAndDrink();
            PlantTrees();

            m_res.Props.AddRange(m_placed.Where(p => p.Info != null));
            Debug.Log($"[PlaygroundMod] layout zone#{m_zone.Id}: area={m_zone.Area:0} plazaR={m_rp:0.0} gates={m_res.Gates.Count} " +
                      $"zones=[{string.Join(",", m_types.Select((t, i) => $"{t}:{m_samples[i].Count * 4}m2").ToArray())}] " +
                      $"props={m_res.Props.Count} trees={m_res.Trees.Count}");
        }

        // ================= 骨格 =================

        /// <summary>境界から一番遠い点(=一番広い所)を広場の中心にする</summary>
        void FindCenter()
        {
            Rect b = m_zone.BoundsXZ;
            float step = Mathf.Max(2f, Mathf.Sqrt(m_zone.Area) / 30f);
            float bestD = -1f;
            m_c = m_zone.Centroid;
            for (float x = b.xMin + step * 0.5f; x < b.xMax; x += step)
                for (float z = b.yMin + step * 0.5f; z < b.yMax; z += step)
                {
                    var p = new Vector2(x, z);
                    if (!m_zone.Contains(p)) continue;
                    float d = m_zone.DistToBoundary(p) - (p - m_zone.Centroid).magnitude * 0.05f; // 同程度なら中心寄り
                    if (d > bestD) { bestD = d; m_c = p; }
                }

            float dmax = m_zone.DistToBoundary(m_c);
            float R = Mathf.Sqrt(m_zone.Area / Mathf.PI);
            m_rp = Mathf.Min(Mathf.Clamp(R * 0.2f, 4f, 14f), dmax * 0.4f);
            if (m_rp < 3f) m_rp = 0f; // 狭すぎるので広場なし
        }

        void MakeGatesAndPaths()
        {
            int count = m_s.GateCount > 0 ? m_s.GateCount : Mathf.Clamp(Mathf.RoundToInt(m_zone.Perimeter / 100f), 2, 4);
            m_res.GateWidth = Mathf.Max(m_s.GateWidth, m_pathHalf * 2f + 1f);
            m_res.Gates.AddRange(ChooseGates(count, m_res.GateWidth));

            var gateAngles = m_res.Gates.Select(g => AngleOf(g.Pos)).ToList();

            // 広場の周回路: 園路の接続点 + 45°ごとの点(接続点に近いものは省く)
            if (m_rp > 0f)
            {
                var angles = new List<float>(gateAngles);
                float baseA = gateAngles.Count > 0 ? gateAngles[0] : 0f;
                for (int k = 0; k < 8; k++)
                {
                    float a = Mathf.Repeat(baseA + k * Mathf.PI / 4f, Mathf.PI * 2f);
                    if (gateAngles.All(g => AngleDiff(a, g) > 15f * Mathf.Deg2Rad)) angles.Add(a);
                }
                angles.Sort();
                m_res.Ring = angles.Select(a => m_c + Dir(a) * m_rp).ToArray();
                for (int i = 0; i < m_res.Ring.Length; i++)
                    m_corridors.Add(new KeyValuePair<Vector2, Vector2>(m_res.Ring[i], m_res.Ring[(i + 1) % m_res.Ring.Length]));
            }

            // 園路: ゲート → 広場の周回路(広場が無ければ中心)
            for (int i = 0; i < m_res.Gates.Count; i++)
            {
                Vector2 start = m_res.Gates[i].Pos;
                Vector2 end = m_rp > 0f ? m_c + Dir(gateAngles[i]) * m_rp : m_c;
                m_res.Paths.Add(new[] { start, end });
                m_corridors.Add(new KeyValuePair<Vector2, Vector2>(start, end));
            }
        }

        /// <summary>
        /// 外周を等間隔に分けたゲート位置を、ずらし方を変えて何通りか試し、角から離れた(辺の中ほどの)ものを選ぶ。
        /// 上位数案からランダムに選んで、置き直すたびに入口の位置も変わるようにする
        /// </summary>
        List<Gate> ChooseGates(int count, float gateWidth)
        {
            const int Trials = 36;
            var cands = new List<KeyValuePair<float, List<Gate>>>();
            for (int k = 0; k < Trials; k++)
            {
                float offset = k * m_zone.Perimeter / count / Trials;
                var gates = new List<Gate>();
                for (int i = 0; i < count; i++)
                {
                    float d = offset + i * m_zone.Perimeter / count;
                    if (m_zone.PointAtDistance(d, gateWidth * 0.5f + 1f, out Vector2 p, out Vector2 inward))
                        gates.Add(new Gate { Pos = p, Inward = inward });
                }
                if (gates.Count == 0) continue;
                float score = gates.Min(g => m_zone.Polygon.Min(v => (v - g.Pos).magnitude)) + gates.Count * 1000f;
                cands.Add(new KeyValuePair<float, List<Gate>>(score, gates));
            }
            if (cands.Count == 0) return new List<Gate>();
            var top = cands.OrderByDescending(c => c.Key).Take(3).ToList();
            float best = top[0].Key;
            top = top.Where(c => c.Key >= best * 0.8f).ToList();
            return top[m_rng.Next(top.Count)].Value;
        }

        /// <summary>園路の角度で区画に分け、足りなければ広い区画を半分に割って4区画以上にする</summary>
        void MakeSectors()
        {
            var bounds = m_res.Gates.Select(g => AngleOf(g.Pos)).ToList();
            if (bounds.Count == 0) bounds.Add(0f);
            bounds.Sort();
            while (bounds.Count < 4)
            {
                int widest = 0; float widestW = -1f;
                for (int i = 0; i < bounds.Count; i++)
                {
                    float w = SectorWidth(bounds, i);
                    if (w > widestW) { widestW = w; widest = i; }
                }
                if (widestW < 60f * Mathf.Deg2Rad) break;
                bounds.Add(Mathf.Repeat(bounds[widest] + widestW * 0.5f, Mathf.PI * 2f));
                bounds.Sort();
            }
            m_bounds = bounds.ToArray();

            int n = m_bounds.Length;
            m_samples = new List<Vector2>[n];
            for (int i = 0; i < n; i++) m_samples[i] = new List<Vector2>();

            Rect b = m_zone.BoundsXZ;
            const float step = 2f;
            for (float x = b.xMin + 1f; x < b.xMax; x += step)
                for (float z = b.yMin + 1f; z < b.yMax; z += step)
                {
                    var p = new Vector2(x, z);
                    if (!m_zone.Contains(p) || m_zone.DistToBoundary(p) < 1f) continue;
                    if (m_rp > 0f && (p - m_c).magnitude < m_rp + m_pathHalf + 1f) continue;
                    if (CorridorDist(p) < m_pathHalf + 0.5f) continue;
                    m_samples[SectorOf(p)].Add(p);
                }

            m_sectorCenter = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var s = m_samples[i];
                m_sectorCenter[i] = s.Count > 0 ? s.Aggregate(Vector2.zero, (acc, v) => acc + v) / s.Count : m_c;
            }

            // 広い区画から アクティブ → 幼児用 → 水遊び → 芝生、残りは芝生。狭すぎる区画も芝生
            m_types = new ParkZoneType[n];
            var order = Enumerable.Range(0, n).OrderByDescending(i => m_samples[i].Count).ToList();
            var plan = new[] { ParkZoneType.Active, ParkZoneType.Toddler, ParkZoneType.Water, ParkZoneType.Lawn };
            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                m_types[i] = k < plan.Length && m_samples[i].Count >= 30 ? plan[k] : ParkZoneType.Lawn;
            }
        }

        // ================= 入口・広場 =================

        void PlaceGateFacilities()
        {
            var gates = m_res.Gates;
            if (gates.Count == 0) return;

            // トイレ: 広い公園は入口2か所、そうでなければ1か所
            var toilet = m_s.PlaceToilet && m_zone.Area >= m_s.ToiletMinArea ? PropCatalog.PickOne(m_rng, PlayKind.Toilet, m_avoidHere) : null;
            int toilets = toilet == null ? 0 : (m_zone.Area > 3000f && gates.Count >= 2 ? 2 : 1);
            var toiletGates = new List<int> { 0 };
            if (toilets == 2)
                toiletGates.Add(Enumerable.Range(1, gates.Count - 1).OrderByDescending(i => (gates[i].Pos - gates[0].Pos).sqrMagnitude).First());

            var sign = PropCatalog.PickOne(m_rng, PlayKind.Sign, m_avoidHere);

            for (int gi = 0; gi < gates.Count; gi++)
            {
                var g = gates[gi];
                Vector2 perp = new Vector2(-g.Inward.y, g.Inward.x);
                int side = m_rng.Next(2) == 0 ? 1 : -1;

                if (toilets > 0 && toiletGates.Contains(gi))
                {
                    float off = m_pathHalf + toilet.Size.x * 0.5f + 1.2f;
                    foreach (float d in new[] { 4f, 7f, 10f, 14f })
                    {
                        Vector2 p = g.Pos + g.Inward * (toilet.Size.z * 0.5f + d) + perp * (side * off);
                        if (TryPlace(toilet, p, AngleFront(-perp * side), AnySector, true)) break; // 入口を園路に向ける
                    }
                    side = -side; // 案内板は反対側
                }

                if (sign != null)
                {
                    Vector2 p = g.Pos + g.Inward * 2.5f + perp * (side * (m_pathHalf + sign.Size.x * 0.5f + 0.8f));
                    TryPlace(sign, p, AngleFront(-perp * side), AnySector, true);
                }
            }
        }

        void PlacePlaza()
        {
            if (m_rp <= 0f) return;
            float inner = m_rp - m_pathHalf - 0.8f; // 周回路の内側で使える半径
            float baseAngle = m_res.Gates.Count > 0 ? AngleOf(m_res.Gates[0].Pos) : 0f;

            // 中央のシェルター(入る大きさのもの)
            var shelter = PropCatalog.PickOne(m_rng,
                e => e.Kind == PlayKind.Shelter && Mathf.Max(e.Size.x, e.Size.z) * 0.5f <= inner, m_avoidHere);
            if (shelter != null)
                TryPlace(shelter, m_c, Geo.AngleAlongX(Dir(baseAngle)), PlazaSector, true);

            // 広場のベンチ: 園路の接続点の間に、中心を向けて
            if (m_bench == null) return;
            var joins = m_res.Gates.Select(g => AngleOf(g.Pos)).OrderBy(a => a).ToList();
            if (joins.Count == 0) joins.Add(0f);
            for (int i = 0; i < joins.Count; i++)
            {
                float a = joins[i] + SectorWidth(joins, i) * 0.5f;
                float r = inner - m_bench.Size.z * 0.5f - 0.3f;
                if (r <= 0f) continue;
                Vector2 p = m_c + Dir(a) * r;
                TryPlace(m_bench, p, AngleFront(m_c - p), PlazaSector, true, 0.6f);
            }
        }

        void PlacePicnicAndDrink()
        {
            // 芝生かアクティブの区画の、広場寄り
            int sector = Enumerable.Range(0, m_types.Length)
                .Where(i => m_types[i] == ParkZoneType.Lawn || m_types[i] == ParkZoneType.Active)
                .OrderByDescending(i => m_samples[i].Count).DefaultIfEmpty(-1).First();
            if (sector < 0) return;

            Vector2 target = m_c + (m_sectorCenter[sector] - m_c).normalized * (m_rp + m_pathHalf + 5f);

            var picnic = PropCatalog.PickOne(m_rng, PlayKind.Picnic, m_avoidHere);
            if (picnic != null)
            {
                int n = Mathf.Clamp(Mathf.RoundToInt(m_zone.Area / 2500f), 1, 3);
                for (int k = 0; k < n; k++)
                    PlaceNear(picnic, sector, target, v => Geo.AngleAlongX((m_c - v).normalized), false);
            }

            var drink = PropCatalog.PickOne(m_rng, PlayKind.Drink, m_avoidHere);
            if (drink != null)
                PlaceNear(drink, sector, target, v => AngleFront(m_c - v), false);
        }

        // ================= ゾーンごとの中身 =================

        int AreaCount(int sector, float perM2, int min, int max)
            => Mathf.Clamp(Mathf.RoundToInt(m_samples[sector].Count * 4f / perM2 * m_s.Density), min, max);

        void FillActive(int i)
        {
            // 大型複合遊具をゾーンの中心に
            var big = PropCatalog.PickOne(m_rng,
                e => (e.Kind == PlayKind.Generic || e.Kind == PlayKind.Climber || e.Kind == PlayKind.Slide) && e.Footprint >= 30f, m_avoidHere)
                ?? PropCatalog.Pick(m_rng, e => PropCatalog.IsPlayKind(e.Kind), 3, m_avoidHere, false).OrderByDescending(e => e.Footprint).FirstOrDefault();
            if (big != null) PlaceNear(big, i, m_sectorCenter[i], null, false);

            // 周りに動きのある遊具
            var others = PropCatalog.Pick(m_rng,
                e => (e.Kind == PlayKind.Slide || e.Kind == PlayKind.Climber || e.Kind == PlayKind.Spinner ||
                      e.Kind == PlayKind.Generic || e.Kind == PlayKind.Seesaw) && e != big,
                AreaCount(i, 300f, 1, 5), m_avoidHere, true);
            foreach (var e in others) PlaceNear(e, i, RandomSample(i), null, false);

            // 奥に日陰棚(パーゴラ)とベンチ
            Vector2 far = m_samples[i].Count > 0 ? m_samples[i].OrderByDescending(v => (v - m_c).sqrMagnitude).First() : m_sectorCenter[i];
            var shade = m_samples[i].Count * 4f > 400f ? PropCatalog.PickOne(m_rng, PlayKind.Shelter, m_avoidHere) : null;
            Vector2? shadePos = null;
            if (shade != null && PlaceNear(shade, i, Vector2.Lerp(m_sectorCenter[i], far, 0.7f), v => AngleFront(m_sectorCenter[i] - v), false, out Vector2 sp))
                shadePos = sp;

            if (m_bench != null)
            {
                if (shadePos.HasValue)
                    for (int k = 0; k < 2; k++)
                        PlaceNear(m_bench, i, shadePos.Value, v => AngleFront(m_sectorCenter[i] - v), true);
                EdgeBenches(i, AreaCount(i, 500f, 1, 4));
            }
        }

        void FillToddler(int i)
        {
            var swing = PropCatalog.PickOne(m_rng, PlayKind.Swing, m_avoidHere);
            if (swing != null) PlaceNear(swing, i, m_sectorCenter[i], null, false);

            // 小さめの遊具(スプリング遊具・シーソー・小さい滑り台など)
            var small = PropCatalog.Pick(m_rng,
                e => PropCatalog.IsPlayKind(e.Kind) && e.Kind != PlayKind.Swing && e.Kind != PlayKind.WaterPlay && e.Footprint < 40f,
                AreaCount(i, 200f, 2, 6), m_avoidHere, true);
            foreach (var e in small) PlaceNear(e, i, RandomSample(i), null, false);

            // 見守り用ベンチは園路側に
            if (m_bench != null) PathBenches(i, AreaCount(i, 400f, 1, 3));
        }

        void FillWater(int i)
        {
            var water = PropCatalog.Pick(m_rng, e => e.Kind == PlayKind.WaterPlay, 2, m_avoidHere, false);
            foreach (var e in water) PlaceNear(e, i, m_sectorCenter[i], null, false);

            var sand = PropCatalog.PickOne(m_rng, PlayKind.Sandbox, m_avoidHere);
            if (sand != null) PlaceNear(sand, i, water.Count > 0 ? RandomSample(i) : m_sectorCenter[i], null, false);

            var medium = PropCatalog.PickOne(m_rng,
                e => (e.Kind == PlayKind.Generic || e.Kind == PlayKind.Climber) && e.Footprint >= 10f && e.Footprint <= 150f, m_avoidHere);
            if (medium != null) PlaceNear(medium, i, RandomSample(i), null, false);

            if (m_bench != null) PathBenches(i, AreaCount(i, 400f, 1, 2));
        }

        void FillLawn(int i)
        {
            // 芝生は広く空けて、園路沿いにベンチだけ
            if (m_bench != null) PathBenches(i, AreaCount(i, 500f, 1, 3));
        }

        /// <summary>外周の木の帯の内側に、ゾーン中心を向けてベンチ</summary>
        void EdgeBenches(int i, int count)
        {
            var cands = m_samples[i].OrderBy(v => m_zone.DistToBoundary(v)).Take(200).ToList();
            PlaceAlong(i, cands, count);
        }

        /// <summary>園路沿いに、ゾーン中心を向けてベンチ</summary>
        void PathBenches(int i, int count)
        {
            var cands = m_samples[i].OrderBy(CorridorDist).Take(200).ToList();
            PlaceAlong(i, cands, count);
        }

        void PlaceAlong(int i, List<Vector2> cands, int count)
        {
            if (cands.Count == 0) return;
            int placed = 0;
            // 候補を飛び飛びに試して、ベンチが固まらないようにする
            int stride = Mathf.Max(1, cands.Count / Mathf.Max(1, count * 3));
            for (int k = m_rng.Next(stride); k < cands.Count && placed < count; k += stride)
            {
                Vector2 p = cands[k];
                if (TryPlace(m_bench, p, AngleFront(m_sectorCenter[i] - p), i, true, 4f)) placed++;
            }
        }

        // ================= 木 =================

        void PlantTrees()
        {
            if (!m_s.TreeRing) return;
            var species = PropCatalog.PickTrees(m_rng, m_avoid);
            if (species.Count == 0) return;

            float spacing = Mathf.Max(3f, m_s.TreeSpacing);
            for (float d = 0f; d < m_zone.Perimeter; d += spacing + (float)(m_rng.NextDouble() - 0.5) * spacing * 0.3f)
            {
                if (!m_zone.PointAtDistance(d, 2f, out Vector2 edge, out Vector2 inward)) continue;
                Vector2 p = edge + inward * 3f;
                if (!m_zone.Contains(p) || m_zone.DistToBoundary(p) < 2f) continue;
                if (m_res.Gates.Any(g => (g.Pos - p).magnitude < m_res.GateWidth * 0.5f + 4f)) continue;
                if (CorridorDist(p) < m_pathHalf + 2f) continue;
                if (m_placed.Any(o => (new Vector2(o.Position.x, o.Position.z) - p).magnitude < o.Radius + 2.5f)) continue;
                if (m_res.Trees.Any(t => (new Vector2(t.Value.x, t.Value.z) - p).magnitude < spacing * 0.6f)) continue;

                var info = species[m_rng.Next(species.Count)];
                m_res.Trees.Add(new KeyValuePair<TreeInfo, Vector3>(info, Geo.Ground(p)));
            }
        }

        // ================= 配置の共通処理 =================

        bool PlaceNear(CatalogEntry e, int sector, Vector2 target, Func<Vector2, float> angleAt, bool allowBand)
            => PlaceNear(e, sector, target, angleAt, allowBand, out _);

        /// <summary>targetに近い候補点から順に試す。angleAtが無ければ向きはランダム(4方向+揺らぎ)</summary>
        bool PlaceNear(CatalogEntry e, int sector, Vector2 target, Func<Vector2, float> angleAt, bool allowBand, out Vector2 pos)
        {
            pos = Vector2.zero;
            if (e == null) return false;
            IEnumerable<Vector2> cands = sector >= 0
                ? m_samples[sector]
                : m_samples.SelectMany(s => s);
            foreach (var p in cands.OrderBy(v => (v - target).sqrMagnitude).Take(250))
            {
                if (angleAt != null)
                {
                    if (TryPlace(e, p, angleAt(p), sector, allowBand)) { pos = p; return true; }
                    continue;
                }
                float baseA = (float)(m_rng.NextDouble() * Math.PI * 2);
                for (int k = 0; k < 2; k++)
                    if (TryPlace(e, p, baseA + k * Mathf.PI * 0.5f, sector, allowBand)) { pos = p; return true; }
            }
            return false;
        }

        bool TryPlace(CatalogEntry e, Vector2 p, float angle, int sector, bool allowBand, float gap = 1.2f)
        {
            Vector3 size = e.Size;
            if (!m_zone.ContainsFootprint(p, size, angle, 0.3f)) return false;

            // 外形(中心+外周2mおき)の各点で、帯・区画・広場・園路との位置関係を調べる
            float band = allowBand ? 0.5f : m_treeBand;
            foreach (var q in FootprintPoints(p, size, angle))
            {
                if (m_zone.DistToBoundary(q) < band) return false;
                if (sector >= 0 && (SectorOf(q) != sector || DistToSectorEdges(q, sector) < 0.8f)) return false;
                if (m_rp > 0f)
                {
                    float dc = (q - m_c).magnitude;
                    if (sector == PlazaSector) { if (dc > m_rp - m_pathHalf - 0.3f) return false; }
                    else if (dc < m_rp + m_pathHalf + 0.5f) return false;
                }
                if (sector != PlazaSector && CorridorDist(q) < m_pathHalf + 0.4f) return false;
            }

            // 他のPropとの間隔(長方形を平均半径の円で近似)
            float r = (size.x + size.z) * 0.25f;
            foreach (var o in m_placed)
            {
                float dx = o.Position.x - p.x, dz = o.Position.z - p.y;
                float min = r + o.Radius + gap;
                if (dx * dx + dz * dz < min * min) return false;
            }

            Vector3 pos = Geo.Ground(p);
            if (!PlacementGenerator.PlacementOk(e.Info, pos)) return false;

            m_placed.Add(new Placement { Info = e.Info, Position = pos, Angle = angle, Radius = r });
            m_avoidHere.Add(e.Info.name); // 同じ公園で同じモデルを繰り返さない(ベンチなど意図して使い回すものは除く)
            return true;
        }

        static IEnumerable<Vector2> FootprintPoints(Vector2 c, Vector3 size, float angle)
        {
            yield return c;
            Quaternion q = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, Vector3.down);
            Vector3 r3 = q * Vector3.right * (size.x * 0.5f), f3 = q * Vector3.forward * (size.z * 0.5f);
            Vector2 R = new Vector2(r3.x, r3.z), F = new Vector2(f3.x, f3.z);
            var corners = new[] { c + R + F, c - R + F, c - R - F, c + R - F };
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = corners[i], b = corners[(i + 1) % 4];
                int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / 2f));
                for (int k = 0; k < n; k++) yield return Vector2.Lerp(a, b, k / (float)n);
            }
        }

        // ================= 角度・区画 =================

        float AngleOf(Vector2 p) => Mathf.Repeat(Mathf.Atan2(p.y - m_c.y, p.x - m_c.x), Mathf.PI * 2f);
        static Vector2 Dir(float a) => new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        static float AngleDiff(float a, float b) { float d = Mathf.Abs(Mathf.Repeat(a - b, Mathf.PI * 2f)); return Mathf.Min(d, Mathf.PI * 2f - d); }

        static float SectorWidth(List<float> sortedBounds, int i)
        {
            float a = sortedBounds[i];
            float b = i + 1 < sortedBounds.Count ? sortedBounds[i + 1] : sortedBounds[0] + Mathf.PI * 2f;
            return sortedBounds.Count == 1 ? Mathf.PI * 2f : b - a;
        }

        int SectorOf(Vector2 p)
        {
            float a = AngleOf(p);
            int n = m_bounds.Length;
            for (int i = 0; i < n; i++)
            {
                float lo = m_bounds[i];
                float hi = i + 1 < n ? m_bounds[i + 1] : m_bounds[0] + Mathf.PI * 2f;
                float aa = a < lo ? a + Mathf.PI * 2f : a;
                if (aa >= lo && aa < hi) return i;
            }
            return 0;
        }

        float DistToSectorEdges(Vector2 p, int i)
        {
            if (m_bounds.Length < 2) return float.MaxValue;
            return Mathf.Min(DistToRay(p, m_bounds[i]), DistToRay(p, m_bounds[(i + 1) % m_bounds.Length]));
        }

        float DistToRay(Vector2 p, float angle)
        {
            Vector2 d = Dir(angle), v = p - m_c;
            float t = Vector2.Dot(v, d);
            return t <= 0f ? v.magnitude : Mathf.Abs(v.x * d.y - v.y * d.x);
        }

        float CorridorDist(Vector2 p)
        {
            float best = float.MaxValue;
            foreach (var c in m_corridors) best = Mathf.Min(best, Geo.DistToSegment(p, c.Key, c.Value));
            return best;
        }

        Vector2 RandomSample(int i) => m_samples[i].Count > 0 ? m_samples[i][m_rng.Next(m_samples[i].Count)] : m_sectorCenter[i];

        /// <summary>ベンチ/トイレ等の前面をdirに向けるAngle(向き反転設定を考慮)</summary>
        float AngleFront(Vector2 dir)
        {
            if (dir.sqrMagnitude < 1e-6f) dir = Vector2.up;
            return Geo.AngleFacing(m_flip ? -dir.normalized : dir.normalized);
        }
    }
}
