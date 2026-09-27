using System.Collections.Generic;
using ColossalFramework.Math;
using UnityEngine;

namespace PlaygroundMod
{
    /// <summary>
    /// 範囲指定ツール
    ///  左ドラッグ         : 矩形で範囲指定
    ///  左クリック         : 多角形の頂点を追加（始点クリック or 右クリックで確定）
    ///  エリア上で Delete  : そのエリアと遊具を削除
    ///  エリア上で R       : 遊具を別の組み合わせで置き直し
    ///  Esc               : 入力中の頂点を破棄 / ツール終了
    /// </summary>
    public class PlaygroundTool : ToolBase
    {
        public static PlaygroundTool Instance { get; private set; }

        readonly List<Vector3> m_points = new List<Vector3>();
        Ray m_ray;
        float m_rayLength;
        volatile bool m_rayValid;
        Vector3 m_mousePos;
        volatile bool m_mouseValid;

        bool m_pressing, m_dragging;
        Vector3 m_pressPos;
        int m_hoverZone;

        static readonly Color ColZone = new Color(0.3f, 1f, 0.5f, 0.6f);
        static readonly Color ColHover = new Color(1f, 0.35f, 0.3f, 0.9f);
        static readonly Color ColEdit = new Color(0.3f, 0.7f, 1f, 0.9f);
        static readonly Color ColGate = new Color(1f, 0.85f, 0.2f, 0.8f);
        static readonly Color ColPath = new Color(0.9f, 0.9f, 0.9f, 0.5f);

        public static void ClearInstance() => Instance = null;

        public static void Toggle()
        {
            if (Instance == null) return;
            var tc = ToolsModifierControl.toolController;
            if (tc.CurrentTool == Instance) ToolsModifierControl.SetTool<DefaultTool>();
            else tc.CurrentTool = Instance;
        }

        protected override void Awake()
        {
            base.Awake();
            Instance = this;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            ResetInput();
            PlaygroundButton.SetActive(true);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ResetInput();
            ShowToolInfo(false, null, Vector3.zero);
            PlaygroundButton.SetActive(false);
        }

        void ResetInput()
        {
            m_points.Clear();
            m_pressing = m_dragging = false;
        }

        protected override void OnToolUpdate()
        {
            base.OnToolUpdate();

            Camera cam = Camera.main;
            m_ray = cam.ScreenPointToRay(Input.mousePosition);
            m_rayLength = cam.farClipPlane;
            m_rayValid = !m_toolController.IsInsideUI && Cursor.visible;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (m_points.Count > 0 || m_pressing) ResetInput();
                else ToolsModifierControl.SetTool<DefaultTool>();
                return;
            }
            if (!m_rayValid) { ShowToolInfo(false, null, Vector3.zero); return; }

            bool valid = m_mouseValid;
            Vector3 mouse = m_mousePos;
            m_hoverZone = valid && m_points.Count == 0 && !m_dragging ? ZoneManager.FindAt(mouse) : 0;

            if (Input.GetMouseButtonDown(0) && valid)
            {
                m_pressing = true;
                m_dragging = false;
                m_pressPos = mouse;
            }
            if (m_pressing && Input.GetMouseButton(0) && valid && m_points.Count == 0 && XZDist(m_pressPos, mouse) > 3f)
                m_dragging = true;

            if (m_pressing && Input.GetMouseButtonUp(0))
            {
                m_pressing = false;
                if (m_dragging)
                {
                    m_dragging = false;
                    if (valid) CommitRect(m_pressPos, mouse);
                }
                else if (valid)
                {
                    if (m_points.Count >= 3 && XZDist(m_points[0], mouse) < 3f) CommitPolygon();
                    else m_points.Add(mouse);
                }
            }

            if (Input.GetMouseButtonDown(1))
            {
                if (m_points.Count >= 3) CommitPolygon();
                else m_points.Clear();
            }

            if (m_hoverZone != 0 && !m_pressing)
            {
                int id = m_hoverZone;
                if (Input.GetKeyDown(KeyCode.Delete))
                    SimulationManager.instance.AddAction(() => ZoneManager.RemoveZone(id));
                else if (Input.GetKeyDown(KeyCode.R))
                    SimulationManager.instance.AddAction(() => ZoneManager.Reroll(id));
            }

            UpdateInfo(valid, mouse);
        }

        void UpdateInfo(bool valid, Vector3 mouse)
        {
            if (!valid) { ShowToolInfo(false, null, Vector3.zero); return; }
            string text;
            if (PropCatalog.PlayCount == 0)
                text = Loc.T("tip_no_props");
            else if (m_dragging)
                text = $"{Mathf.Abs(mouse.x - m_pressPos.x):0}m x {Mathf.Abs(mouse.z - m_pressPos.z):0}m";
            else if (m_points.Count > 0)
                text = string.Format(Loc.T("tip_points"), m_points.Count);
            else if (m_hoverZone != 0)
                text = Loc.T("tip_hover");
            else
                text = Loc.T("tip_idle");
            ShowToolInfo(true, text, mouse);
        }

