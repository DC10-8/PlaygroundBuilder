using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace PlaygroundMod
{
    public class PlaygroundData : SerializableDataExtensionBase
    {
        const string Key = "KuniePlaygroundMod";

        public override void OnSaveData()
        {
            serializableDataManager.SaveData(Key, ZoneManager.Serialize());
        }

        public override void OnLoadData()
        {
            ZoneManager.Clear();
            byte[] data = serializableDataManager.LoadData(Key);
            if (data != null) ZoneManager.Deserialize(data);
        }
    }

    public class PlaygroundThreading : ThreadingExtensionBase
    {
        public override void OnAfterSimulationTick()
        {
            if (Loading.InGame)
            {
                ActorManager.SimulationTick();
                ParkGuard.SimulationTick();
            }
        }

        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            if (!Loading.InGame || UIView.HasInputFocus()) return;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.P)) PlaygroundTool.Toggle();
        }
    }

    public static class PlaygroundButton
    {
        static UIButton s_btn;

        public static void Create()
        {
            var view = UIView.GetAView();
            if (view == null) return;
            s_btn = (UIButton)view.AddUIComponent(typeof(UIButton));
            s_btn.name = "KuniePlaygroundButton";
            s_btn.text = Loc.T("btn_text");
            s_btn.tooltip = Loc.T("btn_tooltip");
            s_btn.size = new Vector2(60f, 32f);
            s_btn.textScale = 0.9f;
            s_btn.normalBgSprite = "ButtonMenu";
            s_btn.hoveredBgSprite = "ButtonMenuHovered";
            s_btn.pressedBgSprite = "ButtonMenuPressed";
            s_btn.focusedBgSprite = "ButtonMenu";
            s_btn.absolutePosition = new Vector3(120f, 80f);
            s_btn.eventClick += (c, e) => PlaygroundTool.Toggle();
        }

        public static void SetActive(bool on)
        {
            if (s_btn != null) s_btn.normalBgSprite = on ? "ButtonMenuFocused" : "ButtonMenu";
        }

        /// <summary>言語切り替え時に呼ばれる</summary>
        public static void Relabel()
        {
            if (s_btn == null) return;
            s_btn.text = Loc.T("btn_text");
            s_btn.tooltip = Loc.T("btn_tooltip");
        }

        public static void Destroy()
        {
            if (s_btn == null) return;
            Object.Destroy(s_btn.gameObject);
            s_btn = null;
        }
    }
}
