using System;
using System.Collections.Generic;
using Game;
using Game.Prefabs;
using Game.Rendering;
using TMPro;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Transform = Game.Objects.Transform;

namespace CityLife.GameBridge
{
    /// <summary>
    /// M3 气泡层 v4.0（自烘焙 TMP 文字网格 + 通知图标管线底板，SRP 回调自绘；执行层折行+按字计时）。
    ///
    /// 文字管线定案（勿复探）：
    /// - v1（Buffer.DrawText）判死：共享 TMP fontSize=200 懒烘焙+scale=1 硬编码=巨字；overlay 通道
    ///   文字不计数不开门="悬停才显示/俯仰角玄学"（2026-08-21 截图+反编译双实锤）。
    /// - v2 正路：烘焙借 GetTextMesh()（**快照八项状态+finally 恢复**，污染全城标签的坑勿拆），
    ///   材质 clone OverlayConfigurationPrefab.m_TextMaterial + CopyFontAtlasParameters，
    ///   绘制 beginContextRendering + Graphics.DrawMesh。v2.1 实机验证：跟随/视角全解。
    /// - 跟随读 InterpolatedTransform（模拟 Transform 是 tick 级，读它必卡）；显隐尺=到镜头距离
    ///   （k_MaxDist，勿再加落点距离闸——浅俯仰角误杀全屏）；hideOverlay 只记日志不门控。
    ///
    /// 底板长征全记录（勿复探）：
    /// - v2.2 通知图标管线：能画、深底白边观感对，但尺寸/锚点黑盒（plateH=9.26m 巨框错位），当时判死。
    /// - v2.3-v3.6 TMP 文字同路 SDF 底板：八版排除法走完（uv2/绕向/全家桶/贴图维度/贴图内容/网格/
    ///   缩放域失配修复/试色四套）——v3.5 起显形成功，但 WaterSourceName 是自发光标签 shader：
    ///   _EmissionColor=白是亮度地板（供体实采值 (1,1,1,1)），深色底板永远出不来，试色只剩
    ///   "透明/白"两态（2026-09-08 实机）。TMP 路线**观感**判死。
    /// - v4.0 定案：回归通知图标管线（观感已被 v2.2 实机证明），尺寸/锚点不反推，运行时标定键
    ///   实机选定：Ctrl+5 尺寸倍率、Ctrl+6 Y 下沉系数（抵消图标 shader 的图钉式抬高——v2.2 截图里
    ///   文字贴底板下缘实锤锚点在下）。TLE 先例契约：mesh ±1 quad + m_Params.x=2
    ///   （Cities2-TrafficLightsEnhancement/Systems/Overlay/RenderSystem.cs，AddIcon 原文）。
    ///
    /// 长文适配（沿用）：执行层折行（CJK 1 格/其余半格、13 格/行、≤4 行、溢出收"…"——排版是确定性
    ///   的活，不归 LLM）；内容不限字数（话痨/沉默是人格，全文归信息流）；屏占按行恒定；驻留按字数。
    ///
    /// 键位：Ctrl+9 开关；Ctrl+8 数量档（30/60/120）；Ctrl+7 底板开关；
    ///       Ctrl+5 底板尺寸档；Ctrl+6 底板 Y 补偿档（标定完砍键）。
    /// 扩展口（正式版待办）：①内容管道接入（信息层降级产物+共位小剧场）；②k_MaxDist 按
    ///   "人清晰可见"实机标定（§12 #41）；③密度/重叠治理（同屏上限+防叠）。
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        private const int k_MaxBubbles = 120;
        private const float k_MaxDist = 800f;        // 单泡距镜头上限（采样/绘制两用）——同时就是 LOD 尺子：
                                                     // 超出即人不可辨（§12 #41 的标定终值落在这个常量上）
        private const float k_TargetPixels = 26f;    // 文字目标屏占高（像素/行——多行按行数叠）
        private const float k_MinWorldH = 0.35f;     // 单行世界高下限（街景不至于糊脸上）
        private const float k_MaxWorldH = 5f;        // 单行上限（远看不成区名牌）
        private const int k_CacheCap = 48;           // 文字网格缓存上限（LRU 逐出，防漏）

