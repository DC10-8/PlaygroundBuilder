using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlaygroundMod
{
    /// <summary>
    /// 公園の敷地を建物から守る。
    ///  - 敷地にかかる成長型の建物(住宅・商業・工業・オフィス)は作らせない (Harmony: BuildingManager.CreateBuilding)
    ///  - 敷地内の区画(ゾーン)を未指定に戻し、塗り直しても定期的に戻す
    ///  - 設定により手動で置く建物も禁止 (Harmony: BuildingTool.CheckSpace にエラーを足す)
    /// </summary>
    public static class ParkGuard
    {
        const float Shrink = 0.5f;       // 境界に接するだけの建物は許す
        const float SampleStep = 4f;
        const uint ZoneBlockCreated = 1u; // ZoneBlock.FLAG_CREATED

        // ================= 判定 =================

        /// <summary>
        /// 位置pos・角度angle、幅width×奥行きlength(セル数、1セル8m)の建物の敷地が、どこかの公園にかかるか
        /// </summary>
        public static bool IntersectsPark(Vector3 pos, float angle, int width, int length)
        {
            if (ZoneManager.Zones.Count == 0) return false;

            float hw = Mathf.Max(0.5f, width * 4f - Shrink), hl = Mathf.Max(0.5f, length * 4f - Shrink);
            // 建物の回転はPropと同じ: right=(cos, sin), forward=(−sin, cos)
            Vector2 c = new Vector2(pos.x, pos.z);
            Vector2 R = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 F = new Vector2(-R.y, R.x);
            float reach = Mathf.Sqrt(hw * hw + hl * hl);

            lock (ZoneManager.Lock)
            {
                foreach (var z in ZoneManager.Zones)
                {
                    Rect b = z.BoundsXZ;
                    if (c.x + reach < b.xMin || c.x - reach > b.xMax || c.y + reach < b.yMin || c.y - reach > b.yMax) continue;

                    // 公園の角が敷地の中にある
                    foreach (var v in z.Polygon)
                    {
                        Vector2 d = v - c;
                        if (Mathf.Abs(Vector2.Dot(d, R)) < hw && Mathf.Abs(Vector2.Dot(d, F)) < hl) return true;
                    }

                    // 敷地の中の点(4mおき、外周込み)が公園の中にある
                    int nx = Mathf.Max(1, Mathf.CeilToInt(hw * 2f / SampleStep));
                    int nz = Mathf.Max(1, Mathf.CeilToInt(hl * 2f / SampleStep));
                    for (int ix = 0; ix <= nx; ix++)
                        for (int iz = 0; iz <= nz; iz++)
                        {
                            Vector2 p = c + R * (-hw + 2f * hw * ix / nx) + F * (-hl + 2f * hl * iz / nz);
                            if (z.Contains(p)) return true;
                        }
                }
            }
            return false;
        }

        /// <summary>区画に建つ成長型の建物か(住宅・商業・工業・オフィス)</summary>
        public static bool IsGrowable(BuildingInfo info) => info != null && info.m_buildingAI is PrivateBuildingAI;

        // ================= ゾーン消し (シミュレーションスレッド) =================

        public static void SimulationTick()
        {
            if (!ModSettings.Instance.UnzoneParkArea) return;
            // 約4秒(256フレーム)ごとに、塗り直された区画を戻す
            if ((SimulationManager.instance.m_currentFrameIndex & 255) != 0) return;
            UnzoneAll();
        }

        public static void UnzoneAll()
        {
            List<PlaygroundZone> zones;
            lock (ZoneManager.Lock) zones = new List<PlaygroundZone>(ZoneManager.Zones);
            foreach (var z in zones) Unzone(z);
        }

        /// <summary>公園の中にある区画のセルを未指定にする</summary>
        public static int Unzone(PlaygroundZone zone)
        {
            if (!ModSettings.Instance.UnzoneParkArea) return 0;
            var zm = global::ZoneManager.instance; // PlaygroundMod.ZoneManager と名前が同じなので global:: で区別
            var buf = zm.m_blocks.m_buffer;
            Rect b = zone.BoundsXZ;
            const float Margin = 48f; // ブロック(最大 4x8 セル)の中心から端までの距離より大きく
            int cleared = 0;

            for (int i = 1; i < buf.Length; i++)
            {
                if ((buf[i].m_flags & ZoneBlockCreated) == 0) continue;
                Vector3 pos = buf[i].m_position;
                if (pos.x < b.xMin - Margin || pos.x > b.xMax + Margin || pos.z < b.yMin - Margin || pos.z > b.yMax + Margin) continue;

                // セル(列x=0..3, 行z=0..7)の中心 = 位置 + a*(x-3.5) + a2*(z-3.5)   (a: 列方向8m, a2: 行方向8m)
                float ang = buf[i].m_angle;
                Vector2 a = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 8f;
                Vector2 a2 = new Vector2(a.y, -a.x);
                Vector2 origin = new Vector2(pos.x, pos.z);

                bool changed = false;
                for (int row = 0; row < 8; row++)
                    for (int col = 0; col < 4; col++)
                    {
                        Vector2 cell = origin + a * (col - 3.5f) + a2 * (row - 3.5f);
                        if (!zone.Contains(cell)) continue;
                        if (buf[i].GetZone(col, row) == ItemClass.Zone.Unzoned) continue;
                        if (buf[i].SetZone(col, row, ItemClass.Zone.Unzoned)) { changed = true; cleared++; }
                    }
                if (changed) buf[i].RefreshZoning((ushort)i);
            }
            if (cleared > 0) Debug.Log($"[PlaygroundMod] zone#{zone.Id}: unzoned {cleared} cells");
            return cleared;
        }
    }
}
