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
    /// M3 气泡层 v2.5（自烘焙 TMP 文字网格 + TMP 同路 SDF 底板，SRP 回调自绘；执行层折行+按字计时）。
    ///
    /// v1（Buffer.DrawText）实机死因（2026-08-21 截图 + 反编译双实锤，勿复探）：
    /// - DrawText 无尺寸参：文字网格由 OverlayRenderSystem 全局共享 TMP 以 fontSize=200 懒烘焙、
    ///   绘制 matrix scale=1 硬编码 → 出来就是"区名牌"级巨字（roadmod 反编译逐行实锤）；
    /// - overlay 通道按曲线/网格计数开门、文字不计数 → 不悬停建筑通道不开 → "悬停才显示/俯仰角玄学"；
    /// - 共享 TMP 状态污染会扩散全城标签（ASL 实锤）——借烘焙必须快照八项状态 finally 恢复（本版沿用）。
    ///
    /// v2 管线定案（2026-08-21 实机"有字"确认）：烘焙借 GetTextMesh()（快照八项+finally），
    /// 材质 clone OverlayConfigurationPrefab.m_TextMaterial + CopyFontAtlasParameters，
    /// 绘制走 beginContextRendering + Graphics.DrawMesh。v2.1 实机验证：跟随/视角问题全解。
    ///
    /// v2.1 修复链（实机逐条实锤，勿复探）：
    /// - 跟随读 InterpolatedTransform（渲染插值位）；模拟 Transform 是 tick 级，读它必卡（实锤）；
    /// - 显隐尺 = 每泡到镜头距离（k_MaxDist）；**不要再加落点距离闸**——浅俯仰角落点甩飞误杀全屏；
    /// - hideOverlay：不压不设，只记状态跳变日志（正常游玩也可能为 True，跟它门控=自杀）。
    ///
    /// v2.2 底板判死（2026-09-07 实机截图实锤，勿回头）：通知图标管线（clone IconConfigurationPrefab.
    ///   m_Material + InstanceData 缓冲 + DrawMeshInstancedIndirect）的 m_Params/锚点语义是黑盒，
    ///   反推"世界全高米数"实测画出 plateH=9.26m 巨框、与文字错位；该 shader 语义逐版本漂移，
    ///   属版本敏感面，按铁律 #4 收缩。
    /// v2.3 底板路线：改走 TMP 文字同路（材质 clone 同一文字基底、_MainTex 换自建圆角矩形 SDF、
    ///   矩阵与文字同一套 TRS）。实机判"画而不显"（9/8）：材质/绘制调用都发了我方 quad 却全透明，
    ///   头号嫌疑=uv2 通道（TMP 在该通道携带 SDF 缩放信息，v2 开发期已实锤"不拷会糊"；我填了 (0,0)）。
    /// v2.4：uv2 实采（游戏字形=（0, 0.22)，底板照抄）——实机仍"画而不显"（9/8），uv2 排除。
    ///   真凶收敛到材质参数：文字走的是 CopyFontAtlasParameters 灌的"图集参数全家桶"
    ///   （_GradientScale/_ScaleRatioA/B/关键字……），底板只 clone 基底 + SetTexture——漏了桶。
    /// v2.5：底板材质先 CopyPropertiesFromMaterial（活文字材质做供体，全家桶一个不落），
    ///   再覆写 _MainTex/尺寸/颜色/队列——实机仍"画而不显"（9/8），材质参数排除。
    /// v2.6 绕向实读翻车自纠：供体首三角=0,1,2/2,3,0 手性为负，与我方 quad 原序**同序同手性**
    ///   ——绕向从来不是病根（且 v2.6 符号臂选反，反而改拧了）。绕向正式排除。
    /// v2.7 贴图槽维度假设翻车（同日）：供体槽=Texture2D（数组假设死亡）；且 ReferenceEquals 读回
    ///   只证明属性袋收了对象、证明不了 shader 声明维度——数组塞进 2D 槽画时必死，回退 2D。
    ///   至此 uv2/绕向/参数全家桶/贴图维度全部排除，剩余变量=我方手搓 quad 网格 vs SDF 贴图本身。
    /// v2.8 对剖判决（9/8 实机）：A 臂（我方 quad+供体原样材质）显形=网格/矩阵/通路全好；
    ///   B 臂（供体字形+我方 SDF 材质）不显=我方材质/贴图坏。
    /// v2.9 二阶对剖：B 臂换"供体克隆仅换 _MainTex"（我方的颜色/描边/队列覆写全撤）——
    ///   显=凶手是我方参数覆写（下一版逐个放回定位）；不显=凶手是 SDF 贴图本身（转方块字形路线）。
    /// v2.4 同版上的长文适配（沿用）：
    /// - 折行是执行层的活（铁律 #1，不依赖 TMP 折行对 CJK 的怪癖）：CJK 1 格/其余 0.5 格、
    ///   13 格/行、最多 4 行、溢出末字换"…"。内容层不限字数——话痨/沉默是人格，全文归信息流；
    /// - 屏占按"行"恒定：块世界高 = 行高 × 行数，多行段落不会缩成蚂蚁；
    /// - 驻留时长按字数缩放（4s 起每字 +0.28s，封顶 30s）——长文让人读得完。
    ///
    /// 键位：Ctrl+9 开关；Ctrl+8 数量档（30/60/120）；Ctrl+7 底板开关。
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

        // 底板三档宽高比（S/M/L）；按折行后文案块宽高比就近取档，宁宽勿窄（宽了居中好看，窄了包不住字）
        private static readonly float[] k_PlateAspects = { 2.2f, 4f, 7.5f };
        private const int k_PlateTexW = 512;
        private const int k_PlateTexH = 128;
        private const int k_PlatePadPx = 24;         // SDF 过渡带像素（单边）；_GradientScale 由此换算

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
        private Vector2? m_GlyphUV2;                // 从游戏字形网格实采的 uv2（底板照抄，不猜）

        // 底板管线（TMP 同路：贴图/网格是程序化内容 OnCreate 即建；材质等文字基底+uv2 实采就位后克隆）
        private int m_PlateMode = 1;                // 0=关 1=SDF 底板 2=诊断对照（Ctrl+7 三态轮转）
        private Texture2D[] m_PlateTexs = null!;
        private Mesh[] m_PlateMeshes = null!;
        private Material[] m_PlateMats = null!;
        private Mesh m_DonorMesh = null!;           // 诊断臂用：供体字形网格/材质（BuildPlateMaterials 捕获）
        private Material m_DonorMat = null!;
        private Material[] m_BisectMats = null!;    // 诊断 B 臂：供体克隆仅换 _MainTex（我方的覆写全撤）
        private bool m_PlateMaterialWarned;
        private bool m_LoggedFirstPlate;

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
            public int Lines;       // 执行层折行后的行数（屏占/底板留白都按它算）
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
                m_PlateMode = (m_PlateMode + 1) % 3;
                Mod.Log.Info($"[BubbleW] 底板模式 → {(m_PlateMode == 0 ? "关" : m_PlateMode == 1 ? "SDF 底板" : "诊断对照（2×2 对剖）")}");
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
            // 底板材质等"文字基底+uv2 实采"双就位（同帧烘焙循环后必有 uv2）
            if (m_PlateMode != 0 && m_PlateMats == null && m_BaseTextMaterial != null && m_GlyphUV2.HasValue)
                BuildPlateMaterials();

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
                    if (!m_GlyphUV2.HasValue && src.uv2 != null && src.uv2.Length >= 4)
                        m_GlyphUV2 = src.uv2[0]; // 实采游戏字形的 uv2（底板 quad 照抄，不猜）
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
                    Mod.Log.Info($"[BubbleW] 首烘：'{text}' parts={entry.Parts.Count} 网格高={entry.Height:F1} 宽高比={entry.Aspect:F2} 行={entry.Lines} uv2={(m_GlyphUV2.HasValue ? m_GlyphUV2.Value.ToString() : "未采到")}（fontSize=200 烘焙，绘制归一）");
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

        // —— 底板资产：圆角矩形 SDF 贴图（3 档宽高比各一片，圆角按档烘焙防拉伸变形）+ quad 网格。
        //    纯 CPU 内容，不碰游戏单例，OnCreate 就能建。——
        private void BuildPlateAssets()
        {
            try
            {
                m_PlateTexs = new Texture2D[k_PlateAspects.Length];
                m_PlateMeshes = new Mesh[k_PlateAspects.Length];
                for (int i = 0; i < k_PlateAspects.Length; i++)
                {
                    m_PlateTexs[i] = BakePlateTexture(k_PlateAspects[i]);
                    m_PlateMeshes[i] = BuildPlateMesh(k_PlateAspects[i]);
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"[BubbleW] 底板资产构建失败：{ex.Message}");
            }
        }

        /// <summary>圆角底板贴图（喂 TMP SDF shader）：alpha=有向距离场（0.5=边、内 1 外 0），
        /// RGB 全白——填充色/描边色全走材质参数，贴图只供形状。</summary>
        private static Texture2D BakePlateTexture(float aspect)
        {
            var w = k_PlateTexW;
            var h = k_PlateTexH;
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, true);
            var px = new Color32[w * h];
            const float radius = 0.20f;                        // 圆角半径（世界单位，高=1）
            var padWorld = k_PlatePadPx / (float)k_PlateTexH;  // SDF 过渡带（世界单位）：以边为中心内外各一程
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
                    var a = (byte)(math.saturate(0.5f - d / (2f * padWorld)) * 255f);
                    px[y * w + x] = new Color32(255, 255, 255, a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>底板 quad：宽高比由几何承载（材质统一缩放），XY 平面、UV 0..1、顶点色白。
        /// uv2 先填 (0,0) 占位，材质构建时用游戏字形实采值覆写（v2.4：不猜，照抄）。
        /// 绕向沿用 v2.2.1 照抄游戏原版的序（BL→TL→TR→BR，0,1,2/2,3,0）——billboard 后以背面朝镜头，
        /// TMP shader Cull Off 本不挑绕向，保持原序无害。</summary>
        private static Mesh BuildPlateMesh(float aspect)
        {
            var hx = aspect * 0.5f;
            var white = new Color32(255, 255, 255, 255);
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
                uv2 = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero },
                colors32 = new[] { white, white, white, white },
                triangles = new[] { 0, 1, 2, 2, 3, 0 },
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        // —— 底板材质：clone 文字同款 TMP 基底 + CopyPropertiesFromMaterial 活文字材质（图集参数全家桶
        //    ——缩放比/GradientScale/关键字——一个不落；v2.4 只 clone 基底被判"画而不显"，漏的桶在这）。
        //    再覆写 _MainTex=自建 SDF 贴图 + 颜色/队列。等"文字基底+uv2 实采+至少一段烘焙（供体）"就位才建。——
        private void BuildPlateMaterials()
        {
            try
            {
                Mesh donorMesh = null;
                Material donor = null;
                foreach (var kv in m_Cache)
                    if (kv.Value.Parts.Count > 0) { donor = kv.Value.Parts[0].mat; donorMesh = kv.Value.Parts[0].mesh; break; }
                if (donor == null || donorMesh == null)
                    return; // 还没有烘焙产物做供体，下一帧再试（静默重试，不刷日志）

                // uv2 实采值覆写到底板 quad
                var g = m_GlyphUV2!.Value;
                foreach (var mesh in m_PlateMeshes)
                    mesh.uv2 = new[] { g, g, g, g };

                // 绕向实读供体手性（不猜）。注意符号臂映射：我方顶点序 BL/TL/TR/BR 下，
                // 0,1,2/2,3,0 算出来手性为负——供体为负就配它（v2.6 曾把臂选反，改拧了一次）。
                var sv = donorMesh.vertices;
                var st = donorMesh.triangles;
                var a = sv[st[0]];
                var b2 = sv[st[1]];
                var c = sv[st[2]];
                var handedness = (b2.x - a.x) * (c.y - a.y) - (b2.y - a.y) * (c.x - a.x); // >0=逆时针
                var tris = handedness < 0f ? new[] { 0, 1, 2, 2, 3, 0 } : new[] { 0, 3, 2, 2, 1, 0 };
                foreach (var mesh in m_PlateMeshes)
                    mesh.triangles = tris;
                Mod.Log.Info($"[BubbleW] 绕向实读：供体首三角 {st[0]},{st[1]},{st[2]}/{st[3]},{st[4]},{st[5]} 手性={handedness:F1} → 底板接线 {(handedness < 0f ? "0,1,2/2,3,0" : "0,3,2/2,1,0")}");

                m_DonorMesh = donorMesh;
                m_DonorMat = donor; // 诊断臂用原样供体（只读引用，不 clone，析构归缓存管）
                Mod.Log.Info($"[BubbleW] 供体 _MainTex 槽类型={donor.GetTexture("_MainTex")?.GetType().Name ?? "null"}（v2.7 数组假设已死，回退 2D）");
                m_PlateMats = new Material[k_PlateAspects.Length];
                for (int i = 0; i < k_PlateAspects.Length; i++)
                {
                    var mat = new Material(m_BaseTextMaterial);
                    mat.CopyPropertiesFromMaterial(donor);
                    mat.SetTexture("_MainTex", m_PlateTexs[i]);
                    mat.SetFloat("_TextureWidth", k_PlateTexW);
                    mat.SetFloat("_TextureHeight", k_PlateTexH);
                    mat.SetFloat("_GradientScale", k_PlatePadPx + 1f); // TMP 惯例=图集 padding+1
                    mat.SetColor("_FaceColor", new Color(0.04f, 0.05f, 0.09f, 0.62f)); // 深底托字
                    mat.SetFloat("_OutlineWidth", 0.09f);
                    mat.SetColor("_OutlineColor", new Color(1f, 1f, 1f, 0.8f));        // 浅描边=气泡框
                    mat.SetFloat("_UnderlayDilate", 0.1f);
                    mat.SetFloat("_UnderlaySoftness", 0.6f);
                    mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.3f));
                    mat.renderQueue = 3700; // 文字 3800 之下——两个透明层的确定序
                    m_PlateMats[i] = mat;
                }
                Mod.Log.Info($"[BubbleW] 底板材质已构建（供体全家桶 ×3 档 + SDF 贴图，uv2 照抄字形 {g}）");
                // 诊断 B 臂材质：供体克隆仅换贴图（与我方覆写全隔离——v2.9 二阶对剖用）
                m_BisectMats = new Material[k_PlateAspects.Length];
                for (int i = 0; i < k_PlateAspects.Length; i++)
                {
                    m_BisectMats[i] = new Material(donor);
                    m_BisectMats[i].SetTexture("_MainTex", m_PlateTexs[i]);
                }
                LogShaderProperties(donor);
            }
            catch (Exception ex)
            {
                m_PlateMats = null!;
                if (!m_PlateMaterialWarned)
                {
                    m_PlateMaterialWarned = true;
                    Mod.Log.Warn($"[BubbleW] 底板材质构建异常：{ex.Message}");
                }
            }
        }

        /// <summary>判决日志：文字材质 shader 的全属性清单（名字:类型=值）+ 启用关键字。
        /// 底板若仍是鬼，下一版照这张单抓药——不再猜。</summary>
        private void LogShaderProperties(Material sample)
        {
            try
            {
                var sh = sample.shader;
                var sb = new System.Text.StringBuilder(512);
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    var name = sh.GetPropertyName(i);
                    var type = sh.GetPropertyType(i);
                    sb.Append(name).Append(':').Append(type);
                    if (type == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    {
                        var t = sample.GetTexture(name);
                        sb.Append('=').Append(t == null ? "null" : $"{t.name}({t.width}x{t.height})");
                    }
                    else if (type == UnityEngine.Rendering.ShaderPropertyType.Float
                        || type == UnityEngine.Rendering.ShaderPropertyType.Range)
                        sb.Append('=').Append(sample.GetFloat(name).ToString("F2"));
                    sb.Append(' ');
                }
                sb.Append("| keywords: ").Append(string.Join(",", sample.shaderKeywords));
                Mod.Log.Info($"[BubbleW] 文字材质清单（{sh.name}）：{sb}");
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"[BubbleW] 材质清单日志异常：{ex.Message}");
            }
        }

        private void DestroyPlateAssets()
        {
            if (m_PlateTexs != null)
                foreach (var t in m_PlateTexs)
                    if (t != null) UnityEngine.Object.Destroy(t);
            if (m_PlateMeshes != null)
                foreach (var m in m_PlateMeshes)
                    if (m != null) UnityEngine.Object.Destroy(m);
            if (m_PlateMats != null)
                foreach (var m in m_PlateMats)
                    if (m != null) UnityEngine.Object.Destroy(m);
            if (m_BisectMats != null)
                foreach (var m in m_BisectMats)
                    if (m != null) UnityEngine.Object.Destroy(m);
            // 供体网格/材质是缓存资产的引用，不归这里销毁
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

            // 数组非空+末元素非空：BuildPlateAssets 中途抛异常会留下半空数组（元素 null 会让 DrawMesh 每帧抛）
            var drawPlate = m_PlateMode != 0 && m_PlateMats != null && m_PlateMeshes != null
                && m_PlateMeshes[k_PlateAspects.Length - 1] != null && m_PlateMats[k_PlateAspects.Length - 1] != null
                && (m_PlateMode == 1 || m_DonorMat != null);
            foreach (var cam in cameras)
            {
                if (cam.cameraType != CameraType.Game)
                    continue;

                var camPos = (float3)cam.transform.position;
                var rot = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
                var tanHalfFov = math.tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var platePushBack = (float3)(cam.transform.forward * 0.06f); // 底板压到文字后面防共面

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

                    if (drawPlate)
                    {
                        // 底板高 = 块高 + 上下各 0.35 行留白；选档宁宽勿窄
                        var plateH = worldH * (1f + 0.7f / entry.Lines);
                        var need = entry.Aspect * worldH / plateH * 1.08f;
                        var bucket = k_PlateAspects.Length - 1;
                        for (int i = 0; i < k_PlateAspects.Length; i++)
                            if (k_PlateAspects[i] >= need) { bucket = i; break; }
                        var plateMatrix = Matrix4x4.TRS((Vector3)(p + platePushBack), rot, new Vector3(plateH, plateH, plateH));
                        if (m_PlateMode == 1)
                        {
                            Graphics.DrawMesh(m_PlateMeshes[bucket], plateMatrix,
                                m_PlateMats[bucket], 0, cam, 0, null, ShadowCastingMode.Off, false);
                        }
                        else
                        {
                            // 诊断 2×2 对剖（v2.9 二阶）：A=我方 quad+供体原样材质（验网格/矩阵/通路）；
                            // B=供体字形网格+【供体克隆仅换 SDF 贴图】，抬高 2 块高防叠——
                            // B 显=凶手是我方参数覆写；B 不显=凶手是 SDF 贴图本身。
                            Graphics.DrawMesh(m_PlateMeshes[bucket], plateMatrix,
                                m_DonorMat, 0, cam, 0, null, ShadowCastingMode.Off, false);
                            Graphics.DrawMesh(m_DonorMesh,
                                Matrix4x4.TRS((Vector3)(p + platePushBack + new float3(0, worldH * 2f, 0)), rot, new Vector3(s, s, s))
                                    * Matrix4x4.Translate(-entry.Center),
                                m_BisectMats[bucket], 0, cam, 0, null, ShadowCastingMode.Off, false);
                        }
                        if (!m_LoggedFirstPlate)
                        {
                            m_LoggedFirstPlate = true;
                            Mod.Log.Info($"[BubbleW] 底板首画（模式={m_PlateMode} bucket={bucket} plateH={plateH:F2}m 文宽比={entry.Aspect:F1} 行={entry.Lines}）");
                        }
                    }

                    foreach (var (mesh, mat) in entry.Parts)
                        Graphics.DrawMesh(mesh, matrix, mat, 0, cam, 0, null, ShadowCastingMode.Off, false);
                    entry.LastUsed = UnityEngine.Time.time;
                }

                if (!m_LoggedFirstRender)
                {
                    m_LoggedFirstRender = true;
                    Mod.Log.Info($"[BubbleW] 首帧自绘（cam={cam.name}，在场 {m_Bubbles.Count}）");
                }
            }
        }
    }
}
