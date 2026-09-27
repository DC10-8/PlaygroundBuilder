using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlaygroundMod
{
    /// <summary>遊具・ベンチ・トイレなど1つ分の「使う場所」。スロット数=同時に使える人数</summary>
    public class PlaySpot
    {
        public readonly ushort PropId;
        public readonly Vector3 Pos;
        public readonly float Angle;
        public readonly PlayKind Kind;
        public readonly Vector3 Size;
        public readonly int Capacity;
        public readonly bool[] Used;
        readonly Vector3 m_long, m_short, m_fwd, m_right;
        readonly float m_longLen, m_shortLen;

        public bool IsAmenity => PropCatalog.IsAmenity(Kind);

        // アセットに設定された座る位置(PropInfo.m_specialPlaces)。無ければ null で、形から推定した位置を使う
        readonly Vector3[] m_seatPos;
        readonly Vector3[] m_seatDir;
        public bool HasAssetSeats => m_seatPos != null;

        readonly PropGeom m_geom;               // メッシュから読んだ形(読めなければ Valid=false)
        public readonly uint[] SeatedFrame;     // 各スロットで最後に座っていたフレーム(シーソーの相手がいるかの判定)

        public PlaySpot(ushort propId, Vector3 pos, float angle, PlayKind kind, Vector3 size, PropInfo info = null)
        {
            PropId = propId; Pos = pos; Angle = angle; Kind = kind; Size = size;
            Quaternion q = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, Vector3.down); // CS1のProp回転と同じ向き
            m_fwd = q * Vector3.forward;
            m_right = q * Vector3.right;
            bool xLong = size.x >= size.z;
            m_long = xLong ? m_right : m_fwd;
            m_short = xLong ? m_fwd : m_right;
            m_longLen = Mathf.Max(size.x, size.z);
            m_shortLen = Mathf.Min(size.x, size.z);

            m_geom = PropCatalog.GetGeom(info);

            // 座る位置: 休憩場所・ブランコ・シーソーはアセットの設定を使う(シーソーは2か所ある時だけ)
            if (info != null && (PropCatalog.IsRest(kind) || kind == PlayKind.Swing || kind == PlayKind.Seesaw))
            {
                BuildAssetSeats(info, pos, angle, out m_seatPos, out m_seatDir);
                if (kind == PlayKind.Seesaw && m_seatPos != null && m_seatPos.Length != 2) { m_seatPos = null; m_seatDir = null; }
            }
            Capacity = m_seatPos != null ? Mathf.Min(m_seatPos.Length, 8) : CapacityFor(kind, size);
            Used = new bool[Capacity];
            SeatedFrame = new uint[Capacity];
        }

        /// <summary>
        /// バニラ(BuildingAI.CalculateUnspawnPosition)と同じ変換で、Propの座る位置をワールド座標にする。
        /// c=cos(角度), s=sin(角度) として
        ///   位置 = Propの位置(地面) + (x*c + z*s, y, x*s − z*c)
        ///   向き = (dx*c + dz*s, dx*s − dz*c)
        /// SittingDown 付きの場所を優先し、無ければ全ての場所を使う
        /// </summary>
        static void BuildAssetSeats(PropInfo info, Vector3 pos, float angle, out Vector3[] seatPos, out Vector3[] seatDir)
        {
            seatPos = null; seatDir = null;
            var places = info.m_specialPlaces;
            if (places == null || places.Length == 0) return;

            var use = new List<PropInfo.SpecialPlace>();
            foreach (var p in places)
                if ((p.m_specialFlags & CitizenInstance.Flags.SittingDown) != 0) use.Add(p);
            if (use.Count == 0) use.AddRange(places);

            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            float ground = Ground(pos);
            seatPos = new Vector3[use.Count];
            seatDir = new Vector3[use.Count];
            for (int i = 0; i < use.Count; i++)
            {
                Vector3 lp = use[i].m_position, ld = use[i].m_direction;
                seatPos[i] = new Vector3(pos.x + lp.x * c + lp.z * s, ground + lp.y, pos.z + lp.x * s - lp.z * c);
                seatDir[i] = new Vector3(ld.x * c + ld.z * s, 0f, ld.x * s - ld.z * c);
            }
        }

        static int CapacityFor(PlayKind kind, Vector3 size)
        {
            float l = Mathf.Max(size.x, size.z);
            switch (kind)
            {
                case PlayKind.Swing: return Mathf.Clamp(Mathf.RoundToInt(l / 1.5f), 1, 4);
                case PlayKind.Seesaw: return 2;
                case PlayKind.Slide: return 2;
                case PlayKind.Spinner: return 4;
                case PlayKind.Sandbox: return 3;
                case PlayKind.Climber: return Mathf.Clamp(Mathf.RoundToInt(size.x * size.z / 15f), 2, 8);
                case PlayKind.Generic: return Mathf.Clamp(Mathf.RoundToInt(size.x * size.z / 15f), 2, 10); // 大型複合遊具は大人数
                case PlayKind.WaterPlay: return Mathf.Clamp(Mathf.RoundToInt(size.x * size.z / 10f), 2, 8);
                case PlayKind.Bench: return Mathf.Clamp(Mathf.FloorToInt(size.x / 0.9f), 1, 3);
                case PlayKind.Toilet: return size.x >= 5f ? 2 : 1; // 個室数
                case PlayKind.Shelter: return Mathf.Clamp(Mathf.RoundToInt((size.x + size.z) / 2.5f), 3, 8);
                case PlayKind.Picnic: return l >= 1.5f ? 4 : 2;
                case PlayKind.Drink: return 1;
                default: return 2;
            }
        }

        /// <summary>ベンチの座面の向き / トイレの入口の向き(アセットの前面)</summary>
        Vector3 Front => ModSettings.Instance.AmenityFrontIsMinusZ ? -m_fwd : m_fwd;

        public float CircleRadius => Mathf.Max(Size.x, Size.z) * 0.4f;

        public static float Ground(Vector3 p) => TerrainManager.instance.SampleDetailHeight(p);

        /// <summary>スロットの立ち位置と向き</summary>
        public Vector3 GetSlot(int i, out Vector3 face)
        {
            // アセットに座る位置があればそれを使う(高さもアセットの値)
            if (m_seatPos != null && i < m_seatPos.Length)
            {
                face = m_seatDir[i].sqrMagnitude > 1e-4f ? m_seatDir[i] : Front;
                return m_seatPos[i];
            }

            Vector3 pos;
            switch (Kind)
            {
                case PlayKind.Swing:
                {
                    float t = Capacity == 1 ? 0f : i / (float)(Capacity - 1) - 0.5f;
                    pos = Pos + m_long * (t * m_longLen * 0.7f);
                    face = m_short;
                    break;
                }
                case PlayKind.Seesaw:
                {
                    float sign = i == 0 ? 1f : -1f;
                    pos = Pos + m_long * (sign * m_longLen * 0.4f);
                    face = -m_long * sign;
                    break;
                }
                case PlayKind.Slide:
                {
                    GetSlide(i, out pos, out Vector3 top, out _, out _);
                    face = top - pos;
                    break;
                }
                case PlayKind.Bench:
                {
                    // ベンチはモデルのX方向に長い前提。座面に並んで座る
                    float t = Capacity == 1 ? 0f : i / (float)(Capacity - 1) - 0.5f;
                    Vector3 front = Front;
                    pos = Pos + m_right * (t * Size.x * 0.6f) + front * 0.1f;
                    face = front;
                    break;
                }
                case PlayKind.Toilet:
                {
                    // 入口(前面)の前に立つ。個室が2つなら左右に分ける
                    Vector3 front = Front;
                    float lat = (i - (Capacity - 1) * 0.5f) * Size.x * 0.4f;
                    pos = Pos + front * (Size.z * 0.5f + 0.6f) + m_right * lat;
                    face = -front;
                    break;
                }
                case PlayKind.Drink:
                {
                    Vector3 front = Front;
                    pos = Pos + front * (Size.z * 0.5f + 0.5f);
                    face = -front;
                    break;
                }
                case PlayKind.Picnic:
                {
                    // テーブルの長辺の両側に座る
                    float side = i % 2 == 0 ? 1f : -1f;
                    float along = Capacity == 4 ? (i < 2 ? -0.25f : 0.25f) * m_longLen : 0f;
                    pos = Pos + m_short * (side * (m_shortLen * 0.5f + 0.35f)) + m_long * along;
                    face = -m_short * side;
                    break;
                }
                case PlayKind.Shelter:
                {
                    // 屋根の下で輪になって座る
                    float ang = i * Mathf.PI * 2f / Capacity;
                    Vector3 off = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (m_shortLen * 0.3f);
                    pos = Pos + off;
                    face = -off;
                    break;
                }
                default:
                {
                    float ang = i * Mathf.PI * 2f / Capacity;
                    float r = CircleRadius + (Kind == PlayKind.Sandbox ? 0.3f : 0f);
                    Vector3 off = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
                    pos = Pos + off;
                    face = -off;
                    break;
                }
            }
            pos.y = Ground(pos);
            return pos;
        }

        /// <summary>
        /// 滑り台の形。foot=はしごの下(地面)、top=滑り始め(地面の位置、高さはheight)、bottom=滑り終わり(地面)。
        /// メッシュが読めれば高い方の端を上端にし、端の位置と高さもメッシュから取る
        /// </summary>
        public void GetSlide(int i, out Vector3 foot, out Vector3 top, out Vector3 bottom, out float height)
        {
            float lateral = Capacity > 1 ? (i == 0 ? -0.3f : 0.3f) : 0f;
            Vector3 c = Pos + m_short * lateral;
            float topL, bottomL;
            if (m_geom.Valid)
            {
                bool topIsMax = m_geom.TopSign > 0f;
                topL = (topIsMax ? m_geom.LongMax : m_geom.LongMin) * 0.8f;
                bottomL = (topIsMax ? m_geom.LongMin : m_geom.LongMax) * 0.9f;
                height = Mathf.Clamp(m_geom.TopHeight * 0.7f, 0.6f, 4f); // 手すりや屋根の分を引いた、滑り面の高さの目安
            }
            else
            {
                topL = -m_longLen * 0.4f;
                bottomL = m_longLen * 0.45f;
                height = Mathf.Clamp(Size.y * 0.5f, 0.5f, 2.5f);
            }
            Vector3 dir = (topL > bottomL ? 1f : -1f) * m_long; // 下端→上端の向き
            top = c + m_long * topL;
            bottom = c + m_long * bottomL;
            foot = top + dir * 1.0f; // はしごは上端の外側
            top.y = Ground(top); bottom.y = Ground(bottom); foot.y = Ground(foot);
        }

        /// <summary>ブランコの鎖の長さ(横棒の高さ − 座面の高さ)</summary>
        public float SwingLength
        {
            get
            {
                float bar = m_geom.Valid ? m_geom.MaxHeight : Size.y;
                return Mathf.Clamp(bar - 0.5f, 1.0f, 3.5f);
            }
        }

        /// <summary>シーソーのスロットが支点のどちら側か(+1/−1)と、支点からの距離</summary>
        public void SeesawArm(int i, Vector3 seat, out float side, out float arm)
        {
            Vector3 d = seat - Pos; d.y = 0f;
            float along = Vector3.Dot(d, m_long);
            side = along >= 0f ? 1f : -1f;
            arm = Mathf.Max(0.5f, Mathf.Abs(along));
        }

        public Vector3 RandomPoint(System.Random rng)
        {
            float a = (float)(rng.NextDouble() - 0.5) * m_longLen * 0.8f;
            float b = (float)(rng.NextDouble() - 0.5) * m_shortLen * 0.8f;
            Vector3 p = Pos + m_long * a + m_short * b;
            p.y = Ground(p);
            return p;
        }
    }

    /// <summary>柵の出入り口。Posは境界上、Inwardは内向きの単位ベクトル</summary>
    public struct Gate
    {
        public Vector2 Pos;
        public Vector2 Inward;
    }

    /// <summary>ユーザーが指定した範囲(XZ平面の多角形)と、そこに作ったもの</summary>
    public class PlaygroundZone
    {
        public readonly int Id;
        public readonly Vector2[] Polygon;
        public readonly float Area;
        public readonly float Perimeter;
        public readonly Rect BoundsXZ;
        public readonly Vector2 Centroid;

        public readonly List<ushort> Props = new List<ushort>();
        public readonly List<string> PropNames = new List<string>();
        public readonly List<PlaySpot> Spots = new List<PlaySpot>();
        public readonly List<Gate> Gates = new List<Gate>();
        public readonly List<ushort> FenceSegments = new List<ushort>();   // 敷いたフェンスのネットワーク
        public readonly List<string> FenceNetNames = new List<string>();   // 削除時の本人確認用
        public readonly List<ushort> PathSegments = new List<ushort>();    // 園路のネットワーク
        public readonly List<string> PathNetNames = new List<string>();
        public readonly List<uint> Trees = new List<uint>();
        public readonly List<string> TreeNames = new List<string>();
        public readonly List<Vector2[]> Paths = new List<Vector2[]>();     // 園路の中心線 (Gatesと同じ並び: ゲート→広場)
        public Vector2[] Ring = new Vector2[0];                            // 広場の周回路

        public Vector3 Center => new Vector3(Centroid.x, 0f, Centroid.y);

        public PlaygroundZone(int id, Vector2[] polygon)
        {
            Id = id;
            Polygon = polygon;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            float area = 0f, perim = 0f;
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
                area += a.x * b.y - b.x * a.y;
                perim += (b - a).magnitude;
                sum += a;
                minX = Mathf.Min(minX, a.x); maxX = Mathf.Max(maxX, a.x);
                minZ = Mathf.Min(minZ, a.y); maxZ = Mathf.Max(maxZ, a.y);
            }
            Area = Mathf.Abs(area) * 0.5f;
            Perimeter = perim;
            BoundsXZ = new Rect(minX, minZ, maxX - minX, maxZ - minZ);
            Centroid = polygon.Length > 0 ? sum / polygon.Length : Vector2.zero;
        }

        // ================= 幾何 =================

        public bool Contains(Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = Polygon.Length - 1; i < Polygon.Length; j = i++)
            {
                Vector2 a = Polygon[i], b = Polygon[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        public float DistToBoundary(Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < Polygon.Length; i++)
                best = Mathf.Min(best, Geo.DistToSegment(p, Polygon[i], Polygon[(i + 1) % Polygon.Length]));
            return best;
        }

        public bool ContainsFootprint(Vector2 c, Vector3 size, float angle, float margin)
        {
            Quaternion q = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, Vector3.down);
            Vector3 r = q * Vector3.right * (size.x * 0.5f + margin);
            Vector3 f = q * Vector3.forward * (size.z * 0.5f + margin);
            if (!Contains(c)) return false;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 o = r * sx + f * sz;
                    if (!Contains(new Vector2(c.x + o.x, c.y + o.z))) return false;
                }
            return true;
        }

        /// <summary>外周に沿った距離dの点。cornerMarginで辺の端(角)を避ける</summary>
        public bool PointAtDistance(float d, float cornerMargin, out Vector2 point, out Vector2 inward)
        {
            point = Centroid; inward = Vector2.zero;
            if (Polygon.Length < 3 || Perimeter < 1f) return false;
            d = Mathf.Repeat(d, Perimeter);

            for (int i = 0; i < Polygon.Length; i++)
            {
                Vector2 a = Polygon[i], b = Polygon[(i + 1) % Polygon.Length];
                float len = (b - a).magnitude;
                if (d > len && i < Polygon.Length - 1) { d -= len; continue; }
                if (len < 0.5f) return false;

                Vector2 dir = (b - a) / len;
                float m = Mathf.Min(cornerMargin, len * 0.5f);
                point = a + dir * Mathf.Clamp(d, m, len - m);
                inward = InwardNormal(point, dir);
                return inward != Vector2.zero;
            }
            return false;
        }

        Vector2 InwardNormal(Vector2 point, Vector2 dir)
        {
            Vector2 n = new Vector2(-dir.y, dir.x);
            if (Contains(point + n * 1f)) return n;
            if (Contains(point - n * 1f)) return -n;
            return Vector2.zero;
        }

        /// <summary>外周上のランダムな点(辺の長さで重み付け)と内向きの法線</summary>
        public bool RandomEdgePoint(System.Random rng, out Vector2 point, out Vector2 inward)
        {
            float d = (float)rng.NextDouble() * Perimeter;
            return PointAtDistance(d, 2f, out point, out inward);
        }

        // ================= 出入り・経路 =================

        public int NearestGateIndex(Vector3 from)
        {
            Vector2 p = new Vector2(from.x, from.z);
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < Gates.Count; i++)
            {
                float d = (Gates[i].Pos - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// 住民の出入り口(エリアの1.5m外側)。ゲートがあれば一番近いゲート、無ければ一番近い境界上の点
        /// </summary>
        public Vector3 EdgePointToward(Vector3 from)
        {
            int gi = NearestGateIndex(from);
            if (gi >= 0)
            {
                var g = Gates[gi];
                return Geo.Ground(g.Pos - g.Inward * 1.5f);
            }

            Vector2 p = new Vector2(from.x, from.z);
            Vector2 edge = Centroid;
            float edgeD = float.MaxValue;
            for (int i = 0; i < Polygon.Length; i++)
            {
                Vector2 a = Polygon[i], b = Polygon[(i + 1) % Polygon.Length];
                Vector2 c = Geo.ClosestOnSegment(p, a, b);
                float d = (c - p).sqrMagnitude;
                if (d < edgeD) { edgeD = d; edge = c; }
            }
            Vector2 outward = edge - Centroid;
            if (outward.sqrMagnitude > 1e-4f) edge += outward.normalized * 1f;
            return Geo.Ground(edge);
        }

        /// <summary>
        /// ゲートgiから園路を通ってtargetへ向かう経由地 (ゲートの内側 → 園路上でtargetに一番近い点)。
        /// 園路が無ければゲートの内側だけ
        /// </summary>
        public List<Vector3> RouteFromGate(int gi, Vector3 target)
        {
            var route = new List<Vector3>();
            if (gi < 0 || gi >= Gates.Count) return route;
            var g = Gates[gi];
            route.Add(Geo.Ground(g.Pos + g.Inward * 1.5f));

            if (gi < Paths.Count && Paths[gi] != null && Paths[gi].Length >= 2)
            {
                var path = Paths[gi];
                Vector2 t = new Vector2(target.x, target.z);
                // 園路上でtargetに一番近い点まで、途中の折れ点を順に通る
                float bestD = float.MaxValue;
                int bestSeg = 0;
                Vector2 bestPt = path[0];
                for (int i = 0; i < path.Length - 1; i++)
                {
                    Vector2 c = Geo.ClosestOnSegment(t, path[i], path[i + 1]);
                    float d = (c - t).sqrMagnitude;
                    if (d < bestD) { bestD = d; bestSeg = i; bestPt = c; }
                }
                for (int i = 1; i <= bestSeg; i++) route.Add(Geo.Ground(path[i]));
                if ((bestPt - path[0]).sqrMagnitude > 4f) route.Add(Geo.Ground(bestPt));
            }
            return route;
        }

        // ================= スポット =================

        public void RebuildSpots()
        {
            Spots.Clear();
            var buf = PropManager.instance.m_props.m_buffer;
            foreach (ushort id in Props)
            {
                if ((buf[id].m_flags & (ushort)PropInstance.Flags.Created) == 0) continue;
                PropInfo info = buf[id].Info;
                if (info == null) continue;
                PlayKind kind = PropCatalog.Classify(info) ?? PlayKind.Generic;
                if (PropCatalog.IsSpotless(kind)) continue; // 柵・案内板は使う場所ではない
                Spots.Add(new PlaySpot(id, buf[id].Position, buf[id].Angle, kind, PropCatalog.GetSize(info), info));
            }
        }

        public bool HasKind(PlayKind kind)
        {
            foreach (var s in Spots) if (s.Kind == kind) return true;
            return false;
        }

        public int FreeSlotCount(Func<PlaySpot, bool> filter = null)
        {
            int n = 0;
            foreach (var s in Spots)
            {
                if (filter != null && !filter(s)) continue;
                foreach (bool u in s.Used) if (!u) n++;
            }
            return n;
        }

        public bool TakeRandomFreeSlot(System.Random rng, Func<PlaySpot, bool> filter, out PlaySpot spot, out int slot)
        {
            spot = null; slot = -1;
            int free = FreeSlotCount(filter);
            if (free == 0) return false;
            int pick = rng.Next(free);
            foreach (var s in Spots)
            {
                if (filter != null && !filter(s)) continue;
                for (int i = 0; i < s.Used.Length; i++)
                {
                    if (s.Used[i]) continue;
                    if (pick-- == 0) { s.Used[i] = true; spot = s; slot = i; return true; }
                }
            }
            return false;
        }
    }

    /// <summary>2D幾何の小物</summary>
    public static class Geo
    {
        public static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return a + ab * t;
        }

        public static float DistToSegment(Vector2 p, Vector2 a, Vector2 b) => (ClosestOnSegment(p, a, b) - p).magnitude;

        public static Vector3 Ground(Vector2 p)
        {
            var v = new Vector3(p.x, 0f, p.y);
            v.y = TerrainManager.instance.SampleDetailHeight(v);
            return v;
        }

        /// <summary>Propの前面(+Z)をdirに向けるAngle。q=AngleAxis(θ, down) で forward=(−sinθ, 0, cosθ)</summary>
        public static float AngleFacing(Vector2 dir)
        {
            float a = Mathf.Atan2(-dir.x, dir.y);
            return a < 0f ? a + Mathf.PI * 2f : a;
        }

        /// <summary>PropのX軸(right)をdirに合わせるAngle。right=(cosθ, 0, sinθ)</summary>
        public static float AngleAlongX(Vector2 dir)
        {
            float a = Mathf.Atan2(dir.y, dir.x);
            return a < 0f ? a + Mathf.PI * 2f : a;
        }
    }
}
