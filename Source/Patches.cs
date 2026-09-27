using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PlaygroundMod
{
    public static class Patcher
    {
        const string HarmonyId = "kunie.cs1.playgroundmod";
        static bool s_patched;

        public static void PatchAll()
        {
            if (s_patched) return;
            new Harmony(HarmonyId).PatchAll(typeof(Patcher).Assembly);
            s_patched = true;
        }

        public static void UnpatchAll()
        {
            if (!s_patched) return;
            new Harmony(HarmonyId).UnpatchAll(HarmonyId);
            s_patched = false;
        }
    }

    /// <summary>
    /// 1フレーム分の移動処理 SimulationStep(ushort, ref CitizenInstance, ref CitizenInstance.Frame, bool) を乗っ取る。
    /// HumanAI / ResidentAI のうち実際に宣言している方すべてにパッチ。
    /// 引数名に依存しないよう __0.. でアクセス。
    /// </summary>
    [HarmonyPatch]
    static class HumanFrameStepPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var args = new[]
            {
                typeof(ushort),
                typeof(CitizenInstance).MakeByRefType(),
                typeof(CitizenInstance.Frame).MakeByRefType(),
                typeof(bool),
            };
            foreach (var t in new[] { typeof(HumanAI), typeof(ResidentAI) })
            {
                var m = AccessTools.DeclaredMethod(t, "SimulationStep", args);
                if (m != null) yield return m;
            }
        }

        static bool Prefix(ushort __0, ref CitizenInstance __1, ref CitizenInstance.Frame __2)
        {
            return !ActorManager.StepActor(__0, ref __1, ref __2);
        }
    }

    /// <summary>
    /// 公園の敷地にかかる成長型の建物(住宅・商業・工業・オフィス)を作らせない。
    /// 区画からの成長も手動配置も最終的にここを通る。
    /// 対象: bool CreateBuilding(out ushort, ref Randomizer, BuildingInfo, Vector3 position, float angle, int length, uint buildIndex)
    /// </summary>
    [HarmonyPatch]
    static class CreateBuildingPatch
    {
        static MethodBase s_target;

        static bool Prepare()
        {
            s_target = typeof(BuildingManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "CreateBuilding" || m.ReturnType != typeof(bool)) return false;
                    var p = m.GetParameters();
                    return p.Length == 7 && p[0].ParameterType == typeof(ushort).MakeByRefType() &&
                           p[2].ParameterType == typeof(BuildingInfo) && p[3].ParameterType == typeof(Vector3) &&
                           p[4].ParameterType == typeof(float) && p[5].ParameterType == typeof(int);
                });
            if (s_target == null) Debug.LogWarning("[PlaygroundMod] BuildingManager.CreateBuilding not found; park protection disabled");
            return s_target != null;
        }

        static MethodBase TargetMethod() => s_target;

        static bool Prefix(ref ushort __0, BuildingInfo __2, Vector3 __3, float __4, int __5, ref bool __result)
        {
            if (!Loading.InGame) return true;
            var s = ModSettings.Instance;
            bool growable = ParkGuard.IsGrowable(__2);
            if (growable ? !s.BlockBuildingsInPark : !s.BlockPlopsInPark) return true;
            if (__2 == null || !ParkGuard.IntersectsPark(__3, __4, __2.m_cellWidth, __5)) return true;

            __0 = 0;
            __result = false;   // 作らない
            return false;
        }
    }

    /// <summary>
    /// 手動で建物を置くとき、公園にかかる位置なら赤いエラー表示にする(設定 BlockPlopsInPark がONのとき)。
    /// BuildingTool.CheckSpace はバージョンで引数が違うので、引数名/型で位置・角度・幅・奥行きを探す
    /// </summary>
    [HarmonyPatch]
    static class BuildingToolCheckSpacePatch
    {
        static readonly List<MethodBase> s_targets = new List<MethodBase>();

        static bool Prepare()
        {
            s_targets.Clear();
            foreach (var m in typeof(BuildingTool).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.Name != "CheckSpace" || m.ReturnType != typeof(ToolBase.ToolErrors)) continue;
                var p = m.GetParameters();
                if (p.Any(x => x.ParameterType == typeof(BuildingInfo)) && p.Any(x => x.ParameterType == typeof(Vector3)) &&
                    p.Any(x => x.Name == "angle") && p.Any(x => x.Name == "width") && p.Any(x => x.Name == "length"))
                    s_targets.Add(m);
            }
            if (s_targets.Count == 0) Debug.LogWarning("[PlaygroundMod] BuildingTool.CheckSpace not found; no error display for placed buildings");
            return s_targets.Count > 0;
        }

        static IEnumerable<MethodBase> TargetMethods() => s_targets;

        static void Postfix(ref ToolBase.ToolErrors __result, object[] __args, MethodBase __originalMethod)
        {
            if (!Loading.InGame || !ModSettings.Instance.BlockPlopsInPark) return;
            var ps = __originalMethod.GetParameters();
            BuildingInfo info = null; Vector3? pos = null; float angle = 0f; int width = 0, length = 0;
            for (int i = 0; i < ps.Length && i < __args.Length; i++)
            {
                var t = ps[i].ParameterType;
                if (t == typeof(BuildingInfo) && info == null) info = __args[i] as BuildingInfo;
                else if (t == typeof(Vector3) && pos == null) pos = (Vector3)__args[i];
                else if (ps[i].Name == "angle") angle = (float)__args[i];
                else if (ps[i].Name == "width") width = (int)__args[i];
                else if (ps[i].Name == "length") length = (int)__args[i];
            }
            if (info == null || pos == null || ParkGuard.IsGrowable(info)) return;
            if (ParkGuard.IntersectsPark(pos.Value, angle, width, length))
                __result |= ToolBase.ToolErrors.ObjectCollision;
        }
    }
}
