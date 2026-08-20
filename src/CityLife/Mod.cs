using Colossal.Logging;
using Game;
using Game.Modding;

namespace CityLife
{
    /// <summary>
    /// Mod 入口。职责仅限：全局日志、系统登记、共享单例（网关/发帖通道），不挂任何业务逻辑。
    /// 架构约定（详见 docs/specs 设计文档，改代码前必读）：
    /// 1. 版本敏感面只允许出现在 GameBridge 命名空间下；
    /// 2. 写回走"单向阀门"：LLM 输出经 schema 校验 + 分档映射，绝不直接改模拟数值；
    /// 3. 加 System 不改游戏。
    /// </summary>
    public class Mod : IMod
    {
        /// <summary>全局日志。按 mod 名分频道；发布版保持 ShowsErrorsInUI=false，错误不弹窗打扰玩家。</summary>
        public static ILog Log { get; } = LogManager.GetLogger(nameof(CityLife)).SetShowsErrorsInUI(false);

        /// <summary>LLM 网关（后台线程泵，全 one-shot）。MUTE 开关走 CliGateway.Mute 静态属性。</summary>
        public static Llm.CliGateway? Gateway { get; private set; }

        /// <summary>发帖通道（CustomChirps 软依赖反射桥）。<b>已退役留档</b>（2026-08-20：面板直供 FeedStore 后不再创建；
        /// 代码保留——自研发帖内核/兼容层若需要可在此复活）。</summary>
        public static GameBridge.CustomChirpsChannel? ChirpChannel { get; set; }

        /// <summary>信息流仓库（M2 面板数据源）。每发一帖由导演/突发系统登记。</summary>
        public static Content.FeedStore Feed { get; } = new Content.FeedStore();

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"[{nameof(CityLife)}] OnLoad：T1 内容引擎 + 突发新闻钩子 + M0 探针/spike");

            // 原版 chirp 发布侧过滤器（Harmony 前缀，M2-C 收尾）：
            // PublishAddedChirps 前把新 chirp 标记 Deleted。补丁失败仅警告（原版会继续显示，不影响其他功能）。
            try
            {
                new HarmonyLib.Harmony(nameof(CityLife)).PatchAll(typeof(Mod).Assembly);
                Log.Info("[Filter] 原版 chirp 过滤补丁已应用");
            }
            catch (System.Exception e)
            {
                Log.Warn($"[Filter] Harmony 补丁应用失败（原版 chirp 会继续显示，不影响其他功能）：{e.Message}");
            }