        // 底板三档宽高比（S/M/L）；按文案宽高比就近取档，宁宽勿窄（宽了居中好看，窄了包不住字）
        private static readonly float[] k_PlateAspects = { 2.2f, 4f, 7.5f };
        private const int k_PlateTexW = 512;
        private const int k_PlateTexH = 128;

        // 底板标定档（图标 shader 尺寸/锚点黑盒的实机标定——v4.0 定案后砍到只剩一档常量）
        private static readonly float[] k_SizeRatios = { 1.5f, 1.7f, 2.0f, 2.3f };   // 底板高 = 文字块高 × 档（Ctrl+5）
        private static readonly float[] k_YDrops = { 0f, 0.35f, 0.5f, 0.7f, 1.0f };  // 底板下移 = 底板高 × 档（Ctrl+6）

        // 执行层排版：13 格/行（CJK 1 格、其余半格）、最多 4 行
        private const float k_WrapCells = 13f;
        private const int k_WrapMaxLines = 4;

        private OverlayRenderSystem m_Overlay = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private CameraUpdateSystem m_CameraUpdate = default!;
        private EntityQuery m_HumanQuery = default!;
        private EntityQuery m_CarQuery = default!;
        private EntityQuery m_BuildingQuery = default!;
        private EntityQuery m_ConfigQuery = default!;
        private EntityQuery m_IconConfigQuery = default!;

        private bool m_Active;
        private int m_Level = 2;                    // 0=30 1=60 2=120
        private readonly List<TrackedBubble> m_Bubbles = new();
        private uint m_Frame;
        private uint m_LastKeyFrame;
        private float m_FpsAccum;
        private int m_FpsFrames;
        private float m_FpsTimer;

        // 重采样触发器状态（视角大幅移动即重采样，解决"快移视角气泡刷新跟不上"）
        private Vector3 m_LastSamplePos;
        private Vector3 m_LastSampleFwd;
        private uint m_LastSampleFrame;

        // 文字网格缓存（key=文案×类型）。基底材质懒取——主菜单世界没有配置单例，急了会炸。
        private Material m_BaseTextMaterial = null!;
        private bool m_MaterialWarned;
        private readonly Dictionary<(string text, byte kind), BakedText> m_Cache = new();
        private bool m_LoggedFirstBake;
        private bool m_LoggedFirstRender;
        private bool m_LastHideOverlay;

        // 底板管线（通知图标管线：贴图/网格/缓冲是程序化内容 OnCreate 即建；材质懒取游戏图标材质）
        private bool m_PlateOn = true;
        private Material m_PlateMaterial = null!;
        private bool m_PlateMaterialWarned;
        private Texture2DArray m_PlateTex = null!;
        private Mesh[] m_PlateMeshes = null!;
        private ComputeBuffer[] m_PlateInstBuf = null!;
        private ComputeBuffer m_PlateArgs = null!;
        private readonly uint[] m_ArgsArray = new uint[5];
        private readonly List<NotificationIconBufferSystem.InstanceData>[] m_PlateData
            = { new(), new(), new() };
        private int m_InstanceBufferID;
        private bool m_LoggedFirstPlate;
        private int m_SizeRatioIdx = 1;             // k_SizeRatios 索引（Ctrl+5）
        private int m_YDropIdx = 2;                 // k_YDrops 索引（Ctrl+6；默认 0.5=图钉抬高对半补）

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
            public float Aspect;    // 合并包围盒宽/高（底板选档用）
            public int Lines;       // 执行层折行后的行数（屏占按行叠）
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
            m_IconConfigQuery = GetEntityQuery(ComponentType.ReadOnly<IconConfigurationData>());
            m_InstanceBufferID = Shader.PropertyToID("instanceBuffer"); // shader 侧结构化缓冲名（TLE 实锤）
            BuildPlateAssets();
            RenderPipelineManager.beginContextRendering += OnRender;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

