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
    /// 密度/重叠治理（v4.4）：屏幕矩形互斥（底板 footprint 投影、视深为尺）+ 同屏可见上限=采样池/6；
    ///   中选优先级 v5.2 起改滞回（§12 #49，实机"近者优先帧帧翻盘、人堆互顶读不完"的根治）：
    ///   剧场参与者必留 > 在画泡驻留期内保位（换文案时刻才重新竞争）> 近者优先填坑；
    ///   剧场泡计入同屏上限账（2026-09-16 玩家定案，注记挂 §12 #67：占用名额从普通泡预算扣，
    ///   必留档不变——外部泡顶不掉剧场，剧场叠屏也不再顶掉别人；取代 #55"剧场不占同屏上限"的名额部分）。
    ///   v5.3 起叠加可见端分类比例（§12 #51，"不是所有一起冒"）：楼/车各 ≤上限/3，人不限（人为主）；
    ///   车站不当楼说话（楼查询排除 Game.Routes.WaitingPassengers，车站声音归候车行人/小剧场）；
    ///   长文稳锚：换文案时实测锚点移速，动的限长（>3m/s ≤16 字 / >0.6 ≤32 / 静止 ≤48），楼不限；
    ///   追踪上限=采样满档 120+剧场保留位 16（池灌满不再把剧场 EnsureAnchor 顶死）。
    /// 锚点分类治理·查询侧（v5.4，2026-09-11 实机三现象定案，§12 #57）：
    ///   ① 人查询排除乘车市民（Game.Creatures.CurrentVehicle，ilspy dump Game.dll 实锤——任务口径写的
    ///     Game.Citizens.CurrentVehicle 不存在，组件在 Creatures 命名空间）："路边空车/乘车市民以人的
    ///     颜色说话"根治，乘车市民的声音归车档；
    ///   ② 楼查询黑名单转白名单：查询层排公园/景点（Exclude AttractivenessProvider——开放空间的声音
    ///     由园内行人锚点承载，"不宜和密闭楼房同类"玩家定案），Collect 层再过 IsTalkativeBuilding
    ///     （市政/地标直收 / 四 Property 有租户才说话 / 低密住宅 1/3 散列降频"人少话少"）
    ///     ——养鱼场排架/空楼/废墟闭嘴；
    ///   ③ EnsureAnchor（剧场建组口）刻意不加白名单：白名单管"什么楼能自己冒泡"，剧场有自己的选场逻辑。
    /// 分锚点显隐距离（v4.5，2026-09-09 实机标定）：人 125m / 车 320m / 楼 800m 基础档，
    ///   采样/绘制同用 MaxDistFor 一把尺；玩家倍率滑杆已接设置页（§12 #44/#45，Mod.Options 直读生效）。
    /// 设置页接入（2026-09-09，§12 #45）：总开关 BubbleEnabled（与 Ctrl+9 AND）、三类距离倍率、
    ///   同屏上限 BubbleVisibleMax（§12 #53 滑杆 2-30 默认 6；§12 #55 起采样池跟随它×4，旧密度三档废止）、
    ///   底板开关 BubblePlate（OnUpdate 每帧同步，改动即时生效）——全部经 Mod.Options 直读，null（主菜单期）回落默认。
    /// 节奏时钟选项（v5.6，§12 #68，治"3x 下街上气泡过快"）：驻留 NextAt/换文案/滞回保位统一走 PaceNow——
    ///   关（默认）=unscaledTime 墙钟（#49 原义不变），开=Time.time 游戏时钟（timeScale 缩放：暂停冻结、
    ///   倍速同速放大）；选项运行中翻转时在场泡 NextAt 全部重定基（错相小延迟，绝不同时切换纪律不破）。
    ///   高速缓解（打分侧）：倍速 >1x 时中选打分的排序键乘移速惩罚（偏好慢速/静止锚点）——3x 下"气泡
    ///   变化快"的真因大头是移动锚点 3x 速度离画→提前轮换（物理真相，只缓解不根治）；实测移速仍按
    ///   墙钟 m/s（阅读舒适度语义，与时钟源无关），速度档口径与 TryPickSnippet 限长同源。
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
    /// 小剧场插队（v5.1，S7，§12 #48 多人小剧场段）：SetBubbleText 最前先问 BubbleTheaterSystem——
    ///   锚点挂在活剧场 → 显示剧本台词而非池片段（TryGetLine 是**纯查询**，绝不推轮次）；
    ///   轮次推进只走 TickLifecycle 的 OnAnchorRotated 回调（当前说话人锚点气泡到时换下一条→下一人，
    ///   严格串行同剧组同屏 1 泡，2026-09-16 注记挂 §12 #67），
    ///   剧场侧再把新台词经 RefreshAnchorText 推到说话人锚点上（上条读完下条立即接话）。
    ///   供剧场系统的最小 internal 口：HasAnchor（验活）/SnapshotAnchors（选锚点低频快照）/
    ///   EnsureAnchor（绑名单建组，带层开关+存在性+Transform+屏内+容量五道闸）/RefreshAnchorText。
    /// 事件快反层（v5.5，§12 #58，2026-09-11"着火了还在说无关的事"实机实锤定案）：气泡自挂
    ///   OnFire/AccidentSite 只读查询（分类学口径照 EventNewsSystem，勿复探），128 帧错峰刷活动
    ///   事件表（≤8 条，溢出按离相机近优先）；换文案时锚点在事件点 60m 内 → 强制播事件反应模板
    ///   （火灾/车祸/犯罪各一小池，salt=锚点.Index+textIdx 确定性轮换，驻留节奏不变）。
    ///   优先级定案：剧场 > 事件快反 > 片段 > 隐身。0 token——感叹句不需要 AI；
    ///   模板非一次性片段：不进池、不消耗、跨事件允许重复；事件消失后下次换文案自然回退片段池。
    /// 扩展口（正式版待办）：按 BornCycle 新鲜度衰减取泡。（共位小剧场已落地 S7；连续剧串场归 S8）
    /// </summary>
    public partial class BubbleWorldSpikeSystem : GameSystemBase
    {
        // 追踪上限 = 采样池满档 120 + 剧场保留位 16（2026-09-10 实机：池子被采样灌满后 EnsureAnchor
        // 容量闸必挂，剧场开播中止连发——采样永远填不到这里，差额就是剧场的专用坑）
        private const int k_MaxBubbles = 136;
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
        // 锚点 Y 偏移（人头/车顶/楼顶，估值——正式版按包围盒）：绘制/屏内判定/采样三处同用这一组，
        // 判定必须按"气泡实际显示位置"投影（2026-09-11 实机：按地基判屏，俯视近楼时地基在屏外屋顶在屏内
        // → 剧场绑锚误杀/开播 2 秒即终了）
        private const float k_YOffHuman = 2.6f;
        private const float k_YOffCar = 2.8f;
        private const float k_YOffBuilding = 12f;
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

        // 密度/重叠治理（§4：同屏上限+防叠）：屏幕矩形互斥（带留白）、滞回三档中选、
        // 同屏可见上限 = 设置页滑杆（§12 #53/#55；旧"采样池÷6"公式已随旋钮合并废止）
        private const float k_DePad = 1.15f;

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
        private readonly HashSet<string> m_InUseTexts = new();    // 当前全部在显文案（跨泡去重：同一句不同时在两个泡上，池浅时重复观感实锤 2026-09-11）

        // 小剧场（v5.1，S7，§12 #48）：句柄懒解析判空（同片段池纪律）；插队取词在 SetBubbleText，
        // 轮次回调在 TickLifecycle——OnRender 渲染热路径依旧零查询
        private BubbleTheaterSystem? m_Theater;

        // 事件现场反应层（v5.7，§12 #70）：句柄懒解析判空（同上）；角色圈反应行在 SetBubbleText 插队，
        // 优先级高于 #58 事件快反模板
        private EventSceneSystem? m_SceneReactions;

        // —— 事件快反层（v5.5，§12 #58）：活动事件表+反应模板。查询 OnCreate 缓存、128 帧错峰刷新、
        //    距离判定只发生在换文案时刻（低频路径），OnRender 热路径零查询 ——
        private const float k_EventRadius = 60f;   // 事件影响半径（锚点在此范围内换文案即被覆盖）
        private const int k_MaxActiveEvents = 8;   // 活动事件表上限（溢出口径见 RefreshActiveEvents）
        private EntityQuery m_FireQuery = default!;
        private EntityQuery m_AccidentQuery = default!;
        private readonly List<ActiveEvent> m_ActiveEvents = new(k_MaxActiveEvents);
        private readonly HashSet<Entity> m_LoggedEvents = new();   // 已打"新事件"日志的源实体（防抖：同实体不重复打）
        private readonly List<Entity> m_LoggedPruneScratch = new();

        /// <summary>活动事件类型（反应模板分池键）。</summary>
        private enum BubbleEventKind : byte { Fire, Traffic, Crime }

        /// <summary>活动事件表条目：类型 + 世界坐标 + 源实体（日志防抖用）。</summary>
        private struct ActiveEvent
        {
            public BubbleEventKind Kind;
            public float3 Pos;
            public Entity Source;
        }

        // 反应模板小池（§12 #58）：路人惊呼口吻、感叹句、≤12 字为主。非一次性语义——
        // 不进片段池不消耗，跨事件允许重复（"着火了"在哪个火灾现场喊都成立）；
        // salt=锚点.Index+textIdx 确定性轮换（沿用片段池同泡不重样语义），驻留节奏复用 HoldFor。
        // 如何扩展：直接往对应数组里加句即可，轮换/折行/烘焙全自动。
        private static readonly string[] k_FireReactions =
        {
            "着火了！快报警！",
            "好大的火！快躲开！",
            "那边烧起来了！",
            "听！消防车来了！",
            "烟好大！别过去！",
        };
        private static readonly string[] k_TrafficReactions =
        {
            "出车祸了！",
            "撞车了！吓我一跳！",
            "撞得好惨！慢点开！",
            "前面全堵死了！",
        };
        private static readonly string[] k_CrimeReactions =
        {
            "抓小偷啊！",
            "警察都来了！",
            "光天化日的！",
            "这治安怎么了！",
        };

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
        // 滞回状态（§12 #49）：上帧中选锚点集 + 本帧中选锚点集（兼做防重入查重）
        private readonly HashSet<Entity> m_PrevKept = new();
        private readonly HashSet<Entity> m_KeptSet = new();

        /// <summary>节奏时钟（§12 #68 设置页"节奏随游戏倍速"）：关（默认）=unscaledTime 墙钟秒
        /// （§12 #49 原义——倍速只加速模拟，不加速人阅读）；开=Time.time 游戏时钟（timeScale 缩放：
        /// 暂停冻结、倍速同速放大）。驻留 NextAt/换文案/滞回保位全走这一个源——两端必须同时钟比较。</summary>
        private static double PaceNow => Content.ModSettings.BubblePaceFollowsGameSpeed
            ? UnityEngine.Time.time
            : UnityEngine.Time.unscaledTime;

        // 节奏时钟源跟踪（切换检测用）：选项运行中翻转时，旧时钟下的 NextAt 在新时钟下无意义，
        // 全部重定基到"现在+错相小延迟"（见 OnUpdate；错相沿用 Index 散列，绝不同时切换纪律不破）
        private bool m_PaceOnGameClock;

        // 游戏倍速读数（§12 #68 高速缓解）：timeScale——§12 #49 实锤倍速走 timeScale 缩放（暂停=0）。
        // OnUpdate 每帧刷新存字段，OnRender 只读（与自动隐藏双闸同款纪律，渲染热路径不直接读引擎状态）
        private float m_GameSpeed = 1f;

        /// <summary>一帧内一个待画气泡的全部绘制参数（文字矩阵+底板参数+屏幕包围盒+滞回判定用的锚点/驻留期满时刻）。</summary>
        private struct DrawCandidate
        {
            public BakedText Entry;
            public Matrix4x4 TextMatrix;
            public float3 Pos;
            public float Dist;
            public float PlateH;
            public int Bucket;
            public Rect ScreenRect;
            public Entity Anchor;   // 滞回：上帧在画判定
            public double NextAt;    // 驻留期满时刻（节奏时钟秒，时钟源随 §12 #68 选项，与 TrackedBubble 同时钟）
            public float AnchorSpeed; // 实测锚点移速 m/s（墙钟口径）——高速档打分偏好慢速锚用（§12 #68）
            public byte Kind;       // 0 人 1 车 2 楼——可见端分类比例上限用（§12 #51）
            public float Score;     // 中选排序键：视深 × 屏缘惩罚（屏心优先，2026-09-11 玩家定案）
        }

        /// <summary>一个被追踪的气泡：锚点实体 + 当前文案 + 独立生命周期 + 实测移速（长文稳锚用，§12 #51）。</summary>
        private struct TrackedBubble
        {
            public Entity Anchor;
            public byte Kind;       // 0 人 1 车 2 楼
            public string Text;     // 当前文案（Add 前必走 SetBubbleText，不会读到 null）
            public int TextIdx;
            public double NextAt;   // 下次换文案时刻（节奏时钟秒：关=unscaledTime 墙钟——倍速不缩短驻留，§12 #49；
                                    // 开=Time.time 游戏时钟——暂停冻结、倍速同速放大，§12 #68 设置页选项）
            public float3 LastPos;  // 上次换文案时锚点位（测速用）
            public float LastSetAt; // 上次换文案时刻（unscaledTime；0=未测过）
            public float Speed;     // 实测移速 m/s（换文案间隔的位移/时长点估计；初值 车=3 假设在动，人/楼=0）
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
        // 占位句池已于 §12 #53 退役（一次性语义下占位=重复制造机；池空兜底改"……"沉默泡）。
        // 首烘用沉默泡建缓存（"……"三档网格式时即烘，见 EnsureBaked 调用链）

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
                // 乘车市民不按人冒泡（§12 #57 ①：实机"路边空车/乘车市民以人的颜色说话"——声音归车档）。
                // 组件实锤：ilspy dump 本机 Game.dll 打出 struct 定义=存在；注意任务口径写的
                // Game.Citizens.CurrentVehicle 实际不存在，组件在 Game.Creatures 命名空间下
                ComponentType.Exclude<Game.Creatures.CurrentVehicle>(),
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
                // 车站不当楼说话（2026-09-10 实机："公交站说晚饭吃啥"读取混乱）——候车组件实锤挂车站实体
                // （Game.Routes.WaitingPassengers，spike §4）；车站的声音归候车行人锚点/小剧场，不归楼池
                ComponentType.Exclude<Game.Routes.WaitingPassengers>(),
                // 公园/景点归人（§12 #57 ③，玩家定案"不宜和密闭楼房同类"）：AttractivenessProvider 是
                // 公园/开放空间的吸引力组件（AttractionRamp/CityChangeSystem 同款实锤）——开放空间的声音
                // 由园内行人锚点承载。ECS 查询只能按组件有无，更细的楼池白名单在 Collect 层
                // IsTalkativeBuilding（§12 #57 ②）
                ComponentType.Exclude<Game.Buildings.AttractivenessProvider>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadOnly<OverlayConfigurationData>());
            m_IconConfigQuery = GetEntityQuery(ComponentType.ReadOnly<IconConfigurationData>());
            // 事件快反层（§12 #58）：只读查询 OnCreate 缓存、排除 Temp/Deleted，口径照 EventNewsSystem
            // （分类学实锤勿复探）——火灾=OnFire 挂燃烧中的建筑本体（自带 Transform 定位）；
            // 车祸/犯罪=AccidentSite.m_Flags 位标志（位置在刷新时按本体→m_Event 逐级取，查询不强求 Transform）
            m_FireQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Events.OnFire>(),
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_AccidentQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Events.AccidentSite>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
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

            // 游戏倍速每帧存字段（§12 #68 高速缓解打分用；OnRender 只读不直接查引擎）
            m_GameSpeed = UnityEngine.Time.timeScale;

            // 节奏时钟切换重定基（§12 #68 选项运行中翻转）：旧时钟的 NextAt 在新时钟下无意义——
            // 全部压到"现在+Index 错相 0-4.75s"，两个方向都平滑过渡（不错过也不整屏同切）
            var paceOnGameClock = Content.ModSettings.BubblePaceFollowsGameSpeed;
            if (paceOnGameClock != m_PaceOnGameClock)
            {
                m_PaceOnGameClock = paceOnGameClock;
                var rebaseNow = PaceNow;
                for (int i = 0; i < m_Bubbles.Count; i++)
                {
                    var rb = m_Bubbles[i];
                    rb.NextAt = rebaseNow + (rb.Anchor.Index % 20) * 0.25;
                    m_Bubbles[i] = rb;
                }
                Mod.Log.Info($"[BubbleW] 节奏时钟切换 → {(paceOnGameClock ? "游戏时钟（暂停冻结、倍速同速放大）" : "墙钟（现实时间）")}，在场 {m_Bubbles.Count} 泡已重定基");
            }

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

            // 在显文案集（跨泡去重用，取泡前重建）：同一句台词不同时在两个泡头上
            m_InUseTexts.Clear();
            foreach (var b in m_Bubbles)
                m_InUseTexts.Add(b.Text);

            // 事件快反表刷新：128 帧错峰（本系 GetUpdateInterval=1 逐帧跑，手动分频；相位 64
            // 与 Resample 的 %512==0 错开，别每帧全扫）
            if (m_Frame % 128 == 64)
                RefreshActiveEvents(cam);

            // 重采样：周期兜底 + 视角大幅移动即触发（节流 30 帧——快移视角气泡跟不上的根治）
            var camMoved = (cam.transform.position - m_LastSamplePos).sqrMagnitude > 40f * 40f
                || Vector3.Angle(m_LastSampleFwd, cam.transform.forward) > 12f;
            if (m_Frame % 512 == 0 || (camMoved && m_Frame - m_LastSampleFrame > 30))
                Resample(cam);
            TickLifecycle();

            // 基底材质懒取 + 兜底补烘（取材质失败时 SetBubbleText 的烘焙会被跳过，这里兜住）；
            // "……"不烘（沉默拍隐身不画，§12 #54 玩家定案：没内容就不该有泡）
            if (m_BaseTextMaterial == null)
                TryInitMaterial();
            if (m_BaseTextMaterial != null)
                foreach (var b in m_Bubbles)
                    if (b.Text != "……" && !m_Cache.ContainsKey((b.Text, b.Kind)))
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

        /// <summary>采样池大小：跟随同屏上限滑杆（§12 #55 单旋钮——密度三档与上限管同一观感，已合并废止）；
        /// 池 = 上限×4 夹 [8,120]（轮换/剧场/候选的头部余量；上限拉满 30 时=120 与原高档一致）。
        /// null（主菜单期）回落 24（默认上限 6×4）。</summary>
        private static int LevelCount()
        {
            var opts = Mod.Options;
            var cap = opts?.BubbleVisibleMax ?? 6;
            return math.clamp(cap * 4, 8, 120);
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

        // 屏内判定带 15% 溢出余量（2026-09-11 实机：绑锚/终了被"刚好出屏一点点"误杀——开播中止六连
        // 全是"离屏"但场景明明在眼前；绘制热路径另有镜头背后剔除（sp.z<1f），余量只影响绑定/保留判定）
        private const float k_ScreenMargin = 0.15f;

        // （冷却制常量已随 §12 #53 真一次性退役——取用即消耗，无冷却可言）

        private bool OnScreen(Camera cam, Entity e, byte kind)
        {
            var p = EntityManager.GetComponentData<Transform>(e).m_Position;
            p.y += kind == 0 ? k_YOffHuman : kind == 1 ? k_YOffCar : k_YOffBuilding; // 按气泡显示位置判（人头/车顶/楼顶）
            var s = cam.WorldToScreenPoint(p);
            var mx = Screen.width * k_ScreenMargin;
            var my = Screen.height * k_ScreenMargin;
            return s.z > 5f && s.z <= MaxDistFor(kind)
                && s.x >= -mx && s.x <= Screen.width + mx && s.y >= -my && s.y <= Screen.height + my;
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
                // 楼池白名单（§12 #57 ②）：查询层只排了组件有无，细口径在收楼时逐实体判定
                if (kind == 2 && !IsTalkativeBuilding(e))
                    continue;
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                p.y += kind == 0 ? k_YOffHuman : kind == 1 ? k_YOffCar : k_YOffBuilding; // 按气泡显示位置判（同 OnScreen 口径）
                var s = cam.WorldToScreenPoint(p);
                if (s.z < 5f || s.z > MaxDistFor(kind))
                    continue;
                if (s.x < 0 || s.x > Screen.width || s.y < 0 || s.y > Screen.height)
                    continue;
                scored.Add((s.z, e));
            }
            arr.Dispose();
            scored.Sort((a, b) => a.d.CompareTo(b.d));
            var now = PaceNow; // 节奏时钟（§12 #68）：关=墙钟/开=游戏时钟
            for (int i = 0; i < scored.Count && i < cap && m_Bubbles.Count < k_MaxBubbles; i++)
            {
                var b = new TrackedBubble
                {
                    Anchor = scored[i].e,
                    Kind = kind,
                    TextIdx = 0,
                    Speed = kind == 1 ? 3f : 0f, // 车未测先按在动（只配短句），人/楼按静止（§12 #51 长文稳锚）
                };
                SetBubbleText(ref b, 0);
                b.NextAt = now + HoldFor(scored[i].e.Index, 0, b.Text.Length); // 时长依赖文案，须在 SetBubbleText 之后
                m_Bubbles.Add(b);
            }
        }

        /// <summary>楼池白名单判定（§12 #57 ②④ + 2026-09-11 二轮治理，实机三现象定案——养鱼场排架/水域构件、
        /// 空楼、低密住宅不该一直说话；施工中大楼/露台仓库/学校操场不该说话）。Collect 收楼时逐实体调用，
        /// 判定全部 HasComponent/buffer Length 级别（仓储闸为租户侧一层解引用，spike 核准），禁止遍历嵌套。四档口径：
        /// ⓪ 二轮排除闸（spike: docs/spikes/2026-09-11-construction-and-openlot-signals.md，必须在①直收前——
        ///   操场正是从①漏入的）：UnderConstruction=施工中闭嘴（租户签约不避施工楼，有租户≠已建成，组件完工即移除）；
        ///   ServiceUpgrade/Extension=升级附属（学校操场/体育场等独立升级建筑+贴附扩展）开放空间归人；
        ///   ParkingFacility/CarParkingFacility=停车场/停车楼，声音归车档/行人；
        /// ① 市政/地标直收：School/Hospital/SignatureBuildingData 任一（市政建筑无租户概念；
        ///   SignatureBuildingData 是实体侧空标记组件，dump 实锤）；
        /// ② 可租物业需有租户：四 Property（住宅/商业/工业/办公，互斥挂其一）任一 且 Renter buffer
        ///   非空——没人租=没人在里面=不说话。<b>注意组件方向</b>：Game.Buildings.Renter 才是楼上的
        ///   租户 buffer（IBufferElementData，Serialization.RenterSystem 以 m_Property 为键维护，dump 实锤）；
        ///   Game.Buildings.PropertyRenter 是租户侧（住户/公司）指回房产的组件（IComponentData），别搞反；
        ///   仓储闸：任一租户是 Game.Companies.StorageCompany → 露台仓库/开放堆场，开放空间归人；
        /// ③ 低密住宅降频"人少话少"：住宅且租户 ≤2 户（一家人一栋楼）→ e.Index 散列 %3==0 才入池
        ///   （确定性散列：会话内稳定，不会因重采样闪进闪出）。</summary>
        private bool IsTalkativeBuilding(Entity e)
        {
            var em = EntityManager;
            // ⓪ 二轮排除闸（必须先于①直收）
            if (em.HasComponent<Game.Objects.UnderConstruction>(e))
                return false; // 施工中闭嘴
            if (em.HasComponent<Game.Buildings.ServiceUpgrade>(e)
                || em.HasComponent<Game.Buildings.Extension>(e))
                return false; // 升级附属（操场/体育场等）归人
            if (em.HasComponent<Game.Buildings.ParkingFacility>(e)
                || em.HasComponent<Game.Buildings.CarParkingFacility>(e))
                return false; // 停车场/停车楼归车档/行人
            // ① 市政/地标直收
            if (em.HasComponent<Game.Buildings.School>(e)
                || em.HasComponent<Game.Buildings.Hospital>(e)
                || em.HasComponent<Game.Prefabs.SignatureBuildingData>(e))
                return true;
            var isResidential = em.HasComponent<Game.Buildings.ResidentialProperty>(e);
            if (!isResidential
                && !em.HasComponent<Game.Buildings.CommercialProperty>(e)
                && !em.HasComponent<Game.Buildings.IndustrialProperty>(e)
                && !em.HasComponent<Game.Buildings.OfficeProperty>(e))
                return false; // 四 Property 全不中=非可租物业（纯构件/废墟/水域排架等），闭嘴
            // ② 租户非空（楼侧 Renter buffer；无 buffer 组件=从没被租过，同空处理）+ 仓储堆场闸
            if (!em.HasComponent<Game.Buildings.Renter>(e))
                return false;
            var renters = em.GetBuffer<Game.Buildings.Renter>(e, true);
            var renterCount = renters.Length;
            if (renterCount == 0)
                return false;
            for (int i = 0; i < renters.Length; i++)
                if (em.HasComponent<Game.Companies.StorageCompany>(renters[i].m_Renter))
                    return false; // 露台仓库/开放仓储堆场归人（仓储公司承租的场地不收）
            // ③ 低密住宅 1/3 降频：Knuth 乘性散列，%3==0 才入池（e.Index 会话内稳定=不闪）
            if (isResidential && renterCount <= 2 && (uint)e.Index * 2654435761u % 3u != 0u)
                return false;
            return true;
        }

        private bool HasAnchor(Entity e)
        {
            foreach (var b in m_Bubbles)
                if (b.Anchor == e)
                    return true;
            return false;
        }

        // —— S7 小剧场接口（改动最小化：四个 internal 口 + 生命周期回调，OnRender 热路径不碰）——

        /// <summary>剧场句柄懒解析（主菜单世界系统可能不存在，判空前绝不碰）。</summary>
        private BubbleTheaterSystem? Theater
            => m_Theater ??= World.GetExistingSystemManaged<BubbleTheaterSystem>();

        /// <summary>S7 剧场口：锚点是否在场（剧场终了判定"任一参与者锚点离屏即终了"用）。主线程低频。</summary>
        internal bool HasAnchorOf(Entity e) => HasAnchor(e);

        /// <summary>S7 剧场口：在场锚点快照（实体+类型+模拟坐标）**清空后**写入 dst。
        /// 供剧场选锚点（候选来自可见人锚点附近的环境点——本表天然全是屏内锚点）。主线程低频专用。</summary>
        internal void SnapshotAnchors(List<(Entity Anchor, byte Kind, float3 Pos)> dst)
        {
            dst.Clear();
            foreach (var b in m_Bubbles)
                if (EntityManager.Exists(b.Anchor) && EntityManager.HasComponent<Transform>(b.Anchor))
                    dst.Add((b.Anchor, b.Kind, EntityManager.GetComponentData<Transform>(b.Anchor).m_Position));
        }

        /// <summary>S7 剧场口：确保实体有气泡锚点（在场→true；否则过五道闸——层开/存在/有 Transform/
        /// 屏内（当前相机+分锚点距离档）/容量未满——立即按 Collect 同路径建组）。建组走 SetBubbleText，
        /// 剧场若已激活则插队分支直接给首句台词。任一闸不过 → false（剧场开播中止）。
        /// 分工（§12 #57 ③）：楼池白名单 IsTalkativeBuilding 只管气泡采样（Resample→Collect），
        /// 本口刻意不加——剧场室内锚点锚的是建筑本体，被采样池拒收的楼（如低密住宅降频未中签）
        /// 剧场照样可演；剧场有自己的选场逻辑（BubbleTheaterSystem 候选检测含场景判定）。</summary>
        internal bool EnsureAnchor(Entity e, byte kind)
        {
            if (!m_Active || !MasterOn)
                return false; // 层关着锚了也看不见
            if (HasAnchor(e))
                return true;
            if (m_Bubbles.Count >= k_MaxBubbles)
                return false;
            if (!EntityManager.Exists(e) || !EntityManager.HasComponent<Transform>(e))
                return false;
            var cam = m_CameraUpdate.activeCamera != null ? m_CameraUpdate.activeCamera
                : Camera.main != null ? Camera.main
                : Camera.allCameras.Length > 0 ? Camera.allCameras[0] : null;
            if (cam == null || !OnScreen(cam, e, kind))
                return false;
            var b = new TrackedBubble { Anchor = e, Kind = kind, TextIdx = 0, Speed = kind == 1 ? 3f : 0f };
            SetBubbleText(ref b, 0);
            b.NextAt = PaceNow + HoldFor(e.Index, 0, b.Text.Length); // 时长依赖文案，须在 SetBubbleText 之后（时钟源 §12 #68）
            m_Bubbles.Add(b);
            return true;
        }

        /// <summary>S7 剧场口：立即重取该锚点文案并重启驻留计时（剧场"上条读完下条接话"用）。
        /// <b>不</b>推剧场轮次——推进只走 TickLifecycle 的 OnAnchorRotated（本方法内 SetBubbleText →
        /// TryGetLine 是纯查询，无递归风险）。锚点不在场则空转。</summary>
        internal void RefreshAnchorText(Entity anchor)
        {
            for (int i = 0; i < m_Bubbles.Count; i++)
            {
                if (m_Bubbles[i].Anchor != anchor)
                    continue;
                var b = m_Bubbles[i];
                b.TextIdx++;
                SetBubbleText(ref b, b.TextIdx);
                b.NextAt = PaceNow + HoldFor(b.Anchor.Index, b.TextIdx, b.Text.Length);
                m_Bubbles[i] = b;
                return;
            }
        }

        // —— 生命周期：各气泡独立时钟（按字数缩放 + 确定性错相，绝不同时切换）——
        private void TickLifecycle()
        {
            var now = PaceNow; // 节奏时钟（§12 #68 选项统一源；剧场换句节拍跟着这里走）
            for (int i = 0; i < m_Bubbles.Count; i++)
            {
                var b = m_Bubbles[i];
                if (!EntityManager.Exists(b.Anchor))
                    continue; // 出组清理由 Resample 负责
                if (now >= b.NextAt)
                {
                    b.TextIdx++;
                    // S7 小剧场：当前说话人锚点到时换下一句→下一人（严格串行，2026-09-16 注记挂 §12 #67——
                    // 旁听锚点到期在 OnAnchorRotated 里被 CurrentAnchor 闸挡下，不推轮次；
                    // 剧场内部纯推进，再把新台词 RefreshAnchorText 到说话人锚点上）；非剧场锚点空转
                    Theater?.OnAnchorRotated(b.Anchor);
                    // 换气节奏（§12 #54）：一句说完歇一拍（奇数拍="……"隐身，绘制端跳过不画）——
                    // 一次性消耗下的节奏阀（消耗砍半）+ RimTalk 式"冒出一句→消失→再冒新句"；
                    // 剧场锚点由剧场侧自管显隐（严格串行：非当前说话人 Lines 恒"……"，轮流出现）；
                    // 事件快反期间照常走换气（§12 #58 定案：惊呼也轮播，不为事件搞特殊节拍）
                    if (b.TextIdx % 2 == 1 && (Theater == null || !Theater.HasActiveOn(b.Anchor)))
                        b.Text = "……";
                    else
                        SetBubbleText(ref b, b.TextIdx);
                    b.NextAt = now + HoldFor(b.Anchor.Index, b.TextIdx, b.Text.Length);
                    m_Bubbles[i] = b;
                }
            }
        }

        /// <summary>换文案唯一入口（采样建组/生命周期轮换/剧场建组与推轮都走这）：
        /// 最先小剧场插队（v5.1：锚点挂活剧场 → 剧本台词，纯查询不推轮次），
        /// 其次事件现场反应层（v5.7，§12 #70：受害模板/火灾楼本体/围观 LLM 卡，角色圈三层），
        /// 再次事件快反模板覆盖（v5.5，§12 #58：锚点在活动事件点 60m 内 → 反应模板），
        /// 再次闲聊炉片段池（v5.0 主源），取不到回退占位文案池（池空兜底——开局炉未出时气泡仍有话）。
        /// 优先级定案：剧场 > 现场反应层 > 事件快反模板 > 片段 > 隐身（"……"沉默拍）。</summary>
        private void SetBubbleText(ref TrackedBubble b, int textIdx)
        {
            // 实测移速（§12 #51 长文稳锚）：换文案间隔位移/时长的点估计；读不到位置维持旧值
            float3 anchorPos = default;
            var hasPos = false;
            if (EntityManager.HasComponent<Transform>(b.Anchor))
            {
                var pos = EntityManager.GetComponentData<Transform>(b.Anchor).m_Position;
                anchorPos = pos;
                hasPos = true;
                var nowS = UnityEngine.Time.unscaledTime;
                if (b.LastSetAt > 0f && nowS - b.LastSetAt > 0.5f)
                    b.Speed = math.distance(pos, b.LastPos) / (nowS - b.LastSetAt);
                b.LastPos = pos;
                b.LastSetAt = nowS;
            }
            var theater = Theater;
            if (theater != null && theater.TryGetLine(b.Anchor, out var theaterLine))
            {
                b.Text = theaterLine;
                if (m_BaseTextMaterial != null)
                    EnsureBaked(b.Text, b.Kind);
                return;
            }
            // 事件现场反应层（§12 #70）：受害市民锚→强制独白模板（轻伤骂街/重伤呼救）、
            // 燃烧建筑锚→"我家着火了"型、半径内锚→围观 LLM 卡（一次性消耗）。系统句柄懒解析判空
            // （主菜单世界可能不存在）；锚点读不到位置（无 Transform）时不判，直接走 #58 模板路径
            if (hasPos)
            {
                if (m_SceneReactions == null)
                    m_SceneReactions = World.GetExistingSystemManaged<EventSceneSystem>();
                if (m_SceneReactions != null && m_SceneReactions.TryGetSceneLine(b.Anchor, anchorPos, textIdx, out var sceneLine))
                {
                    b.Text = sceneLine;
                    if (m_BaseTextMaterial != null)
                        EnsureBaked(b.Text, b.Kind);
                    return;
                }
            }
            // 事件快反（§12 #58）：锚点在活动事件点 k_EventRadius 内 → 该事件类型反应模板覆盖
            // （不进片段池、不消耗一次性片段）；事件消失后下次换文案自然落回下方片段池路径，
            // 无需特殊处理。锚点读不到位置（无 Transform）时不判，直接走片段池
            if (hasPos && TryPickEventReaction(b, textIdx, anchorPos, out var reaction))
            {
                b.Text = reaction;
                if (m_BaseTextMaterial != null)
                    EnsureBaked(b.Text, b.Kind);
                return;
            }
            if (!TryPickSnippet(b, textIdx, out var text))
            {
                // 池空兜底（§12 #53/#54）："……"=隐身标记——绘制端见到即跳过（没内容就没泡，宁隐身不重复）
                text = "……";
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
            // 长文稳锚（§12 #51）：楼不限长；动的锚点按实测移速限长——快车只彪短句，堵车/站稳才说长话。
            // 楼 anchor 恒静止；人走路 ~1.5m/s 归中档；车未测速前按在动（初始化 3 m/s）
            var maxLen = b.Kind == 2 ? int.MaxValue
                : b.Speed > 3f ? 16
                : b.Speed > 0.6f ? 32
                : 48;
            var salt = (uint)(b.Anchor.Index + textIdx);
            for (uint k = 0; k < 8; k++)
            {
                var picked = pool.PickFor(occasion, salt + k, maxLen); // 纯探测（§12 #53：采用才 Consume）
                if (picked == null)
                    return false; // 池已空/该场合无候选——调用方落"……"沉默泡
                if (k < 7 && (m_UsedThisFrame.Contains(picked.Text) || m_InUseTexts.Contains(picked.Text)))
                    continue; // 在显冲突顺探下一条（探测不消耗，库存零浪费）
                m_UsedThisFrame.Add(picked.Text);
                pool.Consume(picked); // 真一次性：采用即从池删除
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

        // —— 事件快反层（v5.5，§12 #58）：活动事件表刷新 + 反应模板选取 ——

        /// <summary>低频刷新活动事件表（OnUpdate 128 帧错峰调用，别每帧全扫）。
        /// 口径照 EventNewsSystem 分类学实锤：火灾=OnFire 挂燃烧建筑本体（读 Transform 定位）；
        /// 车祸/犯罪=AccidentSite.m_Flags 位标志（TrafficAccident=8 / CrimeScene=4，
        /// CrimeFinished=16 已结案的跳过不算活动）。上限 k_MaxActiveEvents 条——
        /// 溢出按离相机近优先（玩家看得见的事件才配抢话；远处事件挤掉近处=白覆盖）。</summary>
        private void RefreshActiveEvents(Camera cam)
        {
            m_ActiveEvents.Clear();
            var fires = m_FireQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var e in fires)
            {
                var p = EntityManager.GetComponentData<Transform>(e).m_Position;
                m_ActiveEvents.Add(new ActiveEvent { Kind = BubbleEventKind.Fire, Pos = p, Source = e });
            }
            fires.Dispose();
            var sites = m_AccidentQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var e in sites)
            {
                var site = EntityManager.GetComponentData<Game.Events.AccidentSite>(e);
                if ((site.m_Flags & Game.Events.AccidentSiteFlags.CrimeFinished) != 0)
                    continue; // 已结案的旧现场不算活动（EventNewsSystem 同口径）
                var isCrime = (site.m_Flags & Game.Events.AccidentSiteFlags.CrimeScene) != 0;
                var isTraffic = (site.m_Flags & Game.Events.AccidentSiteFlags.TrafficAccident) != 0;
                if (!isCrime && !isTraffic)
                    continue;
                if (!TryGetSitePos(e, site, out var p))
                    continue; // 拿不到位置的现场无法做距离判定，不收
                m_ActiveEvents.Add(new ActiveEvent
                    { Kind = isCrime ? BubbleEventKind.Crime : BubbleEventKind.Traffic, Pos = p, Source = e });
            }
            sites.Dispose();

            // 超上限：离相机近优先（N 很小，每 128 帧一次排序开销可忽略）
            if (m_ActiveEvents.Count > k_MaxActiveEvents)
            {
                var camPos = (float3)cam.transform.position;
                m_ActiveEvents.Sort((x, y) => math.distancesq(x.Pos, camPos).CompareTo(math.distancesq(y.Pos, camPos)));
                m_ActiveEvents.RemoveRange(k_MaxActiveEvents, m_ActiveEvents.Count - k_MaxActiveEvents);
            }

            // 新事件一行一次性 INFO（防抖：同实体不重复打；被上限裁掉的不打，哪天挤进表再打）
            foreach (var ev in m_ActiveEvents)
                if (m_LoggedEvents.Add(ev.Source))
                    Mod.Log.Info($"[BubbleW] 事件快反：{KindName(ev.Kind)} @({ev.Pos.x:F0},{ev.Pos.z:F0})（活动 {m_ActiveEvents.Count} 条）");

            // 防抖集合清理：源实体不存在（火灭/现场撤除）即移除，防无限涨（EventNewsSystem.PruneReported 同款）
            m_LoggedPruneScratch.Clear();
            foreach (var e in m_LoggedEvents)
                if (!EntityManager.Exists(e))
                    m_LoggedPruneScratch.Add(e);
            foreach (var e in m_LoggedPruneScratch)
                m_LoggedEvents.Remove(e);
        }

        /// <summary>事故现场取位置：本体 Transform → m_Event 的 Transform → 放弃
        /// （EventNewsSystem.DirectionOfSite 同路径——AccidentSite 现场实体自身未必有 Transform）。</summary>
        private bool TryGetSitePos(Entity siteEntity, Game.Events.AccidentSite site, out float3 pos)
        {
            if (EntityManager.HasComponent<Transform>(siteEntity))
            {
                pos = EntityManager.GetComponentData<Transform>(siteEntity).m_Position;
                return true;
            }
            if (site.m_Event != Entity.Null && EntityManager.Exists(site.m_Event)
                && EntityManager.HasComponent<Transform>(site.m_Event))
            {
                pos = EntityManager.GetComponentData<Transform>(site.m_Event).m_Position;
                return true;
            }
            pos = default;
            return false;
        }

        /// <summary>事件快反选取（SetBubbleText 低频路径专用）：锚点在任一活动事件点 k_EventRadius 内 →
        /// 该事件类型的反应模板（多个命中取最近）。salt=锚点.Index+textIdx 确定性轮换——同泡连换两条
        /// 不重样（池深>1 时），驻留时长仍走 HoldFor 原节奏（模板不复用一次性语义，跨事件允许重复）。
        /// 无命中返回 false（调用方继续走片段池）。</summary>
        private bool TryPickEventReaction(TrackedBubble b, int textIdx, float3 anchorPos, out string text)
        {
            text = "";
            if (m_ActiveEvents.Count == 0)
                return false;
            var best = -1;
            var bestSq = k_EventRadius * k_EventRadius;
            for (int i = 0; i < m_ActiveEvents.Count; i++)
            {
                var d2 = math.distancesq(m_ActiveEvents[i].Pos, anchorPos);
                if (d2 <= bestSq) { bestSq = d2; best = i; }
            }
            if (best < 0)
                return false;
            var pool = m_ActiveEvents[best].Kind == BubbleEventKind.Fire ? k_FireReactions
                : m_ActiveEvents[best].Kind == BubbleEventKind.Traffic ? k_TrafficReactions
                : k_CrimeReactions;
            text = pool[(b.Anchor.Index + textIdx) % pool.Length];
            return true;
        }

        private static string KindName(BubbleEventKind kind)
            => kind == BubbleEventKind.Fire ? "fire" : kind == BubbleEventKind.Traffic ? "traffic" : "crime";

        /// <summary>气泡驻留时长：阅读时间 4s 起、每字 +0.28s、封顶 30s（话痨段落让人读完），
        /// 再叠 0-4s 确定性抖动（实体×集数散列——全屏绝不同时切换）。
        /// 默认墙钟语义（§12 #49）：配 unscaledTime 消费——倍速只加速模拟，不加速人阅读；
        /// §12 #68 起时钟源由设置页选项统一（PaceNow：开=Time.time 游戏时钟，暂停冻结、倍速放大）。
        /// 时长倍率读设置页 BubbleHoldScale（默认 1.5×，2026-09-11 玩家实机"更换太快"；封顶同步乘）。</summary>
        private static float HoldFor(int entityIndex, int textIdx, int textLen)
        {
            var read = 4f + textLen * 0.28f;
            var jitter = ((entityIndex * 7919 + textIdx * 104729) % 400) / 100f;
            var scale = Mod.Options?.BubbleHoldScale ?? 1f;
            return math.min((read + jitter) * scale, 30f * scale);
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
                entry.LastUsed = UnityEngine.Time.unscaledTime;
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

                // 第一遍：收集候选（位置/尺寸/屏幕包围盒），超距/镜头背后剔除；
                // 沉默拍（"……"）隐身不画——没内容就没泡（§12 #54 玩家定案，剧场"在听"同理：
                // 轮到谁说话谁冒泡，轮播对戏反而更清晰）
                m_Candidates.Clear();
                foreach (var b in m_Bubbles)
                {
                    if (!EntityManager.Exists(b.Anchor))
                        continue;
                    if (b.Text == "……")
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
                    p.y += b.Kind == 0 ? k_YOffHuman : b.Kind == 1 ? k_YOffCar : k_YOffBuilding; // 人头/车顶/楼顶
                    var dist = math.distance(camPos, p);
                    // 距离阈值滞回（2026-09-11 实机：锚点在可视边界上每帧进出=疯狂闪烁）——
                    // 新泡严格按 MaxDistFor，上帧在画的泡给 12% 越界宽限（只在边界带抖动才吃到）
                    var maxD = MaxDistFor(b.Kind);
                    if (dist > maxD && !(m_PrevKept.Contains(b.Anchor) && dist <= maxD * 1.12f))
                        continue;

                    // 恒定屏占按"行"：单行世界高 = 2·dist·tan(fov/2)·目标像素/屏高，夹 [0.35, 5]m；
                    // 块高 = 行高 × 行数——话痨段落按行数长高，不会缩成蚂蚁
                    var lineH = math.clamp(2f * dist * tanHalfFov * k_TargetPixels / cam.pixelHeight,
                        k_MinWorldH, k_MaxWorldH);
                    var worldH = lineH * entry.Lines;
                    var s = worldH / entry.Height;
                    var matrix = Matrix4x4.TRS((Vector3)p, rot, new Vector3(s, s, s))
                        * Matrix4x4.Translate(-entry.Center);
                    entry.LastUsed = UnityEngine.Time.unscaledTime;

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
                    // 屏缘惩罚（屏心优先）：归一化离屏心距离（0=屏心 1=角落外），排序键=视深×(1+1.5·edge)——
                    // 屏心泡 100m 仍赢屏缘泡 50m；"中央区域占大头"由权重自然涌现，不写死配比
                    var ex = (sp.x - Screen.width * 0.5f) / (Screen.width * 0.5f);
                    var ey = (sp.y - Screen.height * 0.5f) / (Screen.height * 0.5f);
                    var edge = math.min(1f, (ex * ex + ey * ey) * 0.5f);
                    // §12 #68 高速缓解：倍速 >1x 时移动锚点以倍速离画→提前轮换（"3x 下街上气泡过快"的
                    // 真因大头，物理真相只做缓解不根治）——打分乘移速惩罚偏好慢速/静止锚点，
                    // 速度档口径与 TryPickSnippet 限长同源（0.6/3 m/s 档），惩罚随倍速线性放大；
                    // timeScale 即游戏倍速（暂停=0 不罚，§12 #49 实锤倍速走 timeScale 缩放）
                    var speedPenalty = 1f;
                    if (m_GameSpeed > 1.05f)
                        speedPenalty += math.min(b.Speed, 3f) * 0.5f * (m_GameSpeed - 1f);
                    m_Candidates.Add(new DrawCandidate
                    {
                        Entry = entry,
                        TextMatrix = matrix,
                        Pos = p,
                        Dist = dist,
                        PlateH = plateH,
                        Bucket = bucket,
                        ScreenRect = new Rect(sp.x - w * 0.5f, sp.y - h * 0.5f, w, h),
                        Anchor = b.Anchor,
                        NextAt = b.NextAt,
                        AnchorSpeed = b.Speed,
                        Kind = b.Kind,
                        Score = dist * (1f + edge * 1.5f) * speedPenalty,
                    });
                }

                // 防叠（§12 #49 滞回版）：屏幕矩形互斥+同屏上限不变，中选人改三档优先级——
                // ① 剧场参与者必留（看戏不被路人顶掉）② 上帧在画且驻留期未满的保位（到换文案时刻才重新竞争，
                // 换文案时刻天然错相=整屏不一起换）③ 剩余坑位近者优先填满。
                // 旧版纯"近者优先"帧帧翻盘：人堆里深度微变→胜负手每帧换→泡互顶谁也没读完（2026-09-10 实机实锤）。
                // 可见端分类比例（§12 #51，"不是所有一起冒"）：楼 ≤cap/3、车 ≤cap/3、人不限（人为主 §12 #48 哲学）；
                // 剧场泡不受分类上限管（必留档高于一切）；但**计入同屏上限账**（2026-09-16 玩家定案，注记挂 §12 #67：
                // 剧场占用名额从普通泡预算扣——原"不占上限"（§12 #55）在严格串行前会让剧场多泡叠屏顶掉别人；
                // 剧场自身仍不受 cap 闸（必留），外部泡顶不掉它）
                m_Candidates.Sort((a, b) => a.Score.CompareTo(b.Score)); // 屏心优先的近者（非纯视深）
                m_Kept.Clear();
                m_KeptSet.Clear();
                // 同屏上限：设置页滑杆（§12 #53 稀疏默认，2026-09-11 实机"阅读不过来"改默认 4）；null（主菜单期）回落默认 4
                var cap = Mod.Options?.BubbleVisibleMax ?? 4;
                var kindCap = math.max(1, cap / 3); // 楼/车各 ≤1/3（cap 最小 2 时也保 1 个坑）
                var keptPlain = 0;   // 名额账计数（普通泡+剧场泡都记；上限只闸普通泡——剧场必留）
                var keptCar = 0;
                var keptBuilding = 0;
                var nowU = PaceNow; // 滞回保位比较必须与 NextAt 同时钟（§12 #68）
                var theater = Theater;
                foreach (var c in m_Candidates) // ①② 保位档（候选已近→远排好，保位也近者优先）
                {
                    var isTheater = theater != null && theater.HasActiveOn(c.Anchor);
                    if (!isTheater && keptPlain >= cap)
                        continue; // 普通泡到顶；剧场泡不限（继续扫，别 break——后面的剧场候选还要进）
                    var hold = nowU < c.NextAt && m_PrevKept.Contains(c.Anchor);
                    if (!isTheater && !hold)
                        continue;
                    if (!isTheater && c.Kind == 1 && keptCar >= kindCap) { continue; }
                    if (!isTheater && c.Kind == 2 && keptBuilding >= kindCap) { continue; }
                    var blocked = false;
                    foreach (var k in m_Kept)
                        if (c.ScreenRect.Overlaps(k.ScreenRect)) { blocked = true; break; }
                    if (!blocked)
                    {
                        m_Kept.Add(c);
                        m_KeptSet.Add(c.Anchor);
                        keptPlain++; // 名额账（2026-09-16 注记挂 §12 #67）：剧场泡也记数——它占的坑普通泡不能再填
                        if (!isTheater) { if (c.Kind == 1) keptCar++; else if (c.Kind == 2) keptBuilding++; } // 分类比例仍只闸普通泡
                    }
                }
                foreach (var c in m_Candidates) // ③ 填坑档
                {
                    if (keptPlain >= cap)
                        break;
                    if (m_KeptSet.Contains(c.Anchor))
                        continue;
                    if (c.Kind == 1 && keptCar >= kindCap) { continue; }
                    if (c.Kind == 2 && keptBuilding >= kindCap) { continue; }
                    var blocked = false;
                    foreach (var k in m_Kept)
                        if (c.ScreenRect.Overlaps(k.ScreenRect)) { blocked = true; break; }
                    if (!blocked)
                    {
                        m_Kept.Add(c);
                        m_KeptSet.Add(c.Anchor);
                        keptPlain++;
                        if (c.Kind == 1) keptCar++; else if (c.Kind == 2) keptBuilding++;
                    }
                }
                // 本帧中选集 = 下帧滞回依据（HashSet.Clear 保容量，逐帧零分配）
                m_PrevKept.Clear();
                foreach (var a in m_KeptSet)
                    m_PrevKept.Add(a);

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
