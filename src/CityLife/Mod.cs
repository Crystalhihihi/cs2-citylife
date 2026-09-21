using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using LlmFastProviderOption = CityLife.GameBridge.CityLifeSetting.LlmFastProviderOption;
using LlmProviderOption = CityLife.GameBridge.CityLifeSetting.LlmProviderOption;
using ThinkingTierOption = CityLife.GameBridge.CityLifeSetting.ThinkingTierOption;

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

        /// <summary>LLM 慢轨网关（§12 #59：thinking 默认开，服务主炉帖子/话题炉/剧场剧本/续热/市长/广告——要质量）。
        /// MUTE 开关走 CliGateway.Mute 静态属性（快慢两轨共用一个总闸）。</summary>
        public static Llm.CliGateway? Gateway { get; private set; }

        /// <summary>LLM 快轨网关（§12 #59：thinking 默认关，服务闲聊炉——量大句短，收益主在省 token）。
        /// 配置默认"同慢轨"（同 baseUrl/密钥，模型可另填）。</summary>
        public static Llm.CliGateway? FastGateway { get; private set; }

        /// <summary>游戏内设置页实例（选项→Mods→CityLife，玩家配置主源 §12 #45）。
        /// null = 未初始化/已 Dispose（主菜单期消费处一律回落默认值）。</summary>
        public static GameBridge.CityLifeSetting? Options { get; private set; }

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

            // 游戏内设置页（选项→Mods→CityLife）：玩家配置主源（§12 #45）。
            // LoadSettings 从 .coc 读回玩家改动（无文件则全默认）；RegisterInOptionsUI 注册进选项菜单；
            // 本地化中英双语（zh-CN 兜底注册同一中文字典）。
            Options = new GameBridge.CityLifeSetting(this);
            AssetDatabase.global.LoadSettings(nameof(CityLife), Options, new GameBridge.CityLifeSetting(this));
            Options.RegisterInOptionsUI();
            var lm = Game.SceneFlow.GameManager.instance.localizationManager;
            lm.AddSource("en-US", new Colossal.Localization.MemorySource(GameBridge.CityLifeLocalization.En));
            lm.AddSource("zh-HANS", new Colossal.Localization.MemorySource(GameBridge.CityLifeLocalization.Zh));
            lm.AddSource("zh-CN", new Colossal.Localization.MemorySource(GameBridge.CityLifeLocalization.Zh));
            Log.Info("[Settings] 游戏内设置页已注册（选项→Mods→CityLife）+ 中英本地化已注入");
            // 开机回读玩家当前值——验收/排查第一眼就能看到闸门口径
            // （2026-09-10 教训：feedMode=openOnly 收面板时主炉/话题炉/闲聊炉/剧场炉按设计全静默，日志零线索）
            Log.Info($"[Settings] 当前值：feedMode={Content.ModSettings.FeedMode} t0Fallback={Content.ModSettings.T0Fallback} " +
                     $"写回={Content.ModSettings.WriteBackTier} 信息流上限={Content.ModSettings.FeedMaxItems}｜" +
                     $"气泡 总开关={Options.BubbleEnabled} 距离倍率 人{Options.BubbleDistHuman:0.##}/车{Options.BubbleDistCar:0.##}/楼{Options.BubbleDistBuilding:0.##} " +
                     $"同屏上限={Options.BubbleVisibleMax} 闲聊={Options.BubbleChatterEnabled} 剧场={Options.BubbleTheaterEnabled} 底板={Options.BubblePlate}");

            // LLM 快慢双轨装配（§12 #59）：慢轨（Gateway）+ 快轨（FastGateway）各带泵组，两网关都 Start。
            // 配置主源=游戏内设置页（kGroupLlm 组）；llm.json 降级为首次迁移种子（SeedLlmFromJson）。
            // 供给不可用不致命，请求会走失败重试路径并计数，游戏照常（T0 模板兜底）。
            Llm.CliGateway.Log = msg => Log.Info(msg);
            Llm.KimiCliProvider.Log = msg => Log.Info(msg);
            Llm.OpenAiCompatibleProvider.Log = msg => Log.Info(msg);
            SeedLlmFromJson();
            AssembleGateways();

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
            // LLM 结果快速收炉泵（64 帧一拍，接管网关出队——导演自身 4096 帧节拍只管业务节奏了）
            updateSystem.UpdateAt<GameBridge.LlmResultPumpSystem>(SystemUpdatePhase.GameSimulation);

            // 实体级话题源：EventJournal 突发新闻 + 建筑/街道锚点采样
            updateSystem.UpdateAt<GameBridge.EventNewsSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<GameBridge.EntityAnchorSystem>(SystemUpdatePhase.GameSimulation);

            // 城市变化感知器：新落成/拆除/新路/人口门槛 → 变化锚点+快讯（"与我有关"的素材地基）
            updateSystem.UpdateAt<GameBridge.CityChangeSystem>(SystemUpdatePhase.GameSimulation);

            // 市民语境池（作者署名+处境：每个作者是一个真实市民的当下——"创造条件，不做限制"）
            updateSystem.UpdateAt<GameBridge.CitizenPoolSystem>(SystemUpdatePhase.GameSimulation);

            // 环境圈摘要（S6，§12 #48）：四叉树半径查询+聚类蒸馏"旁边有什么"，供闲聊炉组炉时缀处境卡
            updateSystem.UpdateAt<GameBridge.EnvironmentDigestSystem>(SystemUpdatePhase.GameSimulation);

            // 闲聊炉（S4，§12 #48）：3-5 游戏分钟一炉产气泡片段入 BubbleSnippetPool
            // （展示零 token；S5 才接气泡世界层，本系统只备池）
            updateSystem.UpdateAt<GameBridge.BubbleChatterSystem>(SystemUpdatePhase.GameSimulation);

            // M4 写回层：活动链状态机（意图解析 → 确认 → 扣款 → 注入 → 数人 → 结算 → 恢复）
            updateSystem.UpdateAt<GameBridge.EventChainSystem>(SystemUpdatePhase.GameSimulation);

            // 民意层 v2：市民请愿 → 超时未回应聚集市政厅/地标（活动链的逆向闭环，与其互斥防双注入）
            updateSystem.UpdateAt<GameBridge.PetitionSystem>(SystemUpdatePhase.GameSimulation);

            // 商家广告层 v1：店铺广告 → 评论互动 → 打折氛围排队（纯舆情无经济效果，spike 四轮实锤后定案）
            updateSystem.UpdateAt<GameBridge.ShopAdSystem>(SystemUpdatePhase.GameSimulation);

            // M2-A 信息流面板：UI 数据桥挂 UIUpdate 阶段
            // （先例 city-storytelling-mod PromptUISystem / 官方 UI 系统均注册在此阶段）
            updateSystem.UpdateAt<GameBridge.CityLifeUISystem>(SystemUpdatePhase.UIUpdate);

            // M3-W：世界渲染 spike——走游戏原生 OverlayRenderSystem（DrawText/DrawCustomMesh/DrawCircle）。
            // 相位调查（2026-08-21 计数全 0）：先挂 GameSimulation 验证"写入时机/清空顺序"嫌疑
            // （OverlayRenderSystem 若在 GameSimulation 消费，Rendering 相位的写入可能先清后拷被永久跳过）
            updateSystem.UpdateAt<GameBridge.BubbleWorldSpikeSystem>(SystemUpdatePhase.GameSimulation);

            // 多人小剧场（S7，§12 #48 多人小剧场段）：对话锚点成组+真名单绑定+轮流冒泡（同屏 ≤2）。
            // 登记在气泡世界层之后：同相序内先跑完气泡采样/生命周期，剧场再读锚点快照/验活
            updateSystem.UpdateAt<GameBridge.BubbleTheaterSystem>(SystemUpdatePhase.GameSimulation);

            // 事件现场反应层（§12 #70，2026-09-17 玩家拍板）：车祸/火灾/犯罪三层角色圈（受害模板/楼内模板/
            // 围观快轨卡）+死亡传闻限流；读侧只读 128 帧错峰，写侧系统不碰
            updateSystem.UpdateAt<GameBridge.EventSceneSystem>(SystemUpdatePhase.GameSimulation);

            // 普查+事件监听口 spike（2026-09-14，§12 #62/#64/#65 收口工具：Ctrl+6 普查 dump / Ctrl+7 事件探针开关）。
            // 只读；验证完即退役——删本行即可。报告 docs/spikes/2026-09-14-census-and-event-watch.md
            updateSystem.UpdateAt<GameBridge.CensusSpikeSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
            s_LlmAssembled = false;
            // 网关后台线程随 Dispose 回收（进行中的 one-shot 最多等 2s，见 CliGateway.Stop）
            Gateway?.Dispose();
            Gateway = null;
            FastGateway?.Dispose();
            FastGateway = null;
            ChirpChannel = null;
            Options = null;
        }

        // ------------------------------------------------------------------
        // LLM 快慢双轨：装配 / llm.json 迁移种子 / 设置页热切换（§12 #59）
        // 热切换链路：游戏 UI 每次提交改动都走 Setting.ApplyAndSave→Apply
        // （AutomaticSettings 逐字段实锤，2026-09-11 ilspy）→ CityLifeSetting.Apply
        // → OnOptionsApplied 比对供给签名 → 变了的那轨热重建。
        // ------------------------------------------------------------------

        /// <summary>双轨装配完成标记。Apply 钩子在装配完成前也可能触发（迁移种子自己的 ApplyAndSave 就是），必须挡掉。</summary>
        private static bool s_LlmAssembled;

        /// <summary>慢轨/快轨当前供给签名（供给相关字段拼接，仅内存比对；含密钥原文，绝不进日志）。</summary>
        private static string s_SlowSig = "";
        private static string s_FastSig = "";

        /// <summary>一轨的供给参数快照（装配 provider 的最小集）。</summary>
        private struct TrackParts
        {
            /// <summary>true=KimiCli 本机 CLI 订阅轨；false=OpenAI 兼容 HTTP 轨。</summary>
            public bool IsKimi;
            public string BaseUrl;
            public string Key;
            public string Model;
            /// <summary>thinking 档位（"off"/"high"，对应设置页 ThinkingTierOption 两档）：
            /// "off"=请求体带 thinking.disabled；"high"=显式 effort=high。KimiCli 轨忽略。</summary>
            public string Thinking;
        }

        /// <summary>
        /// llm.json → 设置页的一次性迁移（§12 #59：llm.json 降级为首次迁移种子）。
        /// 口径：LlmSeededFromJson=false 时读一次（文件不存在也置 true——只迁移一次，此后设置页为唯一主源）；
        /// openai-compatible 按 baseUrl 归并到硅基流动/DeepSeek 预设，归并不了落自定义；
        /// kimi-cli 保持默认 KimiCli 预设（无字段可迁）。迁移结果立即 ApplyAndSave 持久化。
        /// </summary>
        private static void SeedLlmFromJson()
        {
            var o = Options;
            if (o == null || o.LlmSeededFromJson)
            {
                return;
            }
            o.LlmSeededFromJson = true;
            try
            {
                // 配置文件在游戏用户目录（仓库外，API key 绝不进 git）
                var cfgPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    @"..\LocalLow\Colossal Order\Cities Skylines II\ModsSettings\CityLife\llm.json"));
                if (System.IO.File.Exists(cfgPath))
                {
                    var cfg = Llm.ProviderConfig.Load(cfgPath, msg => Log.Info(msg));
                    if (cfg.Provider == "openai-compatible")
                    {
                        o.LlmSlowProvider = cfg.BaseUrl.Contains("siliconflow")
                            ? LlmProviderOption.SiliconFlow
                            : cfg.BaseUrl.Contains("deepseek")
                                ? LlmProviderOption.DeepSeek
                                : LlmProviderOption.CustomOpenAi;
                        o.LlmSlowBaseUrl = cfg.BaseUrl;
                        // 分轨字段（2026-09-20 llm.json 扩展）优先，全局 model/thinking 兜底
                        o.LlmSlowModel = cfg.EffectiveSlowModel;
                        o.LlmSlowApiKey = cfg.ApiKey;
                        // thinking 档位映射（2026-09-21 档位旋钮）：UI 两档 Off/High；json 手写其他档位值
                        // （"medium"/"low" 等）不拦——一律按 High 落（非 "disabled" 即视为要思考），不校验不报错
                        o.LlmSlowThinking = cfg.EffectiveSlowThinking != "disabled"
                            ? ThinkingTierOption.High : ThinkingTierOption.Off;
                        if (cfg.EffectiveFastModel.Length > 0)
                        {
                            o.LlmFastModel = cfg.EffectiveFastModel; // 快轨分叉模型（SameAsSlow 预设下按此分叉）
                            o.LlmFastThinking = cfg.EffectiveFastThinking != "disabled"
                                ? ThinkingTierOption.High : ThinkingTierOption.Off;
                        }
                        // 整 provider 分轨（2026-09-20）：fast 段带独立端点/key 时连快轨供给端一起迁
                        // （快轨硅基、慢轨官方的双端点形态；baseUrl/key 只在迁移期落设置页，永不进日志）
                        if (cfg.FastBaseUrl.Length > 0 || cfg.FastApiKey.Length > 0)
                        {
                            var fb = cfg.EffectiveFastBaseUrl;
                            o.LlmFastProvider = fb.Contains("siliconflow")
                                ? LlmFastProviderOption.SiliconFlow
                                : fb.Contains("deepseek")
                                    ? LlmFastProviderOption.DeepSeek
                                    : LlmFastProviderOption.CustomOpenAi;
                            o.LlmFastBaseUrl = fb;
                            if (cfg.EffectiveFastApiKey.Length > 0)
                                o.LlmFastApiKey = cfg.EffectiveFastApiKey;
                        }
                    }
                    Log.Info("[LLM] 已从 llm.json 迁移供给配置进设置页（仅此一次，此后 llm.json 不再读取）");
                }
                o.ApplyAndSave(); // 持久化迁移结果+标记位（此刻 s_LlmAssembled=false，Apply 回调空转安全）
            }
            catch (System.Exception e)
            {
                Log.Warn($"[LLM] llm.json 迁移种子失败（按设置页当前值继续）：{e.Message}");
            }
        }

        /// <summary>按设置页当前值装配双轨网关并 Start。启动日志慢轨/快轨各一行（供给+thinking 状态）。</summary>
        private static void AssembleGateways()
        {
            var o = Options!;

            var slowParts = ResolveSlowParts(o);
            Gateway = new Llm.CliGateway(BuildProvider(slowParts));
            Gateway.Start();
            s_SlowSig = SlowSignature(o);
            Log.Info($"[LLM] 慢轨网关已启动：{Describe(slowParts)}");

            var fastParts = ResolveFastParts(o, slowParts);
            FastGateway = new Llm.CliGateway(BuildProvider(fastParts));
            FastGateway.Start();
            s_FastSig = FastSignature(o);
            Log.Info($"[LLM] 快轨网关已启动：{Describe(fastParts)}");

            s_LlmAssembled = true;
        }

        /// <summary>设置页回调（CityLifeSetting.Apply 转调）：比对供给签名，只热重建变了的那轨。
        /// 任何字段（含气泡滑杆）变更都会进这里，签名 diff 是廉价字符串比较。</summary>
        internal static void OnOptionsApplied()
        {
            if (!s_LlmAssembled || Options == null)
            {
                return;
            }
            var o = Options;
            if (SlowSignature(o) != s_SlowSig)
            {
                SwapGateway(slowTrack: true);
            }
            // 快轨签名含慢轨前缀（SameAsSlow 语义下慢轨变更必须连带重建快轨），慢轨换血后这里自然跟着换
            if (FastSignature(o) != s_FastSig)
            {
                SwapGateway(slowTrack: false);
            }
        }

        /// <summary>热重建一轨：先建+Start 新网关再原子换引用（读侧无空窗），旧网关后台 Stop（join 不卡 UI 线程）。</summary>
        private static void SwapGateway(bool slowTrack)
        {
            var o = Options;
            if (o == null)
            {
                return;
            }
            try
            {
                var parts = slowTrack ? ResolveSlowParts(o) : ResolveFastParts(o, ResolveSlowParts(o));
                var fresh = new Llm.CliGateway(BuildProvider(parts));
                fresh.Start();
                var old = slowTrack ? Gateway : FastGateway;
                if (slowTrack)
                {
                    Gateway = fresh;
                    s_SlowSig = SlowSignature(o);
                }
                else
                {
                    FastGateway = fresh;
                    s_FastSig = FastSignature(o);
                }
                Log.Info($"[LLM] {(slowTrack ? "慢轨" : "快轨")}供给已热切换：{Describe(parts)}");
                if (old != null)
                {
                    // 旧网关后台回收：Stop 带 ≤2s/线程 join；在飞请求取消即弃（one-shot 无副作用，AI 内容宁缺毋滥）
                    System.Threading.Tasks.Task.Run(() => old.Dispose());
                }
            }
            catch (System.Exception e)
            {
                Log.Warn($"[LLM] {(slowTrack ? "慢轨" : "快轨")}热切换失败（保持旧供给）：{e.Message}");
            }
        }

        // \x1/\x2 作分隔符防字段内容粘连串扰；签名只进内存比对，密钥原文绝不写日志
        private static string SlowSignature(GameBridge.CityLifeSetting o)
            => string.Join("\x1", (int)o.LlmSlowProvider, o.LlmSlowBaseUrl, o.LlmSlowModel, o.LlmSlowApiKey, (int)o.LlmSlowThinking);

        private static string FastSignature(GameBridge.CityLifeSetting o)
            => SlowSignature(o) + "\x2" + string.Join("\x1",
                (int)o.LlmFastProvider, o.LlmFastBaseUrl, o.LlmFastModel, o.LlmFastApiKey, (int)o.LlmFastThinking);

        /// <summary>慢轨供给参数解析：KimiCli 无参数；DeepSeek/硅基流动 baseUrl 内置、模型空=推荐；自定义全取文本框。</summary>
        private static TrackParts ResolveSlowParts(GameBridge.CityLifeSetting o)
        {
            var p = new TrackParts { Thinking = o.LlmSlowThinking == ThinkingTierOption.High ? "high" : "off" };
            switch (o.LlmSlowProvider)
            {
                case LlmProviderOption.KimiCli:
                    p.IsKimi = true;
                    break;
                case LlmProviderOption.CustomOpenAi:
                    p.BaseUrl = (o.LlmSlowBaseUrl ?? "").Trim();
                    p.Key = (o.LlmSlowApiKey ?? "").Trim();
                    p.Model = (o.LlmSlowModel ?? "").Trim();
                    break;
                default: // DeepSeek / SiliconFlow
                    p.BaseUrl = PresetBaseUrl(o.LlmSlowProvider);
                    p.Key = (o.LlmSlowApiKey ?? "").Trim();
                    var m = (o.LlmSlowModel ?? "").Trim();
                    p.Model = m.Length > 0 ? m : PresetDefaultModel(o.LlmSlowProvider);
                    break;
            }
            return p;
        }

        /// <summary>快轨供给参数解析：SameAsSlow 继承慢轨 baseUrl/密钥/模型（模型可另填分叉），thinking 走自己的档位。</summary>
        private static TrackParts ResolveFastParts(GameBridge.CityLifeSetting o, TrackParts slow)
        {
            var p = new TrackParts { Thinking = o.LlmFastThinking == ThinkingTierOption.High ? "high" : "off" };
            switch (o.LlmFastProvider)
            {
                case LlmFastProviderOption.SameAsSlow:
                    p = slow;
                    p.Thinking = o.LlmFastThinking == ThinkingTierOption.High ? "high" : "off";
                    var fork = (o.LlmFastModel ?? "").Trim();
                    if (fork.Length > 0)
                    {
                        p.Model = fork;
                    }
                    break;
                case LlmFastProviderOption.KimiCli:
                    p.IsKimi = true;
                    break;
                case LlmFastProviderOption.CustomOpenAi:
                    p.BaseUrl = (o.LlmFastBaseUrl ?? "").Trim();
                    p.Key = (o.LlmFastApiKey ?? "").Trim();
                    p.Model = (o.LlmFastModel ?? "").Trim();
                    break;
                default: // DeepSeek / SiliconFlow
                    var preset = o.LlmFastProvider == LlmFastProviderOption.DeepSeek
                        ? LlmProviderOption.DeepSeek
                        : LlmProviderOption.SiliconFlow;
                    p.BaseUrl = PresetBaseUrl(preset);
                    p.Key = (o.LlmFastApiKey ?? "").Trim();
                    var m = (o.LlmFastModel ?? "").Trim();
                    p.Model = m.Length > 0 ? m : PresetDefaultModel(preset);
                    break;
            }
            return p;
        }

        private static Llm.ICliProvider BuildProvider(TrackParts p)
            => p.IsKimi
                ? (Llm.ICliProvider)new Llm.KimiCliProvider()
                // thinking 档位透传（2026-09-21 档位旋钮）："high"=显式 effort=high（2026-09-20 双轨定轨：
                // 厂商默认档位非 high 实锤，"开"定标推荐高档；v4-flash 实锤兼容 effort=high 不报错）；
                // "off"=disabled。档位值→请求体字段的拼装细节在 OpenAiCompatibleProvider
                : new Llm.OpenAiCompatibleProvider(p.BaseUrl, p.Key, p.Model, p.Thinking == "high" ? "high" : "disabled");

        /// <summary>预设内置端点（仅 DeepSeek/硅基流动；其余返回空）。</summary>
        private static string PresetBaseUrl(LlmProviderOption p)
        {
            switch (p)
            {
                case LlmProviderOption.DeepSeek: return "https://api.deepseek.com/v1";
                case LlmProviderOption.SiliconFlow: return "https://api.siliconflow.cn/v1";
                default: return "";
            }
        }

        /// <summary>预设推荐模型（设置页留空时的兜底；仅 DeepSeek/硅基流动）。</summary>
        private static string PresetDefaultModel(LlmProviderOption p)
        {
            switch (p)
            {
                case LlmProviderOption.DeepSeek: return "deepseek-chat";
                case LlmProviderOption.SiliconFlow: return "deepseek-ai/DeepSeek-R1";
                default: return "";
            }
        }

        /// <summary>日志描述（密钥永不进日志）。thinking 打档位原值（off/high）——
        /// 档位变更触发热切换时本行即"快/慢轨 thinking=off/high"变更日志（§12 #59 旋钮）。</summary>
        private static string Describe(TrackParts p)
            => p.IsKimi
                ? "KimiCli（本机 CLI 订阅轨）"
                : $"OpenAiCompat 端点={p.BaseUrl} 模型={p.Model} thinking={p.Thinking}";
    }
}
