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

            // 内容引擎：话题雷达（读侧蒸馏快照）+ 内容导演（一炉出串/滴灌/T0 兜底）。
            updateSystem.UpdateAt<GameBridge.TopicRadarSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<GameBridge.ContentDirectorSystem>(SystemUpdatePhase.GameSimulation);

            // 实体级话题源：EventJournal 突发新闻 + 建筑/街道锚点采样
            updateSystem.UpdateAt<GameBridge.EventNewsSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<GameBridge.EntityAnchorSystem>(SystemUpdatePhase.GameSimulation);

            // M2-A 信息流面板：UI 数据桥挂 UIUpdate 阶段
            // （先例 city-storytelling-mod PromptUISystem / 官方 UI 系统均注册在此阶段）
            updateSystem.UpdateAt<GameBridge.CityLifeUISystem>(SystemUpdatePhase.UIUpdate);
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
