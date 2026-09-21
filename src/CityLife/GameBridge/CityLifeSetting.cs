using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 游戏内设置页（选项 → Mods → CityLife）——玩家配置的唯一主源（§12 #45，2026-09-09 定案）。
    /// 与 settings.json 的分工：本类是玩家旋钮；settings.json 只留开发级旋钮
    /// （冷却/调试等不进 UI 的字段，见 Content.ModSettings，四键 feedMode/t0Fallback/writeBackTier/feedMaxItems
    /// 已从这里迁移、JSON Loader 不再读取）。
    /// 持久化走游戏 SettingAsset（.coc）；FileLocation 让落点在 ModsSettings/CityLife 配置目录旁。
    /// 显示名/说明走正规本地化（CityLifeLocalization 中英字典），不用 SettingsUIDisplayName 硬编码。
    /// 消费处直读 <c>Mod.Options?.属性 ?? 默认值</c>——必须容忍 null（主菜单期/未初始化）。
    /// LLM 供给组（kGroupLlm，§12 #59 快慢双轨）：配置主源在此，llm.json 仅在首次启动时
    /// 被读一次做迁移种子（LlmSeededFromJson 标记位，见 Mod.SeedLlmFromJson）。
    /// 同步口径（写死，2026-09-21 档位旋钮立项时复核）：**.coc=运行时唯一真源，llm.json=首启种子/备份**——
    /// 运行时绝不回读 llm.json，种子迁移后两处各自独立演化（llm.json 手写新值不会再进 .coc，除非重置标记位）。
    /// 热切换：游戏 UI 每次提交改动都走 ApplyAndSave→Apply（AutomaticSettings 逐字段实锤），
    /// 本类重写 Apply 回调 Mod.OnOptionsApplied()，由 Mod 比对供给签名决定是否热重建网关。
    ///
    /// 如何扩展（加一个选项）：
    /// 1. 这里加一个带 [SettingsUISection(kTab, kGroupXxx)] 的公共属性（bool=开关、enum=下拉、
    ///    int/float+[SettingsUISlider]=滑杆，无需别的标注）；
    /// 2. CityLifeLocalization.En/Zh 两个字典各加同名键（Options.OPTION/OPTION_DESCRIPTION[...]，
    ///    枚举另加 Options.{id}.{枚举类型名大写}[值名] 三个值各一键）；
    /// 3. SetDefaults() 里补一行默认值；
    /// 4. 消费处直读，无需接线——UI 改的是同一个实例，读取即时生效。
    /// </summary>
    [FileLocation("ModsSettings/CityLife")]
    [SettingsUIGroupOrder(kGroupBubble, kGroupFeed, kGroupLlm)]
    [SettingsUIShowGroupName(kGroupBubble, kGroupFeed, kGroupLlm)] // 组名"对话气泡/信息流/LLM 供给"显示为分组标题（无此标注 UI 不显示组名）
    public class CityLifeSetting : ModSetting
    {
        /// <summary>设置页 tab 名（全部选项收在一个 tab）。</summary>
        public const string kTab = "CityLife";
        /// <summary>气泡组名。</summary>
        public const string kGroupBubble = "Bubble";
        /// <summary>信息流组名。</summary>
        public const string kGroupFeed = "Feed";
        /// <summary>LLM 供给组名（§12 #59）。</summary>
        public const string kGroupLlm = "Llm";

        /// <summary>信息流生成节拍与面板开合的联动（§12 #45 自 settings.json 迁移）：
        /// Always 常开（城市自己在活着）/ OpenOnly 仅展开（收起即停生成，省 token）/ Throttled 节流（收起降频保温）。</summary>
        public enum FeedModeOption { Always, OpenOnly, Throttled }

        /// <summary>写回强度档（§6 红线）：Mild 体验档 ×0.5 / Normal 正常档 ×1.0 / Crazy 疯狂档 ×2.0（带警示）。</summary>
        public enum WriteBackTierOption { Mild, Normal, Crazy }

        /// <summary>LLM 供给预设（§12 #59）。KimiCli=本机 Kimi Code CLI 订阅轨（无需密钥）；
        /// DeepSeek/ SiliconFlow=对应官方端点（baseUrl 内置，只需密钥+模型）；
        /// CustomOpenAi=自定义 OpenAI 兼容端点（baseUrl 用文本框里的值）。</summary>
        public enum LlmProviderOption { KimiCli, DeepSeek, SiliconFlow, CustomOpenAi }

        /// <summary>快轨供给预设。SameAsSlow（默认）=与慢轨同一家（同 baseUrl/密钥，模型可另填），
        /// 其余取值语义同 <see cref="LlmProviderOption"/>。</summary>
        public enum LlmFastProviderOption { SameAsSlow, KimiCli, DeepSeek, SiliconFlow, CustomOpenAi }

        /// <summary>thinking 档位（§12 #59 旋钮，2026-09-21 立项：玩家口径"high 不要 max"，UI 先两档）。
        /// Off=关（请求体带 thinking.disabled）；High=高（显式 effort=high——厂商默认档位非 high 实锤，
        /// "开"必须定标推荐高档，v4-flash 实锤兼容不报错）。底层值域是字符串（ProviderConfig 口径：
        /// ""/"disabled"/"high"/"medium"/"low"），llm.json 手写其他档位种子迁移不拦（一律按 High 落，
        /// 档位扩张只加枚举值+本地化键，不动映射）。</summary>
        public enum ThinkingTierOption { Off, High }

        public CityLifeSetting(IMod mod) : base(mod)
        {
        }

        // —— 气泡组 ——

        /// <summary>气泡总开关（默认开）。与 Ctrl+9 会话内快速开关是 AND 关系——本开关为权威总闸。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubbleEnabled { get; set; } = true;

        /// <summary>行人气泡可见距离倍率（1.0 = 基础档 125m，§12 #44 实机标定；滑杆 0.25-4×）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage", scalarMultiplier = 100f)]
        public float BubbleDistHuman { get; set; } = 1f;

        /// <summary>车辆气泡可见距离倍率（1.0 = 基础档 320m）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage", scalarMultiplier = 100f)]
        public float BubbleDistCar { get; set; } = 1f;

        /// <summary>建筑气泡可见距离倍率（1.0 = 基础档 800m）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage", scalarMultiplier = 100f)]
        public float BubbleDistBuilding { get; set; } = 1f;

        /// <summary>同屏气泡上限（默认 4，§12 #53：稀疏默认——"不是满屏冒泡"；2026-09-11 玩家实机"有些阅读不过来"6→4，玩家自定 2-30）；
        /// 楼/车各 ≤上限/3、人不限（人为主比例不变）；采样池自动跟随（上限×4 夹 8-120，§12 #55 单旋钮合并）。
        /// 要爽自己拉高，token 账自负。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        [SettingsUISlider(min = 2f, max = 30f, step = 1f, unit = "integer")]
        public int BubbleVisibleMax { get; set; } = 4;

        /// <summary>气泡驻留时长倍率（默认 1.5×，2026-09-11 玩家实机"更换太快"）：乘在阅读时长
        /// （4s+0.28s/字+抖动）和 30s 封顶上；换气拍的沉默时长同比例拉长。0.5-3× 滑杆。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        [SettingsUISlider(min = 50f, max = 300f, step = 25f, unit = "percentage", scalarMultiplier = 100f)]
        public float BubbleHoldScale { get; set; } = 1.5f;

        /// <summary>气泡与剧场节奏随游戏倍速（默认关=现实时间，§12 #68）：关=驻留/换句/轮播按现实秒走，
        /// 3x 下街上气泡不加速读得完；开=跟游戏时钟走（暂停冻结、倍速同速放大，与模拟节拍一致）。
        /// 注意：3x 下"气泡变化快"的大头是移动锚点 3x 速度离画导致提前轮换（物理真相），本开关只管文字节奏。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubblePaceFollowsGameSpeed { get; set; } = false;

        /// <summary>气泡底板开关（默认开）：文字下垫深色圆角底板。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubblePlate { get; set; } = true;

        /// <summary>建造工具激活时自动隐藏气泡（默认开，§12 #46；推土机也算工具激活，一并隐藏）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubbleAutoHideBuildTool { get; set; } = true;

        /// <summary>拍照模式下自动隐藏气泡（默认开，§12 #46）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubbleAutoHidePhotoMode { get; set; } = true;

        /// <summary>气泡 AI 闲聊生成（默认开，§12 #49）：闲聊炉按自己的游戏时间节拍产气泡文案，
        /// 不随信息流节拍（feedMode）停——气泡不看面板也在显示，收面板不该让气泡退回占位话。关=气泡只用占位文案（零 token）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubbleChatterEnabled { get; set; } = true;

        /// <summary>气泡小剧场（默认开，§12 #49）：多人场景（车站/商店/公园/住宅）组剧本轮播对话。关=不成组不开炉（零 token）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubbleTheaterEnabled { get; set; } = true;

        /// <summary>街头短剧最大人数（§12 #67，默认 3，滑杆 2-5）：街头闲谈剧场抓人上限——现场人不够就小演
        /// （不硬凑）。设太高：人走散/出镜头导致演不完会浪费 token，量力而行。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        [SettingsUISlider(min = 2f, max = 5f, step = 1f, unit = "integer")]
        public int StreetTheaterMaxCast { get; set; } = 3;

        /// <summary>街头短剧最长句数（§12 #67，默认 6，滑杆 2-12）：街头闲谈剧场播放句数上限——剧本句数不足
        /// 就短演（不硬凑）。设太高演不完同样浪费 token。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        [SettingsUISlider(min = 2f, max = 12f, step = 1f, unit = "integer")]
        public int StreetTheaterMaxLines { get; set; } = 6;

        // —— 信息流组（四键自 settings.json 迁移，Content.ModSettings 同名字段改透传本实例） ——

        /// <summary>信息流生成节拍（默认 Always 常开）。</summary>
        [SettingsUISection(kTab, kGroupFeed)]
        public FeedModeOption FeedMode { get; set; } = FeedModeOption.Always;

        /// <summary>T0 模板兜底（默认关）：LLM 帖池断粮时是否用模板帖填充。本 mod 本质是 AI mod，断粮默认静默。</summary>
        [SettingsUISection(kTab, kGroupFeed)]
        public bool T0Fallback { get; set; } = false;

        /// <summary>写回强度档（默认 Normal）。</summary>
        [SettingsUISection(kTab, kGroupFeed)]
        public WriteBackTierOption WriteBackTier { get; set; } = WriteBackTierOption.Normal;

        /// <summary>信息流仓库上限（默认 100；超 500 全量推送亏性能，钳 20-500）。</summary>
        [SettingsUISection(kTab, kGroupFeed)]
        [SettingsUISlider(min = 20f, max = 500f, step = 10f, unit = "integer")]
        public int FeedMaxItems { get; set; } = 100;

        // —— LLM 供给组（§12 #59 快慢双轨：慢轨要质量 thinking 默认开，快轨量大句短 thinking 默认关） ——
        // 文本输入控件已经 ilspy 实锤（string 读写属性 + [SettingsUITextInput] → StringInputField）。
        // 密钥明文存 .coc，与 llm.json 同安全级（仅本地 ModsSettings 目录），说明里已注明。

        /// <summary>慢轨供给预设（默认 KimiCli 本机 CLI 订阅轨）。慢轨服务主炉帖子/话题炉/剧场剧本/续热/市长/广告（要质量）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        public LlmProviderOption LlmSlowProvider { get; set; } = LlmProviderOption.KimiCli;

        /// <summary>慢轨模型名（留空=预设推荐：DeepSeek→deepseek-chat，硅基流动→deepseek-ai/DeepSeek-R1；自定义必填）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        [SettingsUITextInput]
        public string LlmSlowModel { get; set; } = "";

        /// <summary>慢轨自定义 baseUrl（仅预设=CustomOpenAi 时生效，如 https://api.siliconflow.cn/v1）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        [SettingsUITextInput]
        public string LlmSlowBaseUrl { get; set; } = "";

        /// <summary>慢轨 API 密钥（KimiCli 预设不需要）。仅本地保存（与 llm.json 同级，明文在本机 ModsSettings 目录）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        [SettingsUITextInput]
        public string LlmSlowApiKey { get; set; } = "";

        /// <summary>慢轨 thinking 档位（默认 High 高）。Off=请求体带 thinking.disabled（支持的厂商生效），
        /// 省 token 大头但降质量；High=显式 effort=high（定标口径见 <see cref="ThinkingTierOption"/>）。
        /// 2026-09-21 由 bool 开关换 enum 档位（同名换型：旧 .coc 值反序列化失败回落默认=与旧默认同语义）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        public ThinkingTierOption LlmSlowThinking { get; set; } = ThinkingTierOption.High;

        /// <summary>快轨供给预设（默认 SameAsSlow 同慢轨一家）。快轨服务气泡闲聊炉（量大句短，~5-10s 要的是省 token）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        public LlmFastProviderOption LlmFastProvider { get; set; } = LlmFastProviderOption.SameAsSlow;

        /// <summary>快轨模型名（留空=同慢轨模型/预设推荐；可另填快模型分叉）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        [SettingsUITextInput]
        public string LlmFastModel { get; set; } = "";

        /// <summary>快轨自定义 baseUrl（仅预设=CustomOpenAi 时生效）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        [SettingsUITextInput]
        public string LlmFastBaseUrl { get; set; } = "";

        /// <summary>快轨 API 密钥（仅当快轨预设为 DeepSeek/硅基流动/自定义时生效；SameAsSlow 复用慢轨密钥）。仅本地保存。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        [SettingsUITextInput]
        public string LlmFastApiKey { get; set; } = "";

        /// <summary>快轨 thinking 档位（默认 Off——快轨收益主在省 token，思考链对短句是浪费；§12 #59）。
        /// 2026-09-21 由 bool 开关换 enum 档位（同名换型，同 <see cref="LlmSlowThinking"/> 注记）。</summary>
        [SettingsUISection(kTab, kGroupLlm)]
        public ThinkingTierOption LlmFastThinking { get; set; } = ThinkingTierOption.Off;

        /// <summary>llm.json 迁移种子已消费标记（隐藏字段不进 UI，只随 .coc 持久化）。
        /// false → Mod.OnLoad 读一次 llm.json 写进上面的字段并置 true（此后 llm.json 不再读取，设置页为唯一主源）。</summary>
        [SettingsUIHidden]
        public bool LlmSeededFromJson { get; set; } = false;

        /// <summary>设置变更回调（UI 每次提交都走 ApplyAndSave→Apply，AutomaticSettings 实锤）：
        /// 转调 Mod.OnOptionsApplied()，由 Mod 比对供给签名热重建网关（§12 #59 热切换不重启）。
        /// 注意本方法对任何字段（含气泡滑杆拖动）都会触发，签名 diff 是廉价的字符串比较。</summary>
        public override void Apply()
        {
            base.Apply();
            Mod.OnOptionsApplied();
        }

        /// <summary>恢复默认（游戏设置页"重置"入口调用；与属性初始化器保持同一份默认值）。</summary>
        public override void SetDefaults()
        {
            BubbleEnabled = true;
            BubbleDistHuman = 1f;
            BubbleDistCar = 1f;
            BubbleDistBuilding = 1f;
            BubbleVisibleMax = 4; // 2026-09-11 玩家实机"有些阅读不过来"：稀疏默认 6→4
            BubbleHoldScale = 1.5f;
            BubblePaceFollowsGameSpeed = false; // §12 #68：默认现实时间（墙钟），3x 下气泡不加速
            BubblePlate = true;
            BubbleAutoHideBuildTool = true;
            BubbleAutoHidePhotoMode = true;
            BubbleChatterEnabled = true;
            BubbleTheaterEnabled = true;
            StreetTheaterMaxCast = 3;  // §12 #67：街头短剧默认 3 人
            StreetTheaterMaxLines = 6; // §12 #67：街头短剧默认 6 句
            FeedMode = FeedModeOption.Always;
            T0Fallback = false;
            WriteBackTier = WriteBackTierOption.Normal;
            FeedMaxItems = 100;
            LlmSlowProvider = LlmProviderOption.KimiCli;
            LlmSlowModel = "";
            LlmSlowBaseUrl = "";
            LlmSlowApiKey = "";
            LlmSlowThinking = ThinkingTierOption.High;
            LlmFastProvider = LlmFastProviderOption.SameAsSlow;
            LlmFastModel = "";
            LlmFastBaseUrl = "";
            LlmFastApiKey = "";
            LlmFastThinking = ThinkingTierOption.Off;
        }
    }
}
