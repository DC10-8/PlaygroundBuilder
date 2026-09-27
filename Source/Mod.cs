using System;
using System.Collections.Generic;
using CitiesHarmony.API;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace PlaygroundMod
{
    public class PlaygroundModInfo : IUserMod
    {
        public string Name => "Playground Builder";
        public string Description => Loc.T("mod_desc");

        public void OnEnabled() => HarmonyHelper.DoOnHarmonyReady(Patcher.PatchAll);

        public void OnDisabled()
        {
            if (HarmonyHelper.IsHarmonyInstalled) Patcher.UnpatchAll();
        }

        public void OnSettingsUI(UIHelperBase helper) => OptionsUI.Build(helper);
    }

    /// <summary>
    /// オプション画面。作った部品と文字のキーを覚えておき、言語を切り替えたらその場で書き換える。
    /// スライダーは現在値もラベルに出す
    /// </summary>
    static class OptionsUI
    {
        static readonly List<Action> s_relabel = new List<Action>();
        static bool s_hooked;

        public static void Build(UIHelperBase helper)
        {
            s_relabel.Clear();
            if (!s_hooked) { Loc.Changed += Relabel; s_hooked = true; }
            var s = ModSettings.Instance;

            var lang = Group(helper, "grp_language");
            Dropdown(lang, "language", () => new[] { Loc.T("lang_auto"), "日本語", "English" }, s.Language, Loc.SetLanguage);

            var l = Group(helper, "grp_layout");
            Dropdown(l, "layout", () => new[] { Loc.T("layout_plan"), Loc.T("layout_random") }, s.LayoutMode, v => { s.LayoutMode = v; s.Save(); });
            Slider(l, "density", 0.3f, 3f, 0.1f, s.Density, "0.0", v => s.Density = v);
            Check(l, "use_path_net", s.UsePathNetwork, v => s.UsePathNetwork = v);
            Slider(l, "path_width", 2f, 8f, 0.5f, s.PathWidth, "0.0", v => s.PathWidth = v);
            Check(l, "tree_ring", s.TreeRing, v => s.TreeRing = v);
            Slider(l, "tree_spacing", 4f, 20f, 1f, s.TreeSpacing, "0", v => s.TreeSpacing = v);

            var g = Group(helper, "grp_visitors");
            Slider(g, "max_visitors", 1f, 30f, 1f, s.MaxActorsPerZone, "0", v => s.MaxActorsPerZone = (int)v);
            Slider(g, "search_radius", 100f, 800f, 20f, s.SpawnRadius, "0", v => s.SpawnRadius = v);
            Check(g, "allow_adults", s.AllowAdults, v => s.AllowAdults = v);
            Check(g, "allow_night", s.AllowNight, v => s.AllowNight = v);
            Slider(g, "adult_rest", 0f, 1f, 0.05f, s.AdultBenchChance, "P0", v => s.AdultBenchChance = v);
            Slider(g, "toilet_chance", 0f, 0.6f, 0.05f, s.ToiletVisitChance, "P0", v => s.ToiletVisitChance = v);
            Slider(g, "drink_chance", 0f, 0.6f, 0.05f, s.DrinkChance, "P0", v => s.DrinkChance = v);

            var a = Group(helper, "grp_benches");
            Slider(a, "bench_density", 0f, 3f, 0.1f, s.BenchDensity, "0.0", v => s.BenchDensity = v);
            Check(a, "place_toilet", s.PlaceToilet, v => s.PlaceToilet = v);
            Slider(a, "toilet_min_area", 100f, 2000f, 50f, s.ToiletMinArea, "0", v => s.ToiletMinArea = v);
            Check(a, "flip_facing", s.AmenityFrontIsMinusZ, v => s.AmenityFrontIsMinusZ = v);

            var pr = Group(helper, "grp_protect");
            Check(pr, "block_growables", s.BlockBuildingsInPark, v => s.BlockBuildingsInPark = v);
            Check(pr, "unzone", s.UnzoneParkArea, v =>
            {
                s.UnzoneParkArea = v;
                if (v && Loading.InGame) SimulationManager.instance.AddAction(ParkGuard.UnzoneAll);
            });
            Check(pr, "block_plops", s.BlockPlopsInPark, v => s.BlockPlopsInPark = v);

            var f = Group(helper, "grp_fence");
            Check(f, "place_fence", s.PlaceFence, v => s.PlaceFence = v);
            Check(f, "use_fence_net", s.UseFenceNetwork, v => s.UseFenceNetwork = v);
            Check(f, "fence_avoid_roads", s.FenceAvoidObstacles, v => s.FenceAvoidObstacles = v);
            Check(f, "fence_avoid_buildings", s.FenceAvoidBuildings, v => s.FenceAvoidBuildings = v);
            Check(f, "follow_terrain", s.FollowTerrain, v => s.FollowTerrain = v);
            Slider(f, "terrain_tol", 0.05f, 1f, 0.05f, s.TerrainTolerance, "0.00", v => s.TerrainTolerance = v);
            Slider(f, "gates", 0f, 4f, 1f, s.GateCount, "0", v => s.GateCount = (int)v);
            Slider(f, "gate_width", 2f, 10f, 0.5f, s.GateWidth, "0.0", v => s.GateWidth = v);
        }

        static void Relabel()
        {
            foreach (var r in s_relabel)
            {
                try { r(); } catch (Exception e) { Debug.LogWarning("[PlaygroundMod] relabel failed: " + e.Message); }
            }
            PlaygroundButton.Relabel();
        }

        // ---- 部品の作成と書き換え ----

        static UIHelperBase Group(UIHelperBase helper, string key)
        {
            var g = helper.AddGroup(Loc.T(key));
            // グループ見出しは、中身パネルの親にある "Label"
            var uh = g as UIHelper;
            var self = uh != null ? uh.self as UIComponent : null;
            var label = self != null && self.parent != null ? self.parent.Find<UILabel>("Label") : null;
            if (label != null) s_relabel.Add(() => label.text = Loc.T(key));
            return g;
        }

        static void Check(UIHelperBase g, string key, bool value, Action<bool> set)
        {
            var cb = g.AddCheckbox(Loc.T(key), value, v => { set(v); ModSettings.Instance.Save(); }) as UICheckBox;
            if (cb != null) s_relabel.Add(() => cb.text = Loc.T(key));
        }

        static void Slider(UIHelperBase g, string key, float min, float max, float step, float value, string fmt, Action<float> set)
        {
            UILabel label = null;
            Func<float, string> text = v => $"{Loc.T(key)}: {v.ToString(fmt)}";
            var sl = g.AddSlider(text(value), min, max, step, value, v =>
            {
                set(v);
                ModSettings.Instance.Save();
                if (label != null) label.text = text(v);
            }) as UISlider;
            if (sl == null) return;
            label = sl.parent?.Find<UILabel>("Label");
            if (label != null) s_relabel.Add(() => label.text = text(sl.value));
        }

        static void Dropdown(UIHelperBase g, string key, Func<string[]> items, int value, Action<int> set)
        {
            var dd = g.AddDropdown(Loc.T(key), items(), value, v => set(v)) as UIDropDown;
            if (dd == null) return;
            var label = dd.parent?.Find<UILabel>("Label");
            s_relabel.Add(() =>
            {
                int sel = dd.selectedIndex;
                dd.items = items();
                dd.selectedIndex = sel;
                if (label != null) label.text = Loc.T(key);
            });
        }
    }

    public class Loading : LoadingExtensionBase
    {
        public static bool InGame;

        public override void OnLevelLoaded(LoadMode mode)
        {
            if (mode != LoadMode.NewGame && mode != LoadMode.LoadGame && mode != LoadMode.NewGameFromScenario)
                return;

            InGame = true;
            PropCatalog.Build();

            var tool = ToolsModifierControl.toolController.gameObject.AddComponent<PlaygroundTool>();
            tool.enabled = false;

            PlaygroundButton.Create();
            SimulationManager.instance.AddAction(ZoneManager.OnLevelLoadedSim);
        }

        public override void OnLevelUnloading()
        {
            if (!InGame) return;
            InGame = false;

            PlaygroundButton.Destroy();
            if (PlaygroundTool.Instance != null) UnityEngine.Object.Destroy(PlaygroundTool.Instance);
            PlaygroundTool.ClearInstance();

            ActorManager.Clear();
            ZoneManager.Clear();
        }
    }
}
