using System;
using System.Collections.Generic;
using Game;
using Game.Prefabs;
using Game.Rendering;
using TMPro;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M3 气泡层 v2（自烘焙 TMP 文字网格 + 自挂 SRP 渲染回调）。
    ///
    /// v1（Buffer.DrawText）实机死因（2026-08-21 截图 + 反编译双实锤，勿复探）：
    /// - DrawText 无尺寸参：文字网格由 OverlayRenderSystem 全局共享 TMP 以 fontSize=200 懒烘焙、
    ///   绘制 matrix scale=1 硬编码 → 出来就是"区名牌"级巨字（roadmod 反编译逐行实锤）；
    /// - OverlayRenderSystem 只按曲线/网格计数开渲染通道、文字不计数 → 悬停建筑（游戏自己画框）
    ///   通道才开 → 我们的文字"悬停才显示、放大没了、俯仰角玄学"。
    ///
    /// v2 路线（NodeController / AllSpeedLimits / RoadSpeedAdjuster 三家已发布 mod 同款，勿再探）：
    /// - 烘焙：借游戏共享 TMP（自带 CJK fallback 链，中文零风险）ForceMeshUpdate 烘出文字网格；
    ///   材质 = clone OverlayConfigurationPrefab.m_TextMaterial（**永不 Shader.Find**——HDRP/Unlit
    ///   不在发布 build，已实锤），再 CopyFontAtlasParameters 灌图集参数，_FaceColor 按类型上色；
    /// - 绘制：RenderPipelineManager.beginContextRendering 自绘，billboard 朝向镜头 + 恒定屏占
    ///   （目标像素高 → 世界高 = 2·dist·tan(fov/2)·px/屏高，夹 [0.35, 5]m——ASL 同款换算）；
    /// - **铁律：烘前快照共享 TMP 八项状态、finally 恢复**（ASL 血泪教训：残留会污染游戏自身的
    ///   路名/区名烘焙缓存——v1"窗口期改字号把字搞没"即此坑）；
    /// - hideOverlay：不压不设，只记状态跳变日志（v4.2 实机：正常游玩也可能为 True，跟着它门控
    ///   = 自杀；拍照/建造自动隐藏待找正确信号，进 backlog）。
    ///
    /// 键位：Ctrl+9 开关；Ctrl+8 数量档（30/60/120）。
    /// 扩展口（正式版待办）：①贴图底板/边框——通知图标管线混合方案（Texture2DArray +
    ///   Shader Graphs/NotificationIcon，TLE 先例）；②内容管道接入（信息层降级产物+共位小剧场）；
    ///   ③LOD 阈值按"人清晰可见"实机标定（§12 #41）。
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        private const int k_MaxBubbles = 120;
        private const float k_MaxDist = 800f;        // 单泡距镜头上限（采样/绘制两用）
        private const float k_LodMaxFocusDist = 1000f; // 镜头到落点距离上限（宽放；终值实机标定）
        private const float k_TargetPixels = 26f;    // 文字目标屏占高（像素）
        private const float k_MinWorldH = 0.35f;     // 文字世界高下限（街景不至于糊脸上）
        private const float k_MaxWorldH = 5f;        // 上限（远看不成区名牌）
        private const int k_CacheCap = 48;           // 文字网格缓存上限（LRU 逐出，防漏）

        private OverlayRenderSystem m_Overlay = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private CameraUpdateSystem m_CameraUpdate = default!;
        private EntityQuery m_HumanQuery = default!;
        private EntityQuery m_CarQuery = default!;
        private EntityQuery m_BuildingQuery = default!;
        private EntityQuery m_ConfigQuery = default!;

        private bool m_Active;
        private int m_Level = 2;                    // 0=30 1=60 2=120
        private readonly List<TrackedBubble> m_Bubbles = new();
        private uint m_Frame;
        private uint m_LastKeyFrame;
        private float m_FpsAccum;
        private int m_FpsFrames;
        private float m_FpsTimer;

        // 文字网格缓存（key=文案×类型）。基底材质懒取——主菜单世界没有配置单例，急了会炸。
        private Material m_BaseTextMaterial = null!;
        private bool m_MaterialWarned;
        private readonly Dictionary<(string text, byte kind), BakedText> m_Cache = new();
        private bool m_LoggedFirstBake;
        private bool m_LoggedFirstRender;
        private bool m_LastHideOverlay;
        private float m_LastLodLog = -999f;

        /// <summary>一个被追踪的气泡：锚点实体 + 当前文案 + 独立生命周期。</summary>
        private struct TrackedBubble
        {
            public Entity Anchor;
            public byte Kind;       // 0 人 1 车 2 楼
            public string Text;     // 当前文案（Add 前必走 SetBubbleText，不会读到 null）
            public int TextIdx;
            public float NextAt;    // 下次换文案时刻（Time.time）
        }

        /// <summary>一段烘焙好的文字：网格+材质对（CJK fallback 可能多段）+ 合并包围盒参数。</summary>
        private sealed class BakedText
        {
            public readonly List<(Mesh mesh, Material mat)> Parts = new();
            public Vector3 Center;  // 合并包围盒中心（绘制时平移抵消，让文字正中落在锚点上）
            public float Height;    // 合并包围盒高（缩放归一分母）
            public float LastUsed;
        }

        // 占位文案池（正式版换内容管道；按类型分池：公园/住宅已分，载具类型全分待正式版）
        private static readonly string[] k_Texts =
            { "……", "吃了吗", "今天这公交又晚点了，离谱", "风好大", "快走要迟到了", "这店排队也太长了", "听说东区新开了家店" };
        private static readonly string[] k_CarTexts =
            { "嘀嘀——", "又堵了", "轰——", "前面路口慢点" };
        private static readonly string[] k_BuildingTexts =
            { "……", "晚饭吃啥", "电视小点声！", "装修第三天了", "快递放门口", "楼上又拖椅子" };
        private static readonly string[] k_ParkTexts =
            { "风一吹真舒服", "鸽子真多", "遛弯第三圈了", "这花开得不错" };

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // 相机走游戏自己的 CameraUpdateSystem.activeCamera（权威源；采样用，绘制用回调给的相机）
            m_CameraUpdate = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_HumanQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Creatures.Human>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_CarQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Vehicles.Car>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Vehicles.ParkedCar>(), // 空车不说话（说话的是车里的人）
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadOnly<OverlayConfigurationData>());
            RenderPipelineManager.beginContextRendering += OnRender;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

        protected override void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= OnRender;
            DestroyCache();
            m_Bubbles.Clear();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha9)) { m_LastKeyFrame = m_Frame; Toggle(); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha8)) { m_LastKeyFrame = m_Frame; CycleLevel(); }

            // FPS 计：每 4 秒一行（开着才有意义）
            m_FpsAccum += UnityEngine.Time.deltaTime;
            m_FpsFrames++;
            m_FpsTimer += UnityEngine.Time.deltaTime;
            if (m_FpsTimer >= 4f)
            {
                if (m_Active)
                    Mod.Log.Info($"[BubbleW] FPS avg={(m_FpsFrames / m_FpsAccum):F1}（N={LevelCount()}，在场 {m_Bubbles.Count}，缓存 {m_Cache.Count}）");
                m_FpsAccum = 0;
                m_FpsFrames = 0;
                m_FpsTimer = 0f;
            }

            if (!m_Active)
            {
                m_Frame++;
                return;
            }

            var cam = m_CameraUpdate.activeCamera != null ? m_CameraUpdate.activeCamera
                : Camera.main != null ? Camera.main
                : Camera.allCameras.Length > 0 ? Camera.allCameras[0] : null;
            if (cam == null)
            {
                m_Frame++;
                return;
            }

            if (m_Frame % 512 == 0)
                Resample(cam);
            TickLifecycle();

            // 基底材质懒取 + 兜底补烘（取材质失败时 SetBubbleText 的烘焙会被跳过，这里兜住）
            if (m_BaseTextMaterial == null)
                TryInitMaterial();
            if (m_BaseTextMaterial != null)
                foreach (var b in m_Bubbles)
                    if (!m_Cache.ContainsKey((b.Text, b.Kind)))
                        EnsureBaked(b.Text, b.Kind);

            m_Frame++;
        }

        private int LevelCount() => m_Level == 0 ? 30 : m_Level == 1 ? 60 : 120;

        private void Toggle()
        {
            m_Active = !m_Active;
            if (!m_Active)
            {
                m_Bubbles.Clear();
                DestroyCache();
            }
            else
            {
                m_Bubbles.Clear(); // 强制重采样建组
            }
            Mod.Log.Info($"[BubbleW] 气泡层 {(m_Active ? "开启" : "关闭")}（N={LevelCount()}）");
        }

        private void CycleLevel()
        {
            m_Level = (m_Level + 1) % 3;
            m_Bubbles.Clear();
            Mod.Log.Info($"[BubbleW] 数量档 → {LevelCount()}");
        }

        // —— 采样：三类锚点，屏内+距离上限，人:车:楼 配比 ——
        private void Resample(Camera cam)
        {
            // 留旧：锚点还在屏内的气泡保留（生命周期/文案不中断）；出屏/死亡的清掉
            for (int i = m_Bubbles.Count - 1; i >= 0; i--)
            {
                var b = m_Bubbles[i];
                if (!EntityManager.Exists(b.Anchor) || !EntityManager.HasComponent<Transform>(b.Anchor)
                    || !OnScreen(cam, b.Anchor))
                    m_Bubbles.RemoveAt(i);
            }

            var want = LevelCount();
            Collect(cam, m_CarQuery, Math.Max(4, want / 6), 1);
            Collect(cam, m_BuildingQuery, Math.Max(3, want / 10), 2);
            Collect(cam, m_HumanQuery, want, 0);
        }

        private bool OnScreen(Camera cam, Entity e)
        {
            var p = EntityManager.GetComponentData<Transform>(e).m_Position;
            var s = cam.WorldToScreenPoint(p);
            return s.z > 5f && s.z <= k_MaxDist
                && s.x >= 0 && s.x <= Screen.width && s.y >= 0 && s.y <= Screen.height;
        }

        private void Collect(Camera cam, EntityQuery query, int cap, byte kind)
        {
            var current = m_Bubbles.Count;
            if (current >= LevelCount())
                return;
            var arr = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            var scored = new List<(float d, Entity e)>(arr.Length);
            foreach (var e in arr)
            {
                if (HasAnchor(e))
                    continue;
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                var s = cam.WorldToScreenPoint(p);
                if (s.z < 5f || s.z > k_MaxDist)
                    continue;
                if (s.x < 0 || s.x > Screen.width || s.y < 0 || s.y > Screen.height)
                    continue;
                scored.Add((s.z, e));
            }
            arr.Dispose();
            scored.Sort((a, b) => a.d.CompareTo(b.d));
            var now = UnityEngine.Time.time;
            for (int i = 0; i < scored.Count && i < cap && m_Bubbles.Count < k_MaxBubbles; i++)
            {
                var b = new TrackedBubble
                {
                    Anchor = scored[i].e,
                    Kind = kind,
                    TextIdx = 0,
                    NextAt = now + HoldFor(scored[i].e.Index, 0),
                };
                SetBubbleText(ref b, 0);
                m_Bubbles.Add(b);
            }
        }

        private bool HasAnchor(Entity e)
        {
            foreach (var b in m_Bubbles)
                if (b.Anchor == e)
                    return true;
            return false;
        }

        // —— 生命周期：各气泡独立时钟（6-15s 错相，绝不同时切换）——
        private void TickLifecycle()
        {
            var now = UnityEngine.Time.time;
            for (int i = 0; i < m_Bubbles.Count; i++)
            {
                var b = m_Bubbles[i];
                if (!EntityManager.Exists(b.Anchor))
                    continue; // 出组清理由 Resample 负责
                if (now >= b.NextAt)
                {
                    b.TextIdx++;
                    b.NextAt = now + HoldFor(b.Anchor.Index, b.TextIdx);
                    SetBubbleText(ref b, b.TextIdx);
                    m_Bubbles[i] = b;
                }
            }
        }

        private void SetBubbleText(ref TrackedBubble b, int textIdx)
        {
            var pool = b.Kind == 0 ? k_Texts
                : b.Kind == 1 ? k_CarTexts
                : EntityManager.HasComponent<Game.Buildings.AttractivenessProvider>(b.Anchor) ? k_ParkTexts
                : k_BuildingTexts;
            b.Text = pool[(b.Anchor.Index + textIdx) % pool.Length];
            if (m_BaseTextMaterial != null)
                EnsureBaked(b.Text, b.Kind);
        }

        /// <summary>气泡驻留时长（6-15s，确定性错相：实体×集数散列——全屏绝不同时切换）。</summary>
        private static float HoldFor(int entityIndex, int textIdx)
            => 6f + ((entityIndex * 7919 + textIdx * 104729) % 900) / 100f;

        /// <summary>视线落点（x/z 作搜索圆心；y 只作参照——真实高度借实体 Transform，地形 y 不可信）。</summary>
        private static float3 FocusGround(Camera cam)
        {
            var camPos = cam.transform.position;
            var fwd = cam.transform.forward;
            var t = fwd.y < -0.001f ? camPos.y / -fwd.y : 100f;
            return (float3)(camPos + fwd * t);
        }

        private static Color KindColor(byte kind)
            => kind == 0 ? Color.white
             : kind == 1 ? new Color(0.8f, 0.9f, 1f)
             : new Color(1f, 0.95f, 0.75f);

        // —— 材质基底：clone 游戏 overlay 文字材质（永不 Shader.Find——build 里没有的已实锤）——
        private void TryInitMaterial()
        {
            try
            {
                if (m_ConfigQuery.IsEmptyIgnoreFilter)
                {
                    if (!m_MaterialWarned)
                    {
                        m_MaterialWarned = true;
                        Mod.Log.Warn("[BubbleW] OverlayConfigurationData 单例未就绪（主菜单？），材质待重试");
                    }
                    return;
                }
                var e = m_ConfigQuery.GetSingletonEntity();
                if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase pb) && pb is OverlayConfigurationPrefab cfg
                    && cfg.m_TextMaterial != null)
                {
                    m_BaseTextMaterial = new Material(cfg.m_TextMaterial);
                    Mod.Log.Info("[BubbleW] 文字基底材质已克隆（OverlayConfigurationPrefab.m_TextMaterial）");
                }
                else if (!m_MaterialWarned)
                {
                    m_MaterialWarned = true;
                    Mod.Log.Warn("[BubbleW] OverlayConfigurationPrefab.m_TextMaterial 取不到，材质待重试");
                }
            }
            catch (Exception ex)
            {
                if (!m_MaterialWarned)
                {
                    m_MaterialWarned = true;
                    Mod.Log.Warn($"[BubbleW] 取文字基底材质异常：{ex.Message}");
                }
            }
        }

        // —— 烘焙：借游戏共享 TMP 烘文字网格；八项状态快照+finally 恢复（污染全城标签的坑，勿拆）——
        private void EnsureBaked(string text, byte kind)
        {
            var key = (text, kind);
            if (m_Cache.ContainsKey(key) || m_BaseTextMaterial == null)
                return;
            var tmp = m_Overlay.GetTextMesh();
            if (tmp == null)
            {
                if (!m_MaterialWarned) { m_MaterialWarned = true; Mod.Log.Warn("[BubbleW] GetTextMesh() 为 null，烘焙跳过"); }
                return;
            }

            var rt = tmp.rectTransform;
            var oldSizeDelta = rt.sizeDelta;
            var oldFontSize = tmp.fontSize;
            var oldAlign = tmp.alignment;
            var oldColor = tmp.color;
            var oldSpacing = tmp.characterSpacing;
            var oldStyle = tmp.fontStyle;
            var oldText = tmp.text;
            var oldWrap = tmp.enableWordWrapping;
            try
            {
                tmp.enableWordWrapping = false;
                tmp.fontStyle = FontStyles.Normal;
                tmp.characterSpacing = 0f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white; // 颜色走材质 _FaceColor，顶点色不动
                tmp.fontSize = 200f;     // 烘焙大字号保 SDF 边缘质量；世界尺寸绘制时归一（ASL 同款）
                rt.sizeDelta = new Vector2(240f * text.Length + 400f, 400f); // 不折行保险
                tmp.text = text;
                tmp.ForceMeshUpdate(true, true);

                var info = tmp.textInfo;
                var entry = new BakedText();
                var bounds = new Bounds();
                var first = true;
                for (int i = 0; i < info.meshInfo.Length; i++)
                {
                    var mi = info.meshInfo[i];
                    if (mi.vertexCount == 0 || mi.mesh == null)
                        continue;
                    var src = mi.mesh;
                    var mesh = new Mesh
                    {
                        vertices = src.vertices,
                        triangles = src.triangles,
                        uv = src.uv,
                        uv2 = src.uv2,   // SDF 缩放信息在这通道，不拷会糊
                        colors32 = src.colors32,
                    };
                    mesh.RecalculateBounds();
                    var mat = new Material(m_BaseTextMaterial);
                    m_Overlay.CopyFontAtlasParameters(mi.material, mat);
                    mat.SetColor("_FaceColor", KindColor(kind));
                    entry.Parts.Add((mesh, mat));
                    if (first) { bounds = mesh.bounds; first = false; }
                    else bounds.Encapsulate(mesh.bounds);
                }
                if (entry.Parts.Count == 0)
                {
                    Mod.Log.Warn($"[BubbleW] 烘焙失败（无有效网格段）：'{text}'");
                    return;
                }
                entry.Center = bounds.center;
                entry.Height = math.max(bounds.size.y, 0.01f);
                entry.LastUsed = UnityEngine.Time.time;
                EvictIfNeeded();
                m_Cache[key] = entry;
                if (!m_LoggedFirstBake)
                {
                    m_LoggedFirstBake = true;
                    Mod.Log.Info($"[BubbleW] 首烘：'{text}' parts={entry.Parts.Count} 网格高={entry.Height:F1}（fontSize=200 烘焙，绘制归一）");
                }
            }
            finally
            {
                rt.sizeDelta = oldSizeDelta;
                tmp.fontSize = oldFontSize;
                tmp.alignment = oldAlign;
                tmp.color = oldColor;
                tmp.characterSpacing = oldSpacing;
                tmp.fontStyle = oldStyle;
                tmp.text = oldText;
                tmp.enableWordWrapping = oldWrap;
            }
        }

        private void EvictIfNeeded()
        {
            if (m_Cache.Count < k_CacheCap)
                return;
            (string, byte) oldest = default;
            var oldestT = float.MaxValue;
            foreach (var kv in m_Cache)
                if (kv.Value.LastUsed < oldestT) { oldestT = kv.Value.LastUsed; oldest = kv.Key; }
            if (m_Cache.Remove(oldest, out var victim))
                DestroyEntry(victim);
        }

        private void DestroyCache()
        {
            foreach (var kv in m_Cache)
                DestroyEntry(kv.Value);
            m_Cache.Clear();
        }

        private static void DestroyEntry(BakedText entry)
        {
            foreach (var (mesh, mat) in entry.Parts)
            {
                if (mesh != null) UnityEngine.Object.Destroy(mesh);
                if (mat != null) UnityEngine.Object.Destroy(mat);
            }
            entry.Parts.Clear();
        }

        // —— 绘制：SRP 回调自绘（billboard + 恒定屏占）。不再过 OverlayRenderSystem 通道，
        //    所以"文字不计数不开门""悬停才显示"这些 gating 与这里无关。——
        private void OnRender(ScriptableRenderContext ctx, List<Camera> cameras)
        {
            // hideOverlay 状态跳变日志（不门控——v4.2 实机正常游玩也可能 True，跟它=自杀）
            var rendering = World.GetExistingSystemManaged<RenderingSystem>();
            var hide = rendering != null && rendering.hideOverlay;
            if (hide != m_LastHideOverlay)
            {
                m_LastHideOverlay = hide;
                Mod.Log.Info($"[BubbleW] hideOverlay → {hide}（仅诊断，v2 不门控）");
            }

            if (!m_Active || m_Bubbles.Count == 0)
                return;

            foreach (var cam in cameras)
            {
                if (cam.cameraType != CameraType.Game)
                    continue;

                // LOD 闸（按镜头到落点距离，不按裸高度——俯仰角变化时高度不代表远近，实机踩坑）
                var focus = FocusGround(cam);
                var focusDist = math.distance(cam.transform.position, focus);
                if (focusDist > k_LodMaxFocusDist)
                {
                    if (UnityEngine.Time.time - m_LastLodLog > 10f)
                    {
                        m_LastLodLog = UnityEngine.Time.time;
                        Mod.Log.Info($"[BubbleW] LOD 拦截：落点距离 {focusDist:F0}m > {k_LodMaxFocusDist:F0}m（zoom={m_CameraUpdate.zoom:F1}）");
                    }
                    continue;
                }

                var camPos = (float3)cam.transform.position;
                var rot = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
                var tanHalfFov = math.tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                foreach (var b in m_Bubbles)
                {
                    if (!EntityManager.Exists(b.Anchor) || !EntityManager.HasComponent<Transform>(b.Anchor))
                        continue;
                    if (!m_Cache.TryGetValue((b.Text, b.Kind), out var entry))
                        continue;
                    var p = EntityManager.GetComponentData<Transform>(b.Anchor).m_Position;
                    p.y += b.Kind == 0 ? 2.6f : b.Kind == 1 ? 2.8f : 12f; // 人头/车顶/楼顶（估值，正式版按包围盒）
                    var dist = math.distance(camPos, p);
                    if (dist > k_MaxDist)
                        continue;

                    // 恒定屏占：世界高 = 2·dist·tan(fov/2)·目标像素/屏高，夹 [0.35, 5]m
                    var worldH = math.clamp(2f * dist * tanHalfFov * k_TargetPixels / cam.pixelHeight,
                        k_MinWorldH, k_MaxWorldH);
                    var s = worldH / entry.Height;
                    var matrix = Matrix4x4.TRS((Vector3)p, rot, new Vector3(s, s, s))
                        * Matrix4x4.Translate(-entry.Center);
                    foreach (var (mesh, mat) in entry.Parts)
                        Graphics.DrawMesh(mesh, matrix, mat, 0, cam, 0, null, ShadowCastingMode.Off, false);
                    entry.LastUsed = UnityEngine.Time.time;
                }

                if (!m_LoggedFirstRender)
                {
                    m_LoggedFirstRender = true;
                    Mod.Log.Info($"[BubbleW] 首帧自绘（cam={cam.name}，落点距离 {focusDist:F0}m，在场 {m_Bubbles.Count}）");
                }
            }
        }
    }
}