        protected override void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= OnRender;
            DestroyCache();
            DestroyPlateAssets();
            m_Bubbles.Clear();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha9)) { m_LastKeyFrame = m_Frame; Toggle(); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha8)) { m_LastKeyFrame = m_Frame; CycleLevel(); }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha7))
            {
                m_LastKeyFrame = m_Frame;
                m_PlateOn = !m_PlateOn;
                Mod.Log.Info($"[BubbleW] 底板 {(m_PlateOn ? "开" : "关")}");
            }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha5))
            {
                m_LastKeyFrame = m_Frame;
                m_SizeRatioIdx = (m_SizeRatioIdx + 1) % k_SizeRatios.Length;
                Mod.Log.Info($"[BubbleW] 底板尺寸档 → ×{k_SizeRatios[m_SizeRatioIdx]}");
            }
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha6))
            {
                m_LastKeyFrame = m_Frame;
                m_YDropIdx = (m_YDropIdx + 1) % k_YDrops.Length;
                Mod.Log.Info($"[BubbleW] 底板 Y 补偿档 → ×{k_YDrops[m_YDropIdx]}");
            }

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

            // 重采样：周期兜底 + 视角大幅移动即触发（节流 30 帧——快移视角气泡跟不上的根治）
            var camMoved = (cam.transform.position - m_LastSamplePos).sqrMagnitude > 40f * 40f
                || Vector3.Angle(m_LastSampleFwd, cam.transform.forward) > 12f;
            if (m_Frame % 512 == 0 || (camMoved && m_Frame - m_LastSampleFrame > 30))
                Resample(cam);
            TickLifecycle();

            // 基底材质懒取 + 兜底补烘（取材质失败时 SetBubbleText 的烘焙会被跳过，这里兜住）
            if (m_BaseTextMaterial == null)
                TryInitMaterial();
            if (m_BaseTextMaterial != null)
                foreach (var b in m_Bubbles)
                    if (!m_Cache.ContainsKey((b.Text, b.Kind)))
                        EnsureBaked(b.Text, b.Kind);
            if (m_PlateOn && m_PlateMaterial == null)
                TryInitPlateMaterial();

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
            m_LastSamplePos = cam.transform.position;
            m_LastSampleFwd = cam.transform.forward;
            m_LastSampleFrame = m_Frame;

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
                };
                SetBubbleText(ref b, 0);
                b.NextAt = now + HoldFor(scored[i].e.Index, 0, b.Text.Length); // 时长依赖文案，须在 SetBubbleText 之后
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

        // —— 生命周期：各气泡独立时钟（按字数缩放 + 确定性错相，绝不同时切换）——
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
                    SetBubbleText(ref b, b.TextIdx);
                    b.NextAt = now + HoldFor(b.Anchor.Index, b.TextIdx, b.Text.Length);
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

        /// <summary>气泡驻留时长：阅读时间 4s 起、每字 +0.28s、封顶 30s（话痨段落让人读完），
        /// 再叠 0-4s 确定性抖动（实体×集数散列——全屏绝不同时切换）。</summary>
        private static float HoldFor(int entityIndex, int textIdx, int textLen)
        {
            var read = 4f + textLen * 0.28f;
            var jitter = ((entityIndex * 7919 + textIdx * 104729) % 400) / 100f;
            return math.min(read + jitter, 30f);
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

        /// <summary>执行层排版（铁律 #1：排版是确定性活，不归 LLM 也不赌 TMP 的 CJK 折行）：
        /// CJK 占 1 格、ASCII 等占半格，每行 ≤13 格，最多 4 行，溢出把末行末字换"…"。
        /// 内容层不限字数（话痨/沉默是人格），气泡只承诺"排得下、读得完"，全文归信息流。</summary>
        private static string WrapForBake(string text, out int lines)
        {
            var cur = new System.Text.StringBuilder(text.Length + 8);
            var list = new List<string>(k_WrapMaxLines);
            var cells = 0f;
            var overflow = false;
            foreach (var ch in text)
            {
                if (ch == '\r')
                    continue;
                if (ch == '\n') // 显式换行=强制满行
                {
                    list.Add(cur.ToString()); cur.Clear(); cells = 0f;
                    if (list.Count == k_WrapMaxLines) { overflow = true; break; }
                    continue;
                }
                var w = ch < 128 ? 0.5f : 1f;
                if (cells + w > k_WrapCells)
                {
                    list.Add(cur.ToString()); cur.Clear(); cells = 0f;
                    if (list.Count == k_WrapMaxLines) { overflow = true; break; }
                }
                cur.Append(ch);
                cells += w;
            }
            if (!overflow && cur.Length > 0)
                list.Add(cur.ToString());
            if (overflow)
            {
                var last = list[k_WrapMaxLines - 1];
                list[k_WrapMaxLines - 1] = last.Length > 1 ? last.Substring(0, last.Length - 1) + '…' : "…";
            }
            lines = Math.Max(1, list.Count);
            return string.Join("\n", list);
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
                var wrapped = WrapForBake(text, out var lines);
                tmp.enableWordWrapping = false; // 折行已由执行层完成（'\n'），TMP 只管排
                tmp.fontStyle = FontStyles.Normal;
                tmp.characterSpacing = 0f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white; // 颜色走材质 _FaceColor，顶点色不动
                tmp.fontSize = 200f;     // 烘焙大字号保 SDF 边缘质量；世界尺寸绘制时归一（ASL 同款）
                rt.sizeDelta = new Vector2(240f * k_WrapCells + 400f, 300f * k_WrapMaxLines); // 单行最宽×最大行高，溢出保险
                tmp.text = wrapped;
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
                    // 描边+柔影（TMP SDF 标准属性；shader 缺这些属性时 SetXXX 是无害空操作）
                    mat.SetFloat("_OutlineWidth", 0.22f);
                    mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 0.9f));
                    mat.SetFloat("_UnderlayDilate", 0.35f);
                    mat.SetFloat("_UnderlaySoftness", 0.55f);
                    mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.45f));
                    mat.renderQueue = 3800; // 恒在底板（3700）之上——两个透明层的确定序
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
                entry.Aspect = bounds.size.x / entry.Height;
                entry.Lines = lines;
                entry.LastUsed = UnityEngine.Time.time;
                EvictIfNeeded();
                m_Cache[key] = entry;
                if (!m_LoggedFirstBake)
                {
                    m_LoggedFirstBake = true;
                    Mod.Log.Info($"[BubbleW] 首烘：'{text}' parts={entry.Parts.Count} 网格高={entry.Height:F1} 宽高比={entry.Aspect:F2} 行={entry.Lines}（fontSize=200 烘焙，绘制归一）");
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

        // —— 底板资产：程序化圆角贴图（3 档宽高比各一片，圆角按档烘焙防拉伸变形）+ quad 网格。
        //    纯 CPU 内容，不碰游戏单例，OnCreate 就能建。——
        private void BuildPlateAssets()
        {
            try
            {
                m_PlateTex = new Texture2DArray(k_PlateTexW, k_PlateTexH, k_PlateAspects.Length,
                    TextureFormat.ARGB32, true);
                m_PlateMeshes = new Mesh[k_PlateAspects.Length];
                for (int i = 0; i < k_PlateAspects.Length; i++)
                {
                    var t = BakePlateTexture(k_PlateAspects[i]);
                    Graphics.CopyTexture(t, 0, m_PlateTex, i); // 源带完整 mip 链，拷贝随链走
                    UnityEngine.Object.Destroy(t);
                    m_PlateMeshes[i] = BuildPlateMesh(k_PlateAspects[i]);
                }
                m_PlateArgs = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
                m_PlateInstBuf = new ComputeBuffer[k_PlateAspects.Length];
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"[BubbleW] 底板资产构建失败：{ex.Message}");
            }
        }

        /// <summary>圆角底板贴图：世界单位域（高 1、宽=aspect）上算圆角矩形 SDF，逐像素填成品色
        /// （图标 shader 是普通采样管线，贴图就是最终颜色——v2.2 实机观感已证明）。</summary>
        private static Texture2D BakePlateTexture(float aspect)
        {
            var w = k_PlateTexW;
            var h = k_PlateTexH;
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, true);
            var px = new Color32[w * h];
            var fill = new Color(0.04f, 0.05f, 0.09f, 0.66f);   // 深底（文字主要靠描边，底只负责托住）
            var border = new Color(1f, 1f, 1f, 0.85f);           // 中性白边（类型色由文字承担）
            const float radius = 0.17f;   // 圆角半径（世界单位，高=1）
            const float bw = 0.045f;      // 边框宽
            const float aa = 0.012f;      // 边缘抗锯齿过渡带宽
            float hx = aspect * 0.5f;
            for (int y = 0; y < h; y++)
            {
                var v = (y + 0.5f) / h - 0.5f;
                for (int x = 0; x < w; x++)
                {
                    var u = (x + 0.5f) / w * aspect - hx;
                    // 圆角矩形 SDF（标准式）：d<0 内部
                    var qx = math.abs(u) - (hx - radius);
                    var qy = math.abs(v) - (0.5f - radius);
                    var d = math.length(math.max(new float2(qx, qy), float2.zero))
                        + math.min(math.max(qx, qy), 0f) - radius;
                    var cover = math.saturate(0.5f - d / aa); // 外边缘 AA
                    var c = d < -bw ? fill : border;
                    c.a *= cover;
                    px[y * w + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>底板 quad：宽高比由几何承载（shader 只给统一缩放），XY 平面、UV 0..1。
        /// 顶点顺序/绕向逐字照抄游戏原版 NotificationIconRenderSystem.GetMesh()（BL→TL→TR→BR，
        /// 三角形 0,1,2/2,3,0）——镜像绕向 billboard 后背对镜头被背面剔除，画了个寂寞（v2.2 实锤）。</summary>
        private static Mesh BuildPlateMesh(float aspect)
        {
            var hx = aspect * 0.5f;
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(-hx, -0.5f, 0f), new Vector3(-hx, 0.5f, 0f),
                    new Vector3(hx, 0.5f, 0f), new Vector3(hx, -0.5f, 0f),
                },
                uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(0f, 1f),
                    new Vector2(1f, 1f), new Vector2(1f, 0f),
                },
                triangles = new[] { 0, 1, 2, 2, 3, 0 },
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        // —— 底板材质：clone 游戏通知图标材质（TLE 同款契约）——
        private void TryInitPlateMaterial()
        {
            try
            {
                if (m_IconConfigQuery.IsEmptyIgnoreFilter)
                {
                    if (!m_PlateMaterialWarned)
                    {
                        m_PlateMaterialWarned = true;
                        Mod.Log.Warn("[BubbleW] IconConfigurationData 单例未就绪（主菜单？），底板材质待重试");
                    }
                    return;
                }
                var e = m_IconConfigQuery.GetSingletonEntity();
                if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase pb) && pb is IconConfigurationPrefab cfg
                    && cfg.m_Material != null)
                {
                    m_PlateMaterial = new Material(cfg.m_Material)
                    {
                        mainTexture = m_PlateTex, // renderQueue 不动：克隆自带 3700，恰在文字（3800）之下
                    };
                    Mod.Log.Info("[BubbleW] 底板材质已克隆（IconConfigurationPrefab.m_Material + 自建 Texture2DArray）");
                }
                else if (!m_PlateMaterialWarned)
                {
                    m_PlateMaterialWarned = true;
                    Mod.Log.Warn("[BubbleW] IconConfigurationPrefab.m_Material 取不到，底板材质待重试");
                }
            }
            catch (Exception ex)
            {
                if (!m_PlateMaterialWarned)
                {
                    m_PlateMaterialWarned = true;
                    Mod.Log.Warn($"[BubbleW] 取底板材质异常：{ex.Message}");
                }
            }
        }

        private void DestroyPlateAssets()
        {
            if (m_PlateTex != null) UnityEngine.Object.Destroy(m_PlateTex);
            if (m_PlateMeshes != null)
                foreach (var m in m_PlateMeshes)
                    if (m != null) UnityEngine.Object.Destroy(m);
            if (m_PlateMaterial != null) UnityEngine.Object.Destroy(m_PlateMaterial);
            if (m_PlateArgs != null) { m_PlateArgs.Release(); m_PlateArgs = null; }
            if (m_PlateInstBuf != null)
                foreach (var b in m_PlateInstBuf)
                    b?.Release();
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

            // 数组非空+末元素非空：BuildPlateAssets 中途抛异常会留下半空数组
            var drawPlate = m_PlateOn && m_PlateMaterial != null && m_PlateArgs != null
                && m_PlateMeshes != null && m_PlateMeshes[k_PlateAspects.Length - 1] != null;
            foreach (var cam in cameras)
            {
                if (cam.cameraType != CameraType.Game)
                    continue;

                var camPos = (float3)cam.transform.position;
                var rot = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
                var tanHalfFov = math.tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                if (drawPlate)
                    foreach (var list in m_PlateData)
                        list.Clear();

                foreach (var b in m_Bubbles)
                {
                    if (!EntityManager.Exists(b.Anchor))
                        continue;
                    if (!m_Cache.TryGetValue((b.Text, b.Kind), out var entry))
                        continue;
                    // 渲染帧读插值变换（游戏给镜头用的每帧平滑位）；模拟 Transform 是 tick 级——读它必卡
                    float3 p;
                    if (EntityManager.HasComponent<InterpolatedTransform>(b.Anchor))
                        p = EntityManager.GetComponentData<InterpolatedTransform>(b.Anchor).m_Position;
                    else if (EntityManager.HasComponent<Transform>(b.Anchor))
                        p = EntityManager.GetComponentData<Transform>(b.Anchor).m_Position;
                    else
                        continue;
                    p.y += b.Kind == 0 ? 2.6f : b.Kind == 1 ? 2.8f : 12f; // 人头/车顶/楼顶（估值，正式版按包围盒）
                    var dist = math.distance(camPos, p);
                    if (dist > k_MaxDist)
                        continue;

                    // 恒定屏占按"行"：单行世界高 = 2·dist·tan(fov/2)·目标像素/屏高，夹 [0.35, 5]m；
                    // 块高 = 行高 × 行数——话痨段落按行数长高，不会缩成蚂蚁
                    var lineH = math.clamp(2f * dist * tanHalfFov * k_TargetPixels / cam.pixelHeight,
                        k_MinWorldH, k_MaxWorldH);
                    var worldH = lineH * entry.Lines;
                    var s = worldH / entry.Height;
                    var matrix = Matrix4x4.TRS((Vector3)p, rot, new Vector3(s, s, s))
                        * Matrix4x4.Translate(-entry.Center);
                    foreach (var (mesh, mat) in entry.Parts)
                        Graphics.DrawMesh(mesh, matrix, mat, 0, cam, 0, null, ShadowCastingMode.Off, false);
                    entry.LastUsed = UnityEngine.Time.time;

                    if (drawPlate)
                    {
                        // 底板高 = 文字块高 × 尺寸档；Y 下沉抵消图标 shader 图钉式抬高（Ctrl+6 标定）
                        var plateH = worldH * k_SizeRatios[m_SizeRatioIdx];
                        // 选档宁宽勿窄：需求比 = 文案宽高比 ÷ 底板高倍率 × 横向余量
                        var need = entry.Aspect * worldH / plateH * 1.08f;
                        var bucket = k_PlateAspects.Length - 1;
                        for (int i = 0; i < k_PlateAspects.Length; i++)
                            if (k_PlateAspects[i] >= need) { bucket = i; break; }
                        m_PlateData[bucket].Add(new NotificationIconBufferSystem.InstanceData
                        {
                            m_Position = p - new float3(0f, plateH * k_YDrops[m_YDropIdx], 0f),
                            m_Params = new float4(plateH, 0f, 1f, 1f), // (全高米数, 不脉动, 不透明, 开)
                            m_Icon = bucket,
                            m_Distance = dist,
                        });
                    }
                }

                if (drawPlate)
                    DrawPlates(cam);

                if (!m_LoggedFirstRender)
                {
                    m_LoggedFirstRender = true;
                    Mod.Log.Info($"[BubbleW] 首帧自绘（cam={cam.name}，在场 {m_Bubbles.Count}）");
                }
            }
        }

        // —— 底板绘制：每档一次 instanced draw（远→近排序，透明叠序才对）——
        private void DrawPlates(Camera cam)
        {
            var stride = UnsafeUtility.SizeOf<NotificationIconBufferSystem.InstanceData>();
            for (int bucket = 0; bucket < k_PlateAspects.Length; bucket++)
            {
                var list = m_PlateData[bucket];
                if (list.Count == 0)
                    continue;
                list.Sort((a, b) => b.m_Distance.CompareTo(a.m_Distance)); // 远→近

                var buf = m_PlateInstBuf[bucket];
                if (buf == null || buf.count < list.Count)
                {
                    buf?.Release();
                    var cap = 64;
                    while (cap < list.Count) cap *= 2;
                    buf = new ComputeBuffer(cap, stride, ComputeBufferType.Default, ComputeBufferMode.Dynamic);
                    m_PlateInstBuf[bucket] = buf;
                }
                buf.SetData(list, 0, 0, list.Count);
                m_PlateMaterial.SetBuffer(m_InstanceBufferID, buf);

                var mesh = m_PlateMeshes[bucket];
                m_ArgsArray[0] = mesh.GetIndexCount(0);
                m_ArgsArray[1] = (uint)list.Count;
                m_ArgsArray[2] = mesh.GetIndexStart(0);
                m_ArgsArray[3] = mesh.GetBaseVertex(0);
                m_ArgsArray[4] = 0;
                m_PlateArgs.SetData(m_ArgsArray);

                // bounds 包住本档所有实例（唯一的剔除机制，半径余量按最大底板算）
                var bounds = new Bounds((Vector3)list[0].m_Position, Vector3.zero);
                foreach (var inst in list)
                    bounds.Encapsulate((Vector3)inst.m_Position);
                bounds.Expand(40f);

                Graphics.DrawMeshInstancedIndirect(mesh, 0, m_PlateMaterial, bounds, m_PlateArgs,
                    0, null, ShadowCastingMode.Off, false, 0, cam);

                if (!m_LoggedFirstPlate)
                {
                    m_LoggedFirstPlate = true;
                    Mod.Log.Info($"[BubbleW] 底板首画（bucket={bucket} N={list.Count} plateH={list[0].m_Params.x:F2}m 尺寸档×{k_SizeRatios[m_SizeRatioIdx]} Y补×{k_YDrops[m_YDropIdx]} 图标管线）");
                }
            }
        }
    }
}