            // M1 CLI 网关装配：日志注入 + 启动后台泵。供给不可用不致命，
            // 请求会走失败重试路径并计数，游戏照常（T0 模板兜底）。
            Llm.CliGateway.Log = msg => Log.Info(msg);
            Llm.KimiCliProvider.Log = msg => Log.Info(msg);
            Llm.OpenAiCompatibleProvider.Log = msg => Log.Info(msg);
            // 供给选择（双轨，§12 #18）：llm.json 指定 openai-compatible 走 API 计费轨，
            // 否则默认 KimiCli 订阅轨。配置文件在游戏用户目录，API key 不进仓库。
            var cfgPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                @"..\LocalLow\Colossal Order\Cities Skylines II\ModsSettings\CityLife\llm.json"));
            var cfg = Llm.ProviderConfig.Load(cfgPath, msg => Log.Info(msg));
            Llm.ICliProvider provider = cfg.Provider == "openai-compatible"
                ? new Llm.OpenAiCompatibleProvider(cfg.BaseUrl, cfg.ApiKey, cfg.Model, cfg.Thinking)
                : (Llm.ICliProvider)new Llm.KimiCliProvider();
            Gateway = new Llm.CliGateway(provider);
            Gateway.Start();
            Log.Info($"[LLM] 网关已启动，{provider.Name} 可用={provider.IsAvailable()}");

            // M0 读侧探针：只读不写，验证 ECS 读取链路。
            // 后续系统登记位置约定（实现到时复核）：
            //   读侧/话题检测类 → SystemUpdatePhase.GameSimulation（本系统）
            //   写回/注入类     → Modification 阶段（游戏原生写系统之后），M4 前定
            //   UI 数据类       → SystemUpdatePhase.UIUpdate
            updateSystem.UpdateAt<GameBridge.ReadProbeSystem>(SystemUpdatePhase.GameSimulation);

            // M0 spike ④：TripNeeded 注入压测（Ctrl+K 抢占实验 / Ctrl+L 大规模压测）。
            // 验证完成后拆除，正式版进"原语执行器库"（设计文档 §4 M6）。
            updateSystem.UpdateAt<GameBridge.InjectionSpikeSystem>(SystemUpdatePhase.GameSimulation);

            // 实质性 spike（2026-08-20）：购物注入是否真成交（Ctrl+B）。验证后拆除，结论决定商家广告层形态。
            updateSystem.UpdateAt<GameBridge.ShoppingSpikeSystem>(SystemUpdatePhase.GameSimulation);

            // 内容引擎：话题雷达（读侧蒸馏快照）+ 内容导演（一炉出串/滴灌/T0 兜底）。
            updateSystem.UpdateAt<GameBridge.TopicRadarSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<GameBridge.ContentDirectorSystem>(SystemUpdatePhase.GameSimulation);

            // 实体级话题源：EventJournal 突发新闻 + 建筑/街道锚点采样
            updateSystem.UpdateAt<GameBridge.EventNewsSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<GameBridge.EntityAnchorSystem>(SystemUpdatePhase.GameSimulation);

            // 城市变化感知器：新落成/拆除/新路/人口门槛 → 变化锚点+快讯（"与我有关"的素材地基）
            updateSystem.UpdateAt<GameBridge.CityChangeSystem>(SystemUpdatePhase.GameSimulation);

            // 市民语境池（作者署名+处境：每个作者是一个真实市民的当下——"创造条件，不做限制"）
            updateSystem.UpdateAt<GameBridge.CitizenPoolSystem>(SystemUpdatePhase.GameSimulation);

            // M4 写回层：活动链状态机（意图解析 → 确认 → 扣款 → 注入 → 数人 → 结算 → 恢复）
            updateSystem.UpdateAt<GameBridge.EventChainSystem>(SystemUpdatePhase.GameSimulation);

            // 民意层 v2：市民请愿 → 超时未回应聚集市政厅/地标（活动链的逆向闭环，与其互斥防双注入）
            updateSystem.UpdateAt<GameBridge.PetitionSystem>(SystemUpdatePhase.GameSimulation);

            // 商家广告层 v1：店铺广告 → 评论互动 → 打折氛围排队（纯舆情无经济效果，spike 四轮实锤后定案）
            updateSystem.UpdateAt<GameBridge.ShopAdSystem>(SystemUpdatePhase.GameSimulation);

            // M2-A 信息流面板：UI 数据桥挂 UIUpdate 阶段
            // （先例 city-storytelling-mod PromptUISystem / 官方 UI 系统均注册在此阶段）
            updateSystem.UpdateAt<GameBridge.CityLifeUISystem>(SystemUpdatePhase.UIUpdate);

            // M3-spike：气泡渲染管线验证（Ctrl+1/2/3=100/300/600 个，Ctrl+0=关；日志 [Bubble] FPS avg）
            updateSystem.UpdateAt<GameBridge.BubbleSpikeSystem>(SystemUpdatePhase.UIUpdate);

            // M3-W S1：世界空间渲染气泡探索（Ctrl+9 单气泡渲染验证；日志 [BubbleW] 着色器枚举）
            updateSystem.UpdateAt<GameBridge.BubbleWorldSpikeSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
            // 网关后台线程随 Dispose 回收（进行中的 one-shot 最多等 2s，见 CliGateway.Stop）
            Gateway?.Dispose();
            Gateway = null;
            ChirpChannel = null;
        }
    }
}
