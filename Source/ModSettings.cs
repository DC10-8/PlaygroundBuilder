using System;
using System.IO;
using System.Xml.Serialization;
using ColossalFramework.IO;
using UnityEngine;

namespace PlaygroundMod
{
    public class ModSettings
    {
        // ---- 言語 ----
        public int Language = 0;                // 0=自動(ゲームの言語), 1=日本語, 2=English

        // ---- レイアウト ----
        public int LayoutMode = 0;              // 0=公園レイアウト(広場・園路・ゾーン), 1=ランダム配置
        public float Density = 1.0f;            // 遊具の配置密度倍率

        // ---- 来訪者 ----
        public int MaxActorsPerZone = 8;        // 1エリアに同時に来る最大人数
        public float SpawnRadius = 320f;        // 住民を探す半径(m)
        public bool AllowAdults = true;         // 大人も来る
        public bool AllowNight = false;         // 夜も来る
        public float AdultBenchChance = 0.7f;   // 大人がベンチ/シェルター/ピクニックテーブルを選ぶ確率
        public float ToiletVisitChance = 0.2f;  // 1回の来訪でトイレに行く確率
        public float DrinkChance = 0.15f;       // 1回の来訪で水飲みに行く確率

        // ---- ベンチ・トイレ ----
        public float BenchDensity = 1.0f;       // ベンチ数倍率 (0でベンチ無し)
        public bool PlaceToilet = true;         // トイレを置く
        public float ToiletMinArea = 400f;      // この面積(m²)未満のエリアにはトイレを置かない
        public bool AmenityFrontIsMinusZ = false; // ベンチ/トイレ等の向きが逆になるアセット用

        // ---- 園路・木 (公園レイアウト) ----
        public bool UsePathNetwork = true;              // 園路を歩行者道ネットワークで作る
        public string PathNetName = "Pedestrian Pavement";
        public float PathWidth = 4f;                    // 園路の幅(ネットワークが無い時の空け幅)
        public bool TreeRing = true;                    // 外周に木を植える
        public float TreeSpacing = 8f;                  // 木の間隔(m)

        // ---- 敷地の保護 ----
        public bool BlockBuildingsInPark = true;   // 公園にかかる成長型の建物(住宅・商業・工業・オフィス)を建てさせない
        public bool UnzoneParkArea = true;         // 公園内の区画(ゾーン)を未指定に戻し、塗り直しても戻す
        public bool BlockPlopsInPark = false;      // 手動で置く建物(公共施設など)も公園にかかる位置には置けなくする

        // ---- 柵 ----
        public bool PlaceFence = true;          // 外周に柵を置く
        public bool UseFenceNetwork = true;     // フェンスのネットワークを優先(無ければ柵Prop)
        public bool FenceAvoidObstacles = true; // 道路に食い込む所だけ柵を空ける(OFFなら外周全部に敷く)
        public bool FenceAvoidBuildings = false; // 建物の敷地にかかる所も空ける(既定OFF=敷地内にも柵を張る)
        public bool FollowTerrain = true;       // 柵・園路のネットワークを地形に沿わせる(起伏に合わせてノードを追加)
        public float TerrainTolerance = 0.25f;  // 地面と直線のズレがこれ(m)を超えたらノードを追加
        public int GateCount = 0;               // 出入り口の数 (0=自動)
        public float GateWidth = 3f;            // 出入り口の幅(m)

        // ---- アセット判定の追加キーワード ----
        public string[] ExtraKeywords = new string[0];   // 遊具
        public string[] ExtraWaterPlayKeywords = new string[0];
        public string[] ExtraBenchKeywords = new string[0];
        public string[] ExtraToiletKeywords = new string[0];
        public string[] ExtraShelterKeywords = new string[0];
        public string[] ExtraPicnicKeywords = new string[0];
        public string[] ExtraDrinkKeywords = new string[0];
        public string[] ExtraSignKeywords = new string[0];
        public string[] ExtraFenceKeywords = new string[0];
        public string[] ExtraFenceNetKeywords = new string[0]; // フェンス扱いにするネットワーク名(一致すれば無条件で採用)
        public string[] ExcludeKeywords = new string[0];

        static ModSettings s_instance;
        public static ModSettings Instance => s_instance ?? (s_instance = Load());

        static string FilePath => Path.Combine(DataLocation.localApplicationData, "PlaygroundMod.xml");

        static ModSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var s = File.OpenRead(FilePath))
                        return (ModSettings)new XmlSerializer(typeof(ModSettings)).Deserialize(s);
            }
            catch (Exception e) { Debug.LogWarning("[PlaygroundMod] settings load failed: " + e); }
            return new ModSettings();
        }

        public void Save()
        {
            try
            {
                using (var s = File.Create(FilePath))
                    new XmlSerializer(typeof(ModSettings)).Serialize(s, this);
            }
            catch (Exception e) { Debug.LogWarning("[PlaygroundMod] settings save failed: " + e); }
        }
    }
}
