using System;
using System.Collections.Generic;

namespace PlaygroundMod
{
    /// <summary>
    /// 日本語/英語の切り替え。設定 Language: 0=自動(ゲームの言語), 1=日本語, 2=English。
    /// 変更すると Changed が呼ばれ、オプション画面やボタンの文字をその場で書き換える
    /// </summary>
    public static class Loc
    {
        public static event Action Changed;

        public static bool Japanese
        {
            get
            {
                switch (ModSettings.Instance.Language)
                {
                    case 1: return true;
                    case 2: return false;
                    default:
                        try { return (ColossalFramework.Globalization.LocaleManager.instance.language ?? "").StartsWith("ja"); }
                        catch { return false; }
                }
            }
        }

        public static void SetLanguage(int lang)
        {
            ModSettings.Instance.Language = lang;
            ModSettings.Instance.Save();
            Changed?.Invoke();
        }

        public static string T(string key)
        {
            if (!s_text.TryGetValue(key, out var pair)) return key;
            return Japanese ? pair[1] : pair[0];
        }

        static readonly Dictionary<string, string[]> s_text = new Dictionary<string, string[]>
        {
            // ---- MOD情報・ボタン ----
            ["mod_desc"] = new[] { "Drag an area to build a park. Residents come and play.", "範囲を指定して公園を作成。住民が遊びに来ます。" },
            ["btn_text"] = new[] { "Park", "公園" },
            ["btn_tooltip"] = new[] { "Playground Builder (Ctrl+P)", "公園ビルダー (Ctrl+P)" },

            // ---- ツールの案内 ----
            ["tip_no_props"] = new[] { "No playground props found.\nSubscribe to playground prop assets.", "遊具のPropが見つかりません。\n遊具アセットを購読してください。" },
            ["tip_idle"] = new[] { "Drag: rectangle area\nClick: polygon area", "ドラッグ: 四角形で範囲指定\nクリック: 多角形で範囲指定" },
            ["tip_points"] = new[] { "Points: {0}\nClick: add point  Right-click: finish", "頂点: {0}\nクリック: 頂点を追加  右クリック: 確定" },
            ["tip_hover"] = new[] { "Delete: remove park\nR: rebuild with new props", "Delete: 公園を削除\nR: 別の組み合わせで作り直す" },

            // ---- オプション: グループ ----
            ["grp_language"] = new[] { "Language", "言語" },
            ["grp_layout"] = new[] { "Layout", "レイアウト" },
            ["grp_visitors"] = new[] { "Visitors", "来園者" },
            ["grp_benches"] = new[] { "Benches & Toilets", "ベンチ・トイレ" },
            ["grp_protect"] = new[] { "Park area protection", "敷地の保護" },
            ["grp_fence"] = new[] { "Fence", "柵" },

            // ---- オプション: 項目 ----
            ["language"] = new[] { "Language", "言語" },
            ["lang_auto"] = new[] { "Auto (game language)", "自動 (ゲームの言語)" },
            ["layout"] = new[] { "Layout", "レイアウト" },
            ["layout_plan"] = new[] { "Park plan (plaza, paths, zones)", "公園レイアウト (広場・園路・ゾーン)" },
            ["layout_random"] = new[] { "Random scatter", "ランダム配置" },
            ["density"] = new[] { "Play equipment density", "遊具の密度" },
            ["use_path_net"] = new[] { "Build paths as pedestrian networks", "園路を歩行者道で作る" },
            ["path_width"] = new[] { "Path width (m)", "園路の幅 (m)" },
            ["tree_ring"] = new[] { "Plant trees along the edge", "外周に木を植える" },
            ["tree_spacing"] = new[] { "Tree spacing (m)", "木の間隔 (m)" },

            ["max_visitors"] = new[] { "Max visitors per park", "1公園の最大来園者数" },
            ["search_radius"] = new[] { "Visitor search radius (m)", "来園者を探す範囲 (m)" },
            ["allow_adults"] = new[] { "Adults also visit", "大人も来る" },
            ["allow_night"] = new[] { "Visit at night", "夜も来る" },
            ["adult_rest"] = new[] { "Adults rest on benches/shelters", "大人がベンチ等で休む確率" },
            ["toilet_chance"] = new[] { "Toilet visit chance", "トイレに行く確率" },
            ["drink_chance"] = new[] { "Drinking fountain chance", "水飲み場に行く確率" },

            ["bench_density"] = new[] { "Bench density (0 = none)", "ベンチの密度 (0でなし)" },
            ["place_toilet"] = new[] { "Place public toilets", "トイレを置く" },
            ["toilet_min_area"] = new[] { "Min area for toilet (m2)", "トイレを置く最小面積 (m2)" },
            ["flip_facing"] = new[] { "Flip bench/toilet facing", "ベンチ・トイレの向きを反転" },

            ["block_growables"] = new[] { "Block houses/shops/offices/industry in parks", "公園内に住宅・商業・工業・オフィスを建てさせない" },
            ["unzone"] = new[] { "Remove zoning inside parks (and keep it removed)", "公園内のゾーンを消す (塗り直しても戻す)" },
            ["block_plops"] = new[] { "Also block placed buildings (services etc.)", "手動で置く建物 (公共施設など) も禁止" },

            ["place_fence"] = new[] { "Place fence around the park", "外周に柵を置く" },
            ["use_fence_net"] = new[] { "Use fence networks (props if none found)", "フェンスのネットワークを使う (無ければProp)" },
            ["fence_avoid_roads"] = new[] { "Leave gaps where roads overlap", "道路と重なる所は空ける" },
            ["fence_avoid_buildings"] = new[] { "Also leave gaps in building lots", "建物の敷地も空ける" },
            ["follow_terrain"] = new[] { "Follow terrain (fence & path networks)", "地形に沿わせる (柵・園路)" },
            ["terrain_tol"] = new[] { "Terrain follow tolerance (m)", "地形追従の許容値 (m)" },
            ["gates"] = new[] { "Entrances (0 = auto)", "入口の数 (0で自動)" },
            ["gate_width"] = new[] { "Entrance width (m)", "入口の幅 (m)" },
        };
    }
}
