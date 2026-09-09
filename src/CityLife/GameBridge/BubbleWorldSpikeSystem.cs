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
    /// M3 气泡层 v5.0（自烘焙 TMP 文字网格 + 通知图标管线底板，SRP 回调自绘；执行层折行+按字计时）。
    ///
    /// 文字管线定案（勿复探）：
    /// - v1（Buffer.DrawText）判死：共享 TMP fontSize=200 懒烘焙+scale=1 硬编码=巨字；overlay 通道
    ///   文字不计数不开门="悬停才显示/俯仰角玄学"（2026-08-21 截图+反编译双实锤）。
    /// - v2 正路：烘焙借 GetTextMesh()（**快照八项状态+finally 恢复**，污染全城标签的坑勿拆），
    ///   材质 clone OverlayConfigurationPrefab.m_TextMaterial + CopyFontAtlasParameters，
    ///   绘制 beginContextRendering + Graphics.DrawMesh。v2.1 实机验证：跟随/视角全解。
    /// - 跟随读 InterpolatedTransform（模拟 Transform 是 tick 级，读它必卡）；显隐尺=分锚点距离
    ///   （MaxDistFor，勿再加落点距离闸——浅俯仰角误杀全屏）；hideOverlay 只记日志不门控。
    ///
    /// 底板长征全记录（勿复探）：
    /// - v2.2 通知图标管线：能画、深底白边观感对，但尺寸/锚点黑盒（plateH=9.26m 巨框错位），当时判死。
    /// - v2.3-v3.6 TMP 文字同路 SDF 底板：八版排除法走完（uv2/绕向/全家桶/贴图维度/贴图内容/网格/
    ///   缩放域失配修复/试色四套）——v3.5 起显形成功，但 WaterSourceName 是自发光标签 shader：
    ///   _EmissionColor=白是亮度地板（供体实采值 (1,1,1,1)），深色底板永远出不来，试色只剩
    ///   "透明/白"两态（2026-09-08 实机）。TMP 路线**观感**判死。
    /// - v4.0 定案：回归通知图标管线（观感已被 v2.2 实机证明），运行时标定键实机选定：
    ///   Ctrl+5 尺寸倍率、Ctrl+6 Y 下沉系数。TLE 先例契约：mesh ±1 quad + m_Params.x=2
    ///   （Cities2-TrafficLightsEnhancement/Systems/Overlay/RenderSystem.cs，AddIcon 原文）。
    /// - v4.1 黑盒破解（勿复探）："尺寸/锚点黑盒"实为 shader 内置距离补偿——渲染半径 =
    ///   m_Params.x·0.063·dist^0.6（IconCluster.CalculateRadius 反编译实锤），锚点在图标
    ///   下缘（GetBounds = m_Center + cameraUp·radius）。此前"越远越大/近处框包不住字/标定
    ///   只在单一距离成立"全因未除该因子；v4.1 喂参归一化后，标定跨距离一次定案。
    /// - v4.2 视角露馅修复：抬高轴是相机 up 而非世界 Y（v4.1 注释已写 cameraUp 但代码补错了轴）
    ///   ——平视碰巧正确，俯视底板横向漂移与文字分家（2026-09-08 实机截图实锤）。
    /// - v4.3 双层/裸奔修复：indirect args 与实例缓冲是 GPU 执行期才读，三档共享一份 args 缓冲
    ///   +一份材质轮换 SetData/SetBuffer，CPU 跑在 GPU 前面时后写的档覆盖先画的档——同一泡被两档
    ///   各画一遍（宽+窄叠两层）或整档被别档 args 截断（裸奔）。改每档独立 args+材质+实例缓冲
    ///   （2026-09-08 实机截图实锤）。
    ///
    /// 长文适配（沿用）：执行层折行（CJK 1 格/其余半格、13 格/行、≤4 行、溢出收"…"——排版是确定性
    ///   的活，不归 LLM）；内容不限字数（话痨/沉默是人格，全文归信息流）；屏占按行恒定；驻留按字数。
    ///
    /// 键位：Ctrl+9 会话内快速开关（与设置页总开关 AND 语义，设置页为权威总闸）。
    ///   （Ctrl+5/6 底板标定键已于 2026-09-08 实机定案后砍除，终值落常量 k_PlateSizeRatio/k_PlateYDrop；
    ///     Ctrl+6 复用为 LOD 打点后，2026-09-09 距离标定收官一并砍除；
    ///     Ctrl+7/8 底板开关/数量档于设置页上线后砍除（2026-09-09，§12 #45）——改由设置页承载）
    /// 密度/重叠治理（v4.4）：屏幕矩形互斥（底板 footprint 投影、视深为尺）+ 近者优先 +
    ///   同屏可见上限=采样池/6。
    /// 分锚点显隐距离（v4.5，2026-09-09 实机标定）：人 125m / 车 320m / 楼 800m 基础档，
    ///   采样/绘制同用 MaxDistFor 一把尺；玩家倍率滑杆已接设置页（§12 #44/#45，Mod.Options 直读生效）。
    /// 设置页接入（2026-09-09，§12 #45）：总开关 BubbleEnabled（与 Ctrl+9 AND）、三类距离倍率、
    ///   密度档 BubbleDensity（Low/Medium/High→30/60/120，LevelCount 优先读它）、底板开关 BubblePlate
    ///   （OnUpdate 每帧同步，改动即时生效）——全部经 Mod.Options 直读，null（主菜单期）回落默认。
    /// 自动隐藏双闸（v4.6，2026-09-09，§12 #46）：建造工具激活（ToolSystem.activeTool != DefaultToolSystem
    ///   实例；推土机 BulldozeToolSystem 也是独立工具，含在判定内一并隐藏）与拍照模式
    ///   （PhotoModeRenderSystem.Enabled——该系 OnCreate 即置 false，唯一置真路径是
    ///   PhotoModeUISystem.Activate(true)→Enable(true)）时整个气泡层不画（文字+底板全停，采样照跑）。
    ///   各配设置页开关 BubbleAutoHideBuildTool/BubbleAutoHidePhotoMode（默认开、即时生效），
    ///   判定在 OnUpdate 算好存字段、OnRender 只读字段；hideOverlay 禁用（v4.2 实机正常游玩也 True）。
    ///   跳变打一行 INFO（防抖，只在变化时打）。
    /// 文案源（v5.0，S5 接闲聊炉片段池，§12 #48）：换文案时优先从 BubbleChatterSystem.Snippets 取
    ///   （懒解析判空——主菜单世界系统可能不存在），锚点→场合 人=Walk/车=Vehicle/楼=Indoor
    ///   （PickFor 自带 Any 兜底语义，场合是优先级不是命门），salt=锚点.Index+换文案次数
    ///   （沿用旧轮换语义：同泡连换两条不重样），同帧片段去重（m_UsedThisFrame，帧尾清）；
    ///   占位文案池自 v5.0 降级为池空兜底（开局炉未出时气泡仍有话）。
    /// 扩展口（正式版待办）：共位小剧场（多泡剧本，§12 #48）、按 BornCycle 新鲜度衰减取泡。
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        private const int k_MaxBubbles = 120;
        // 分锚点显隐距离基础档（2026-09-09 实机标定终值，§12 #44；采样/绘制同用这一组尺）——
        // 纯距离门控不做俯仰角门控；玩家倍率滑杆在设置页（§12 #45），MaxDistFor 里乘上。
        // 人档即 §12 #41 的"人清晰可见"临界
        private const float k_MaxDistHuman = 125f;
        private const float k_MaxDistCar = 320f;
        private const float k_MaxDistBuilding = 800f;

        /// <summary>分锚点显隐距离：基础档 × 设置页倍率（Mod.Options?.BubbleDistXxx ?? 1f，§12 #45）。</summary>
        private static float MaxDistFor(byte kind)
        {
            var opts = Mod.Options;
            return kind == 0 ? k_MaxDistHuman * (opts?.BubbleDistHuman ?? 1f)
                 : kind == 1 ? k_MaxDistCar * (opts?.BubbleDistCar ?? 1f)
                 : k_MaxDistBuilding * (opts?.BubbleDistBuilding ?? 1f);
        }
        private const float k_TargetPixels = 26f;    // 文字目标屏占高（像素/行——多行按行数叠）
        private const float k_MinWorldH = 0.35f;     // 单行世界高下限（街景不至于糊脸上）
        private const float k_MaxWorldH = 5f;        // 单行上限（远看不成区名牌）
        private const int k_CacheCap = 48;           // 文字网格缓存上限（LRU 逐出，防漏）

        // 底板三档宽高比（S/M/L）；按文案宽高比就近取档，宁宽勿窄（宽了居中好看，窄了包不住字）
        private static readonly float[] k_PlateAspects = { 2.2f, 4f, 7.5f };
        private const int k_PlateTexW = 512;
        private const int k_PlateTexH = 128;

        // 图标 shader 内置距离补偿（反编译 IconCluster.CalculateRadius 实锤）：
        // 渲染半径 = m_Params.x × k_IconScaleK × dist^k_IconDistExp，且锚点在图标下缘
        // （图钉式抬高一个半径：GetBounds = m_Center + cameraUp·radius）。v2.2-v4.0 底板
        // "越远越大/近处框包不住字/标定只在单一距离成立"全因没除这个因子——喂参前必须归一。
        private const float k_IconScaleK = 0.063f;
        private const float k_IconDistExp = 0.6f;

        // 底板标定终值（2026-09-08 实机定案；v4.1 归一化后跨距离/视角稳定，v4.3 后无共享态干扰）：
        // 底板高 = 文字块高 × 1.7；Y 补偿 = 底板高 × 1.0——shader 抬高量恰为一个 plateH，
        // 与 CalculateRadius 的半径语义（渲染全高 = 2×半径）互洽
        private const float k_PlateSizeRatio = 1.7f;
        private const float k_PlateYDrop = 1.0f;

        // 密度/重叠治理（§4：同屏上限+防叠）：屏幕矩形互斥（带留白）、近者优先、
        // 同屏可见上限 = 采样池 ÷ k_VisibleDiv（30/60/120 → 5/10/20）
        private const float k_DePad = 1.15f;
        private const int k_VisibleDiv = 6;

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

        // 自动隐藏双闸（§12 #46）：建造工具激活 / 拍照模式。系统句柄懒解析（主菜单世界可能没有），
        // 状态在 OnUpdate 算好存字段，OnRender 只读字段；跳变日志走 SetGate 防抖
        private Game.Tools.ToolSystem m_ToolSystem = default!;
        private Game.Tools.DefaultToolSystem m_DefaultToolSystem = default!;
        private PhotoModeRenderSystem m_PhotoModeRender = default!;
        private bool m_HiddenByBuildTool;
        private bool m_HiddenByPhotoMode;

        // 闲聊炉片段池（v5.0，S5，§12 #48）：气泡文案主源。系统句柄懒解析判空（主菜单世界可能
        // 不存在）；取泡只发生在换文案时刻（低频），OnRender 渲染热路径零查询
        private BubbleChatterSystem m_Chatter = default!;
        private bool m_LoggedSnippetPool;  // "片段池接通" INFO 只打一次
        private bool m_LoggedPoolEmpty;    // "池空回退" INFO 只打一次（防每帧刷）
        private readonly HashSet<string> m_UsedThisFrame = new(); // 本帧已用片段（同帧去重，帧尾清）

        // 底板管线（通知图标管线：贴图/网格/缓冲是程序化内容 OnCreate 即建；材质懒取游戏图标材质）
        private bool m_PlateOn = true;
        private Material[] m_PlateMaterials = null!;    // 每档一份 clone：共享一份材质轮换绑缓冲，
        private bool m_PlateMaterialWarned;             // GPU 延迟读会让三档画出同一份实例数据
        private Texture2DArray m_PlateTex = null!;
        private Mesh[] m_PlateMeshes = null!;
        private ComputeBuffer[] m_PlateInstBuf = null!;
        private ComputeBuffer[] m_PlateArgs = null!;    // 每档一份：indirect args 是 GPU 执行期才读，
                                                        // 单缓冲跨档 SetData 会被后写的档覆盖（v4.3 实锤）
        private readonly uint[] m_ArgsArray = new uint[5];
        private readonly List<NotificationIconBufferSystem.InstanceData>[] m_PlateData
            = { new(), new(), new() };
        private int m_InstanceBufferID;
        private bool m_LoggedFirstPlate;

        // 防叠候选/中选列表（每帧清空复用，零分配）
        private readonly List<DrawCandidate> m_Candidates = new();
        private readonly List<DrawCandidate> m_Kept = new();
        private int m_KeptCount;

        /// <summary>一帧内一个待画气泡的全部绘制参数（文字矩阵+底板参数+屏幕包围盒）。</summary>
        private struct DrawCandidate
        {
            public BakedText Entry;
            public Matrix4x4 TextMatrix;
            public float3 Pos;
            public float Dist;
            public float PlateH;
            public int Bucket;
            public Rect ScreenRect;
        }

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

        // 占位文案池（v5.0 起身份降级：从文案主源降为闲聊炉片段池的池空兜底——炉未出货/系统未就绪时
        // 气泡仍有话；按类型分池：公园/住宅已分，载具类型全分待正式版）
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
            // 设置页初值（§12 #45）：总开关/底板开关联 Mod.Options（null=主菜单期回落默认）。
            // m_Active 是 Ctrl+9 会话开关，权威总闸在 MasterOn（每次绘制/采样都现读设置页）
            m_Active = Mod.Options?.BubbleEnabled ?? false;
            m_PlateOn = Mod.Options?.BubblePlate ?? true;
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
            // Ctrl+9 = 会话内快速开关（与设置页总开关 AND；Ctrl+7/8 已砍除，2026-09-09 §12 #45）
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var debounced = m_Frame - m_LastKeyFrame > 30;
            if (debounced && ctrl && Input.GetKeyDown(KeyCode.Alpha9)) { m_LastKeyFrame = m_Frame; Toggle(); }

            // 底板开关每帧同步设置页（改动即时生效；null=主菜单期保留现状）
            m_PlateOn = Mod.Options?.BubblePlate ?? m_PlateOn;

            // 自动隐藏双闸每帧重算（§12 #46；信号实锤见 UpdateAutoHideGates 注释）
            UpdateAutoHideGates();

            // FPS 计：每 4 秒一行（开着才有意义）
            m_FpsAccum += UnityEngine.Time.deltaTime;
            m_FpsFrames++;
            m_FpsTimer += UnityEngine.Time.deltaTime;
            if (m_FpsTimer >= 4f)
            {
                if (m_Active && MasterOn)
                    Mod.Log.Info($"[BubbleW] FPS avg={(m_FpsFrames / m_FpsAccum):F1}（N={LevelCount()}，在场 {m_Bubbles.Count}，缓存 {m_Cache.Count}，可见 {m_KeptCount}）");
                m_FpsAccum = 0;
                m_FpsFrames = 0;
                m_FpsTimer = 0f;
            }

            if (!m_Active || !MasterOn)
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
            if (m_PlateOn && (m_PlateMaterials == null || m_PlateMaterials[0] == null))
                TryInitPlateMaterial();

            // 本帧已用片段集合帧尾清（S5 同帧去重；取泡只发生在上方 Resample/TickLifecycle，
            // 早退分支在它们之前 return，集合恒为空，无需清）
            m_UsedThisFrame.Clear();
            m_Frame++;
        }

        /// <summary>权威总闸：设置页 BubbleEnabled（§12 #45）。null（主菜单期）视为开——闸不住菜单期探针。</summary>
        private static bool MasterOn => Mod.Options == null || Mod.Options.BubbleEnabled;

        /// <summary>自动隐藏双闸判定（§12 #46，2026-09-09 反编译 Game.dll 实锤，勿复探）：
        /// 建造工具 = <c>ToolSystem.activeTool != DefaultToolSystem 实例</c>——ToolSystem.OnCreate 里
        /// activeTool 初始即 m_DefaultToolSystem，选任何工具（含推土机 BulldozeToolSystem，§12 #46 口径：
        /// 推土也算工具激活，一并隐藏）都会换掉它；游戏本体 PhotoModeUISystem.Activate 里
        /// "activeTool != m_BulldozeTool 则收回 DefaultToolSystem"的写法同口径。ToolSystem 主菜单世界
        /// 可能不存在，GetExistingSystemManaged 判空。
        /// 拍照模式 = <c>PhotoModeRenderSystem.Enabled</c>——该系 OnCreate 即 base.Enabled=false，
        /// 全程序唯一置真路径是 PhotoModeUISystem.Activate(true) → Enable(true)
        /// （Activate 仅由 GamePanelUISystem 拍照面板开合调用；编辑器那条在编辑器世界，与本世界无关），
        /// 游戏自己也这么读（RichPresenceUpdateSystem 判 Enabled）。退出时 Enable(false) 后
        /// 由 OnUpdate 在镜头混合结束才收回 Enabled=false——退出过渡期仍算拍照中，气泡多藏一拍正合适。
        /// RenderingSystem.hideOverlay 禁用：v4.2 实机正常游玩也会 True（见 OnRender 顶部诊断日志）。
        /// 两闸各受设置页开关控制（关=不隐藏，即时生效）；null（主菜单期）回落默认开。</summary>
        private void UpdateAutoHideGates()
        {
            if (m_ToolSystem == null)
                m_ToolSystem = World.GetExistingSystemManaged<Game.Tools.ToolSystem>();
            if (m_DefaultToolSystem == null)
                m_DefaultToolSystem = World.GetExistingSystemManaged<Game.Tools.DefaultToolSystem>();
            if (m_PhotoModeRender == null)
                m_PhotoModeRender = World.GetExistingSystemManaged<PhotoModeRenderSystem>();

            var opts = Mod.Options;
            var buildActive = m_ToolSystem != null && m_DefaultToolSystem != null
                && m_ToolSystem.activeTool != null && m_ToolSystem.activeTool != m_DefaultToolSystem;
            var photoActive = m_PhotoModeRender != null && m_PhotoModeRender.Enabled;
            SetGate(ref m_HiddenByBuildTool, buildActive && (opts?.BubbleAutoHideBuildTool ?? true), "建造工具激活");
            SetGate(ref m_HiddenByPhotoMode, photoActive && (opts?.BubbleAutoHidePhotoMode ?? true), "拍照模式");
        }

        /// <summary>单闸状态更新 + 跳变日志（防抖：只在状态变化时打一行 INFO）。</summary>
        private static void SetGate(ref bool gate, bool on, string reason)
        {
            if (gate == on)
                return;
            gate = on;
            Mod.Log.Info($"[BubbleW] 自动隐藏：{reason} → {(on ? "隐藏气泡层" : "恢复显示")}");
        }

        /// <summary>采样池大小：优先读设置页密度档（Low/Medium/High→30/60/120）；null（主菜单期）回落默认档 120。</summary>
        private static int LevelCount()
        {
            var opts = Mod.Options;
            if (opts == null)
                return 120;
            return opts.BubbleDensity == CityLifeSetting.BubbleDensityLevel.Low ? 30
                 : opts.BubbleDensity == CityLifeSetting.BubbleDensityLevel.Medium ? 60 : 120;
        }

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
                    || !OnScreen(cam, b.Anchor, b.Kind))
                    m_Bubbles.RemoveAt(i);
            }

            var want = LevelCount();
            Collect(cam, m_CarQuery, Math.Max(4, want / 6), 1);
            Collect(cam, m_BuildingQuery, Math.Max(3, want / 10), 2);
            Collect(cam, m_HumanQuery, want, 0);
        }

        private bool OnScreen(Camera cam, Entity e, byte kind)
        {
            var p = EntityManager.GetComponentData<Transform>(e).m_Position;
            var s = cam.WorldToScreenPoint(p);
            return s.z > 5f && s.z <= MaxDistFor(kind)
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
                if (s.z < 5f || s.z > MaxDistFor(kind))
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

        /// <summary>换文案唯一入口（采样建组/生命周期轮换都走这）：优先闲聊炉片段池（v5.0 主源），
        /// 取不到回退占位文案池（池空兜底——开局炉未出时气泡仍有话）。</summary>
        private void SetBubbleText(ref TrackedBubble b, int textIdx)
        {
            if (!TryPickSnippet(b, textIdx, out var text))
            {
                // 池空兜底（占位文案池）：锚点+次数取模轮换（旧语义，确定性错开；公园锚点有专池）
                var pool = b.Kind == 0 ? k_Texts
                    : b.Kind == 1 ? k_CarTexts
                    : EntityManager.HasComponent<Game.Buildings.AttractivenessProvider>(b.Anchor) ? k_ParkTexts
                    : k_BuildingTexts;
                text = pool[(b.Anchor.Index + textIdx) % pool.Length];
            }
            b.Text = text;
            if (m_BaseTextMaterial != null)
                EnsureBaked(b.Text, b.Kind);
        }

        /// <summary>锚点类型→气泡场合（§12 #48）：人锚点采的是路上行人=Walk，车=Vehicle，楼=Indoor。
        /// 拿不准不靠猜——PickFor 候选自带 Any 兜底（exact ∪ Any），场合是优先级不是命门。</summary>
        private static Content.BubbleOccasion OccasionFor(byte kind)
            => kind == 0 ? Content.BubbleOccasion.Walk
             : kind == 1 ? Content.BubbleOccasion.Vehicle
             : Content.BubbleOccasion.Indoor;

        /// <summary>从闲聊炉片段池取一条（v5.0 文案主源，§12 #48）。系统句柄懒解析判空
        /// （主菜单世界可能不存在，判空前绝不碰池）；salt=锚点.Index+换文案次数（沿用旧轮换语义——
        /// 同泡连换两条 salt 递增，池深>1 时必不重样）；同帧去重：本帧已用片段按 salt+k 顺探下一条
        /// （确定性），连探 8 次全占用才接受重复（池够深时一次即中，顺探是极端保险）。
        /// 池空/系统未就绪/该场合无候选返回 false（调用方回退占位池）。</summary>
        private bool TryPickSnippet(TrackedBubble b, int textIdx, out string text)
        {
            text = "";
            if (m_Chatter == null)
                m_Chatter = World.GetExistingSystemManaged<BubbleChatterSystem>();
            if (m_Chatter == null || m_Chatter.Snippets.Count == 0)
            {
                // 池空回退日志只打一次（开局炉未出是常态，别每帧刷）；主菜单期系统不存在不记
                if (m_Chatter != null && !m_LoggedPoolEmpty)
                {
                    m_LoggedPoolEmpty = true;
                    Mod.Log.Info("[BubbleW] 片段池为空（闲聊炉未出货），回退占位文案池");
                }
                return false;
            }
            var pool = m_Chatter.Snippets;
            var occasion = OccasionFor(b.Kind);
            var salt = (uint)(b.Anchor.Index + textIdx);
            for (uint k = 0; k < 8; k++)
            {
                var picked = pool.PickFor(occasion, salt + k);
                if (picked == null)
                    return false; // 该场合连 Any 候选都没有（理论到不了，纯防御）
                if (k < 7 && m_UsedThisFrame.Contains(picked.Text))
                    continue; // 本帧已被别的气泡用，顺探下一条
                m_UsedThisFrame.Add(picked.Text);
                text = picked.Text;
                if (!m_LoggedSnippetPool)
                {
                    m_LoggedSnippetPool = true;
                    Mod.Log.Info($"[BubbleW] 片段池接通，池存 {pool.Count} 条");
                }
                return true;
            }
            return false; // 理论到不了，纯防御
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
                m_PlateArgs = new ComputeBuffer[k_PlateAspects.Length];
                for (int i = 0; i < k_PlateAspects.Length; i++)
                    m_PlateArgs[i] = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
                m_PlateInstBuf = new ComputeBuffer[k_PlateAspects.Length];
                m_PlateMaterials = new Material[k_PlateAspects.Length];
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
                    // 每档一份 clone（renderQueue 不动：克隆自带 3700，恰在文字 3800 之下）——
                    // 共享一份材质轮换 SetBuffer，GPU 延迟读会让三档画出同一份缓冲（双层/裸奔根因）
                    for (int i = 0; i < k_PlateAspects.Length; i++)
                        m_PlateMaterials[i] = new Material(cfg.m_Material) { mainTexture = m_PlateTex };
                    Mod.Log.Info("[BubbleW] 底板材质×3 已克隆（IconConfigurationPrefab.m_Material + 自建 Texture2DArray）");
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
            if (m_PlateMaterials != null)
                foreach (var m in m_PlateMaterials)
                    if (m != null) UnityEngine.Object.Destroy(m);
            if (m_PlateArgs != null)
            {
                foreach (var b in m_PlateArgs)
                    b?.Release();
                m_PlateArgs = null;
            }
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

            if (!m_Active || !MasterOn || m_Bubbles.Count == 0)
                return;

            // 自动隐藏闸（§12 #46）：隐藏时整层不画（文字+底板全停），采样照跑
            if (m_HiddenByBuildTool || m_HiddenByPhotoMode)
                return;

            // 数组非空+末元素非空：BuildPlateAssets 中途抛异常会留下半空数组
            var drawPlate = m_PlateOn && m_PlateArgs != null && m_PlateArgs[k_PlateAspects.Length - 1] != null
                && m_PlateMaterials != null && m_PlateMaterials[k_PlateAspects.Length - 1] != null
                && m_PlateMeshes != null && m_PlateMeshes[k_PlateAspects.Length - 1] != null;
            foreach (var cam in cameras)
            {
                if (cam.cameraType != CameraType.Game)
                    continue;

                var camPos = (float3)cam.transform.position;
                var camUp = (float3)cam.transform.up;
                var rot = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
                var tanHalfFov = math.tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                if (drawPlate)
                    foreach (var list in m_PlateData)
                        list.Clear();

                // 第一遍：收集候选（位置/尺寸/屏幕包围盒），超距/镜头背后剔除
                m_Candidates.Clear();
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
                    if (dist > MaxDistFor(b.Kind))
                        continue;

                    // 恒定屏占按"行"：单行世界高 = 2·dist·tan(fov/2)·目标像素/屏高，夹 [0.35, 5]m；
                    // 块高 = 行高 × 行数——话痨段落按行数长高，不会缩成蚂蚁
                    var lineH = math.clamp(2f * dist * tanHalfFov * k_TargetPixels / cam.pixelHeight,
                        k_MinWorldH, k_MaxWorldH);
                    var worldH = lineH * entry.Lines;
                    var s = worldH / entry.Height;
                    var matrix = Matrix4x4.TRS((Vector3)p, rot, new Vector3(s, s, s))
                        * Matrix4x4.Translate(-entry.Center);
                    entry.LastUsed = UnityEngine.Time.time;

                    // 底板参数（选档宁宽勿窄：需求比 = 文案宽高比 ÷ 底板高倍率 × 横向余量）
                    var plateH = worldH * k_PlateSizeRatio;
                    var need = entry.Aspect * worldH / plateH * 1.08f;
                    var bucket = k_PlateAspects.Length - 1;
                    for (int i = 0; i < k_PlateAspects.Length; i++)
                        if (k_PlateAspects[i] >= need) { bucket = i; break; }

                    // 屏幕包围盒（防叠用）：按底板 footprint 投影，视深为尺（与投影同尺）；
                    // 镜头背后 WorldToScreenPoint 会镜像翻转，直接不参选
                    var sp = cam.WorldToScreenPoint((Vector3)p);
                    if (sp.z < 1f)
                        continue;
                    var pxPerM = cam.pixelHeight / (2f * sp.z * tanHalfFov);
                    var w = plateH * k_PlateAspects[bucket] * pxPerM * k_DePad;
                    var h = plateH * pxPerM * k_DePad;
                    m_Candidates.Add(new DrawCandidate
                    {
                        Entry = entry,
                        TextMatrix = matrix,
                        Pos = p,
                        Dist = dist,
                        PlateH = plateH,
                        Bucket = bucket,
                        ScreenRect = new Rect(sp.x - w * 0.5f, sp.y - h * 0.5f, w, h),
                    });
                }

                // 防叠：近者优先，屏幕矩形互斥，同屏上限——叠压/超限的泡本帧让位（不参与绘制）
                m_Candidates.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                m_Kept.Clear();
                var cap = math.max(4, LevelCount() / k_VisibleDiv);
                foreach (var c in m_Candidates)
                {
                    if (m_Kept.Count >= cap)
                        break;
                    var blocked = false;
                    foreach (var k in m_Kept)
                        if (c.ScreenRect.Overlaps(k.ScreenRect)) { blocked = true; break; }
                    if (!blocked)
                        m_Kept.Add(c);
                }

                // 第二遍：只画中选泡（文字逐段 + 底板入档缓冲）
                foreach (var c in m_Kept)
                {
                    foreach (var (mesh, mat) in c.Entry.Parts)
                        Graphics.DrawMesh(mesh, c.TextMatrix, mat, 0, cam, 0, null, ShadowCastingMode.Off, false);
                    if (drawPlate)
                    {
                        // 锚点在底板下缘、抬高轴 = 相机 up；m_Params.x 除掉 shader 自带的
                        // k_IconScaleK·dist^k_IconDistExp 补偿，渲染尺寸才等于 plateH（跨距离稳定）
                        var iconScale = k_IconScaleK * math.pow(math.max(c.Dist, 1f), k_IconDistExp);
                        m_PlateData[c.Bucket].Add(new NotificationIconBufferSystem.InstanceData
                        {
                            m_Position = c.Pos - camUp * (c.PlateH * k_PlateYDrop),
                            m_Params = new float4(c.PlateH / iconScale, 0f, 1f, 1f), // (归一化全高, 不脉动, 不透明, 开)
                            m_Icon = c.Bucket,
                            m_Distance = c.Dist,
                        });
                    }
                }
                m_KeptCount = m_Kept.Count;

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
                var mat = m_PlateMaterials[bucket];
                mat.SetBuffer(m_InstanceBufferID, buf); // 每档专属材质+缓冲，无跨档共享态

                var mesh = m_PlateMeshes[bucket];
                m_ArgsArray[0] = mesh.GetIndexCount(0);
                m_ArgsArray[1] = (uint)list.Count;
                m_ArgsArray[2] = mesh.GetIndexStart(0);
                m_ArgsArray[3] = mesh.GetBaseVertex(0);
                m_ArgsArray[4] = 0;
                m_PlateArgs[bucket].SetData(m_ArgsArray);

                // bounds 包住本档所有实例（唯一的剔除机制，半径余量按最大底板算）
                var bounds = new Bounds((Vector3)list[0].m_Position, Vector3.zero);
                foreach (var inst in list)
                    bounds.Encapsulate((Vector3)inst.m_Position);
                bounds.Expand(40f);

                Graphics.DrawMeshInstancedIndirect(mesh, 0, mat, bounds, m_PlateArgs[bucket],
                    0, null, ShadowCastingMode.Off, false, 0, cam);

                if (!m_LoggedFirstPlate)
                {
                    m_LoggedFirstPlate = true;
                    Mod.Log.Info($"[BubbleW] 底板首画（bucket={bucket} N={list.Count} plateH={list[0].m_Params.x:F2}m 定值×{k_PlateSizeRatio} Y×{k_PlateYDrop} 图标管线）");
                }
            }
        }
    }
}