        void CommitRect(Vector3 a, Vector3 b)
        {
            var poly = new[]
            {
                new Vector2(a.x, a.z), new Vector2(b.x, a.z),
                new Vector2(b.x, b.z), new Vector2(a.x, b.z),
            };
            SimulationManager.instance.AddAction(() => ZoneManager.CreateZone(poly));
        }

        void CommitPolygon()
        {
            var poly = new Vector2[m_points.Count];
            for (int i = 0; i < poly.Length; i++) poly[i] = new Vector2(m_points[i].x, m_points[i].z);
            m_points.Clear();
            SimulationManager.instance.AddAction(() => ZoneManager.CreateZone(poly));
        }

        public override void SimulationStep()
        {
            if (!m_rayValid) { m_mouseValid = false; return; }
            var input = new RaycastInput(m_ray, m_rayLength);
            if (RayCast(input, out RaycastOutput output))
            {
                m_mousePos = output.m_hitPos;
                m_mouseValid = true;
            }
            else m_mouseValid = false;
        }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);

            lock (ZoneManager.Lock)
            {
                foreach (var z in ZoneManager.Zones)
                {
                    Color c = z.Id == m_hoverZone ? ColHover : ColZone;
                    for (int i = 0; i < z.Polygon.Length; i++)
                    {
                        Vector2 a = z.Polygon[i], b = z.Polygon[(i + 1) % z.Polygon.Length];
                        DrawLine(cameraInfo, c, new Vector3(a.x, 0f, a.y), new Vector3(b.x, 0f, b.y), 1f);
                    }
                    foreach (var g in z.Gates)
                        DrawCircle(cameraInfo, ColGate, new Vector3(g.Pos.x, 0f, g.Pos.y), ModSettings.Instance.GateWidth);
                    foreach (var path in z.Paths)
                        for (int i = 0; i + 1 < path.Length; i++)
                            DrawLine(cameraInfo, ColPath, new Vector3(path[i].x, 0f, path[i].y), new Vector3(path[i + 1].x, 0f, path[i + 1].y), 0.6f);
                    var ring = z.Ring;
                    for (int i = 0; i < ring.Length; i++)
                    {
                        Vector2 a = ring[i], b = ring[(i + 1) % ring.Length];
                        DrawLine(cameraInfo, ColPath, new Vector3(a.x, 0f, a.y), new Vector3(b.x, 0f, b.y), 0.6f);
                    }
                }
            }

            if (!m_mouseValid) return;
            Vector3 mouse = m_mousePos;

            if (m_dragging)
            {
                Vector3 a = m_pressPos, b = mouse;
                var p1 = new Vector3(a.x, 0f, a.z); var p2 = new Vector3(b.x, 0f, a.z);
                var p3 = new Vector3(b.x, 0f, b.z); var p4 = new Vector3(a.x, 0f, b.z);
                DrawLine(cameraInfo, ColEdit, p1, p2, 1.5f); DrawLine(cameraInfo, ColEdit, p2, p3, 1.5f);
                DrawLine(cameraInfo, ColEdit, p3, p4, 1.5f); DrawLine(cameraInfo, ColEdit, p4, p1, 1.5f);
            }
            else if (m_points.Count > 0)
            {
                for (int i = 0; i < m_points.Count - 1; i++) DrawLine(cameraInfo, ColEdit, m_points[i], m_points[i + 1], 1.5f);
                DrawLine(cameraInfo, ColEdit, m_points[m_points.Count - 1], mouse, 1f);
                if (m_points.Count >= 2) DrawLine(cameraInfo, ColEdit * 0.6f, mouse, m_points[0], 0.7f);
                DrawCircle(cameraInfo, ColEdit, m_points[0], 3f);
            }

            DrawCircle(cameraInfo, ColEdit, mouse, 2f);
        }

        static void DrawLine(RenderManager.CameraInfo cam, Color c, Vector3 a, Vector3 b, float width)
        {
            ToolManager.instance.m_drawCallData.m_overlayCalls++;
            RenderManager.instance.OverlayEffect.DrawSegment(cam, c, new Segment3(a, b), width, 0f, -1f, 1280f, false, true);
        }

        static void DrawCircle(RenderManager.CameraInfo cam, Color c, Vector3 pos, float size)
        {
            ToolManager.instance.m_drawCallData.m_overlayCalls++;
            RenderManager.instance.OverlayEffect.DrawCircle(cam, c, pos, size, -1f, 1280f, false, true);
        }

        static float XZDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
