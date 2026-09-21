using System.Collections.Generic;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 设置页本地化字典（中英双语，§12 #3）。注册见 Mod.OnLoad：
    /// LocalizationManager.AddSource + MemorySource（"en-US" / "zh-HANS" / "zh-CN" 共用中文字典）。
    ///
    /// 键位规则（2026-09-09 对 Game.dll 反编译逐条实锤，AutomaticSettings/SettingPageData/OptionsUISystem）：
    /// - mod 标题：Options.SECTION[{modId}]，modId = 程序集名.命名空间.Mod类名 = CityLife.CityLife.Mod
    /// - tab：Options.TAB[{modId}.{tab名}]（addPrefix=true 时 tab 键带 modId 前缀）
    /// - group：Options.GROUP[{modId}.{group名}]
    /// - 选项名/悬停说明：Options.OPTION / Options.OPTION_DESCRIPTION[{modId}.{Setting类名}.{属性名}]
    /// - 枚举下拉项：Options.{modId}.{枚举类型名大写}[{枚举值名}]
    ///   （AutomaticSettings.GetEnumValues：前缀拼 "Options."+modId 后接 类型名.ToUpperInvariant()+"[值名]"）
    ///
    /// 如何扩展：加选项 = CityLifeSetting 加属性 + 这里两个字典各加同名键；加枚举值 = 两个字典
    /// 各加一条 Options.{modId}.{类型名大写}[新值名]。缺键时 UI 回落显示原始键/枚举原名，不炸。
    /// </summary>
    public static class CityLifeLocalization
    {
        private const string kId = "CityLife.CityLife.Mod";
        private const string kSection = "Options.SECTION[" + kId + "]";
        private const string kTab = "Options.TAB[" + kId + "." + CityLifeSetting.kTab + "]";
        private const string kGroupBubble = "Options.GROUP[" + kId + "." + CityLifeSetting.kGroupBubble + "]";
        private const string kGroupFeed = "Options.GROUP[" + kId + "." + CityLifeSetting.kGroupFeed + "]";
        private const string kGroupLlm = "Options.GROUP[" + kId + "." + CityLifeSetting.kGroupLlm + "]";

        /// <summary>拼选项键（name/description 共一条前缀规则）。</summary>
        private static string Opt(string prop) => "Options.OPTION[" + kId + ".CityLifeSetting." + prop + "]";
        private static string OptDesc(string prop) => "Options.OPTION_DESCRIPTION[" + kId + ".CityLifeSetting." + prop + "]";
        /// <summary>拼枚举值键（类型名大写，AutomaticSettings.GetEnumValues 实锤格式）。</summary>
        private static string EnumVal(string enumTypeUpper, string value) => "Options." + kId + "." + enumTypeUpper + "[" + value + "]";

        /// <summary>英文字典（注册 "en-US"）。</summary>
        public static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { kSection, "CityLife" },
            { kTab, "CityLife" },
            { kGroupBubble, "Bubble" },
            { kGroupFeed, "Feed" },
            { kGroupLlm, "LLM supply" },

            { Opt(nameof(CityLifeSetting.BubbleEnabled)), "Dialogue bubbles" },
            { OptDesc(nameof(CityLifeSetting.BubbleEnabled)), "Master switch for street dialogue bubbles. Ctrl+9 is an in-session quick toggle AND-ed with this switch." },
            { Opt(nameof(CityLifeSetting.BubbleDistHuman)), "Bubble distance · pedestrians" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistHuman)), "Max visible distance multiplier for pedestrian bubbles (100% = base 125m)." },
            { Opt(nameof(CityLifeSetting.BubbleDistCar)), "Bubble distance · vehicles" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistCar)), "Max visible distance multiplier for vehicle bubbles (100% = base 320m)." },
            { Opt(nameof(CityLifeSetting.BubbleDistBuilding)), "Bubble distance · buildings" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistBuilding)), "Max visible distance multiplier for building bubbles (100% = base 800m)." },
            { Opt(nameof(CityLifeSetting.BubbleVisibleMax)), "Max visible bubbles" },
            { OptDesc(nameof(CityLifeSetting.BubbleVisibleMax)), "Max bubbles on screen at once (2-30, default 6 — sparse by design; buildings/vehicles each capped at 1/3 of this). Raise it if you want a livelier street; token bill is yours." },
            { Opt(nameof(CityLifeSetting.BubbleHoldScale)), "Bubble linger time" },
            { OptDesc(nameof(CityLifeSetting.BubbleHoldScale)), "How long a bubble stays before rotating (multiplier on 4s + 0.28s per character; default 150%)." },
            { Opt(nameof(CityLifeSetting.BubblePaceFollowsGameSpeed)), "Bubble pace follows game speed" },
            { OptDesc(nameof(CityLifeSetting.BubblePaceFollowsGameSpeed)), "Off (default) = bubble linger and theater lines tick in real time, so 3x speed doesn't rush your reading. On = follow the game clock (frozen while paused, scaled with game speed). Note: at high speed the main reason bubbles churn is moving anchors leaving the screen — a physical truth this switch doesn't govern." },
            { Opt(nameof(CityLifeSetting.BubblePlate)), "Bubble background plate" },
            { OptDesc(nameof(CityLifeSetting.BubblePlate)), "Draw a dark rounded plate under bubble text (off = text with outline only)." },
            { Opt(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "Auto-hide in build tools" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "Hide all bubbles while a build tool is active (bulldozer counts as a tool too)." },
            { Opt(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "Auto-hide in photo mode" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "Hide all bubbles while photo mode is active." },
            { Opt(nameof(CityLifeSetting.BubbleChatterEnabled)), "Bubble AI chatter" },
            { OptDesc(nameof(CityLifeSetting.BubbleChatterEnabled)), "Chatter furnace feeds bubbles with AI lines on its own game-time cadence, independent of feed mode — bubbles are visible even with the panel closed. Off = placeholder lines only (zero tokens)." },
            { Opt(nameof(CityLifeSetting.BubbleTheaterEnabled)), "Bubble mini-theater" },
            { OptDesc(nameof(CityLifeSetting.BubbleTheaterEnabled)), "Group scripted dialogues at multi-person scenes (stops / shops / parks / homes). Off = no grouping, no furnace (zero tokens)." },
            { Opt(nameof(CityLifeSetting.StreetTheaterMaxCast)), "Street play · max cast" },
            { OptDesc(nameof(CityLifeSetting.StreetTheaterMaxCast)), "Max pedestrians grouped into a street chatter play (2-5, default 3). Short-handed scenes simply play smaller — no padding. Set it high and walkers dispersing or leaving the screen may cut the play short, wasting the tokens that wrote it — your call." },
            { Opt(nameof(CityLifeSetting.StreetTheaterMaxLines)), "Street play · max lines" },
            { OptDesc(nameof(CityLifeSetting.StreetTheaterMaxLines)), "Max lines played for a street chatter play (2-12, default 6). Shorter scripts play as-is. Overlong plays that never finish waste tokens all the same." },

            { Opt(nameof(CityLifeSetting.FeedMode)), "Feed generation mode" },
            { OptDesc(nameof(CityLifeSetting.FeedMode)), "Always = the city lives on its own; OpenOnly = generation stops while the panel is closed (saves tokens); Throttled = reduced rate while closed." },
            { Opt(nameof(CityLifeSetting.T0Fallback)), "T0 template fallback" },
            { OptDesc(nameof(CityLifeSetting.T0Fallback)), "Fill the feed with template posts when LLM supply runs out. This is an AI mod at heart — default off means the feed stays silent." },
            { Opt(nameof(CityLifeSetting.WriteBackTier)), "Write-back intensity" },
            { OptDesc(nameof(CityLifeSetting.WriteBackTier)), "Scales crowd injection etc.: Mild ×0.5 / Normal ×1.0 / Crazy ×2.0 (may paralyze traffic; warned in-game)." },
            { Opt(nameof(CityLifeSetting.FeedMaxItems)), "Feed capacity" },
            { OptDesc(nameof(CityLifeSetting.FeedMaxItems)), "Max posts kept in the feed store (20-500; above 500 full pushes start to cost performance)." },

            { Opt(nameof(CityLifeSetting.LlmSlowProvider)), "Slow track · provider" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowProvider)), "Slow track serves posts/topics/mini-theater/mayor/ads (quality first, deep thinking on by default). KimiCli = local Kimi Code CLI subscription (no key needed); DeepSeek / SiliconFlow = official endpoints (baseUrl built in); Custom = any OpenAI-compatible endpoint. Changes hot-swap the gateway without restart." },
            { Opt(nameof(CityLifeSetting.LlmSlowModel)), "Slow track · model" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowModel)), "Empty = preset recommendation (DeepSeek → deepseek-chat, SiliconFlow → deepseek-ai/DeepSeek-R1). Required for Custom." },
            { Opt(nameof(CityLifeSetting.LlmSlowBaseUrl)), "Slow track · custom baseUrl" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowBaseUrl)), "Only used when provider = Custom OpenAI-compatible, e.g. https://api.siliconflow.cn/v1 (no trailing slash needed)." },
            { Opt(nameof(CityLifeSetting.LlmSlowApiKey)), "Slow track · API key" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowApiKey)), "Not needed for KimiCli. Stored locally only (plain text in this machine's ModsSettings folder, same level as llm.json)." },
            { Opt(nameof(CityLifeSetting.LlmSlowThinking)), "Slow track · thinking tier" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowThinking)), "Default High. Off = requests carry thinking.disabled (where the vendor supports it) — saves the bulk of tokens at some quality cost; High = explicit effort=high (vendor default tier is not high; measured)." },
            { Opt(nameof(CityLifeSetting.LlmFastProvider)), "Fast track · provider" },
            { OptDesc(nameof(CityLifeSetting.LlmFastProvider)), "Fast track serves bubble chatter (short lines, high volume, ~5-10s; thinking off by default to save tokens). SameAsSlow (default) = reuse the slow track's endpoint and key, with its own model/thinking below. Quick-response providers (e.g. SiliconFlow) make good fast-track candidates." },
            { Opt(nameof(CityLifeSetting.LlmFastModel)), "Fast track · model" },
            { OptDesc(nameof(CityLifeSetting.LlmFastModel)), "Empty = same model as the slow track (or the preset recommendation). Fill in to fork a faster model." },
            { Opt(nameof(CityLifeSetting.LlmFastBaseUrl)), "Fast track · custom baseUrl" },
            { OptDesc(nameof(CityLifeSetting.LlmFastBaseUrl)), "Only used when fast-track provider = Custom OpenAI-compatible." },
            { Opt(nameof(CityLifeSetting.LlmFastApiKey)), "Fast track · API key" },
            { OptDesc(nameof(CityLifeSetting.LlmFastApiKey)), "Only used when the fast track has its own DeepSeek/SiliconFlow/Custom provider (SameAsSlow reuses the slow key). Stored locally only (plain text in this machine's ModsSettings folder, same level as llm.json)." },
            { Opt(nameof(CityLifeSetting.LlmFastThinking)), "Fast track · thinking tier" },
            { OptDesc(nameof(CityLifeSetting.LlmFastThinking)), "Default Off — the fast track's payoff is saved tokens; chains of thought are wasted on one-liners. High = slow-track quality at slow-track cost." },

            { EnumVal("FEEDMODEOPTION", "Always"), "Always on" },
            { EnumVal("FEEDMODEOPTION", "OpenOnly"), "Only while open" },
            { EnumVal("FEEDMODEOPTION", "Throttled"), "Throttled" },
            { EnumVal("WRITEBACKTIEROPTION", "Mild"), "Mild" },
            { EnumVal("WRITEBACKTIEROPTION", "Normal"), "Normal" },
            { EnumVal("WRITEBACKTIEROPTION", "Crazy"), "Crazy" },
            { EnumVal("LLMPROVIDEROPTION", "KimiCli"), "Kimi Code CLI (local subscription)" },
            { EnumVal("LLMPROVIDEROPTION", "DeepSeek"), "DeepSeek" },
            { EnumVal("LLMPROVIDEROPTION", "SiliconFlow"), "SiliconFlow" },
            { EnumVal("LLMPROVIDEROPTION", "CustomOpenAi"), "Custom (OpenAI-compatible)" },
            { EnumVal("LLMFASTPROVIDEROPTION", "SameAsSlow"), "Same as slow track" },
            { EnumVal("LLMFASTPROVIDEROPTION", "KimiCli"), "Kimi Code CLI (local subscription)" },
            { EnumVal("LLMFASTPROVIDEROPTION", "DeepSeek"), "DeepSeek" },
            { EnumVal("LLMFASTPROVIDEROPTION", "SiliconFlow"), "SiliconFlow" },
            { EnumVal("LLMFASTPROVIDEROPTION", "CustomOpenAi"), "Custom (OpenAI-compatible)" },
            { EnumVal("THINKINGTIEROPTION", "Off"), "Off" },
            { EnumVal("THINKINGTIEROPTION", "High"), "High" },
        };

        /// <summary>中文字典（同时注册 "zh-HANS" 与 "zh-CN" 保险）。</summary>
        public static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            { kSection, "CityLife 市民圈" },
            { kTab, "市民圈" },
            { kGroupBubble, "对话气泡" },
            { kGroupFeed, "信息流" },
            { kGroupLlm, "LLM 供给" },

            { Opt(nameof(CityLifeSetting.BubbleEnabled)), "对话气泡总开关" },
            { OptDesc(nameof(CityLifeSetting.BubbleEnabled)), "街头对话气泡的权威总闸；Ctrl+9 会话内快速开关与本开关为 AND 关系。" },
            { Opt(nameof(CityLifeSetting.BubbleDistHuman)), "气泡可见距离·行人" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistHuman)), "行人气泡最大可见距离倍率（100% = 基础 125m）。" },
            { Opt(nameof(CityLifeSetting.BubbleDistCar)), "气泡可见距离·车辆" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistCar)), "车辆气泡最大可见距离倍率（100% = 基础 320m）。" },
            { Opt(nameof(CityLifeSetting.BubbleDistBuilding)), "气泡可见距离·建筑" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistBuilding)), "建筑气泡最大可见距离倍率（100% = 基础 800m）。" },
            { Opt(nameof(CityLifeSetting.BubbleVisibleMax)), "同屏气泡上限" },
            { OptDesc(nameof(CityLifeSetting.BubbleVisibleMax)), "同屏最多同时显示的气泡数（2-30，默认 6——稀疏是设计；楼/车各占上限的 1/3）。想热闹自己拉高，token 账单自负。" },
            { Opt(nameof(CityLifeSetting.BubbleHoldScale)), "气泡驻留时长" },
            { OptDesc(nameof(CityLifeSetting.BubbleHoldScale)), "一句话停留多久才换下一句（阅读时长 4 秒+每字 0.28 秒的倍率，默认 150%；换气沉默拍同比例拉长）。" },
            { Opt(nameof(CityLifeSetting.BubblePaceFollowsGameSpeed)), "气泡与剧场节奏随游戏倍速" },
            { OptDesc(nameof(CityLifeSetting.BubblePaceFollowsGameSpeed)), "关（默认）=气泡驻留/剧场换句按现实时间走，3 倍速下不催你阅读；开=跟游戏时钟走（暂停冻结、倍速同速放大）。注意：高速下「气泡变化快」的大头是移动市民离开镜头导致提前轮换（物理真相），本开关只管文字节奏。" },
            { Opt(nameof(CityLifeSetting.BubblePlate)), "气泡底板" },
            { OptDesc(nameof(CityLifeSetting.BubblePlate)), "在气泡文字下绘制深色圆角底板（关掉只剩文字+描边）。" },
            { Opt(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "建造工具激活时自动隐藏" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "建造工具激活期间隐藏全部气泡（推土机也算工具激活，一并隐藏）。" },
            { Opt(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "拍照模式下自动隐藏" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "拍照模式激活期间隐藏全部气泡。" },
            { Opt(nameof(CityLifeSetting.BubbleChatterEnabled)), "气泡 AI 闲聊" },
            { OptDesc(nameof(CityLifeSetting.BubbleChatterEnabled)), "闲聊炉按自己的游戏时间节拍给气泡供文案，不随信息流节拍停——气泡不看面板也在显示。关=只用占位文案（零 token）。" },
            { Opt(nameof(CityLifeSetting.BubbleTheaterEnabled)), "气泡小剧场" },
            { OptDesc(nameof(CityLifeSetting.BubbleTheaterEnabled)), "多人场景（车站/商店/公园/住宅）组剧本轮播对话。关=不成组不开炉（零 token）。" },
            { Opt(nameof(CityLifeSetting.StreetTheaterMaxCast)), "街头短剧·最大人数" },
            { OptDesc(nameof(CityLifeSetting.StreetTheaterMaxCast)), "街头闲谈短剧最多抓几个路人同演（2-5，默认 3）。现场人不够就小演，不硬凑。设太高：人走散/出镜头导致演不完，写剧本的 token 就白烧了，量力而行。" },
            { Opt(nameof(CityLifeSetting.StreetTheaterMaxLines)), "街头短剧·最长句数" },
            { OptDesc(nameof(CityLifeSetting.StreetTheaterMaxLines)), "街头闲谈短剧最多播几句（2-12，默认 6）。剧本不够长就短演，不硬凑；设太高演不完同样浪费 token。" },

            { Opt(nameof(CityLifeSetting.FeedMode)), "信息流生成节拍" },
            { OptDesc(nameof(CityLifeSetting.FeedMode)), "常开=城市自己在活着；仅展开=收起面板即停生成省 token；节流=收起时降频保温。" },
            { Opt(nameof(CityLifeSetting.T0Fallback)), "T0 模板兜底" },
            { OptDesc(nameof(CityLifeSetting.T0Fallback)), "LLM 断粮时用模板帖填充信息流。本 mod 本质是 AI mod，默认关闭即断粮静默。" },
            { Opt(nameof(CityLifeSetting.WriteBackTier)), "写回强度" },
            { OptDesc(nameof(CityLifeSetting.WriteBackTier)), "缩放人群注入等写回规模：体验 ×0.5 / 正常 ×1.0 / 疯狂 ×2.0（可能交通瘫痪，游戏内带警示）。" },
            { Opt(nameof(CityLifeSetting.FeedMaxItems)), "信息流容量" },
            { OptDesc(nameof(CityLifeSetting.FeedMaxItems)), "信息流仓库最多保留的帖子条数（20-500；超 500 全量推送开始亏性能）。" },

            { Opt(nameof(CityLifeSetting.LlmSlowProvider)), "慢轨·供给预设" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowProvider)), "慢轨服务主炉帖子/话题炉/剧场剧本/续热/市长/广告（要质量，深度思考默认开）。KimiCli=本机 Kimi Code CLI 订阅轨（无需密钥）；DeepSeek/硅基流动=官方端点（baseUrl 内置，只需密钥+模型）；自定义=任意 OpenAI 兼容端点。改动即时热切换网关，无需重启。" },
            { Opt(nameof(CityLifeSetting.LlmSlowModel)), "慢轨·模型" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowModel)), "留空=预设推荐（DeepSeek→deepseek-chat，硅基流动→deepseek-ai/DeepSeek-R1）；自定义端点必填。" },
            { Opt(nameof(CityLifeSetting.LlmSlowBaseUrl)), "慢轨·自定义 baseUrl" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowBaseUrl)), "仅当预设=自定义 OpenAI 兼容时生效，如 https://api.siliconflow.cn/v1（末尾斜杠有无均可）。" },
            { Opt(nameof(CityLifeSetting.LlmSlowApiKey)), "慢轨·API 密钥" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowApiKey)), "KimiCli 预设不需要。仅本地保存（与 llm.json 同级，明文存放于本机 ModsSettings 目录）。" },
            { Opt(nameof(CityLifeSetting.LlmSlowThinking)), "慢轨·思考档位" },
            { OptDesc(nameof(CityLifeSetting.LlmSlowThinking)), "默认高。关=请求体带 thinking.disabled（支持的厂商生效），省 token 大头但降质量；高=显式 effort=high（厂商默认档位非 high，实锤定标）。" },
            { Opt(nameof(CityLifeSetting.LlmFastProvider)), "快轨·供给预设" },
            { OptDesc(nameof(CityLifeSetting.LlmFastProvider)), "快轨服务气泡闲聊炉（量大句短，~5-10 秒；思考链对短句是浪费，默认关）。同慢轨（默认）=复用慢轨的端点与密钥，模型/思考开关可单独分叉。硅基流动等快响应供给适合当快轨试验田。" },
            { Opt(nameof(CityLifeSetting.LlmFastModel)), "快轨·模型" },
            { OptDesc(nameof(CityLifeSetting.LlmFastModel)), "留空=同慢轨模型（或预设推荐）；另填可分叉出更快的模型。" },
            { Opt(nameof(CityLifeSetting.LlmFastBaseUrl)), "快轨·自定义 baseUrl" },
            { OptDesc(nameof(CityLifeSetting.LlmFastBaseUrl)), "仅当快轨预设=自定义 OpenAI 兼容时生效。" },
            { Opt(nameof(CityLifeSetting.LlmFastApiKey)), "快轨·API 密钥" },
            { OptDesc(nameof(CityLifeSetting.LlmFastApiKey)), "仅当快轨单设 DeepSeek/硅基流动/自定义时生效（同慢轨复用慢轨密钥）。仅本地保存（与 llm.json 同级，明文存放于本机 ModsSettings 目录）。" },
            { Opt(nameof(CityLifeSetting.LlmFastThinking)), "快轨·思考档位" },
            { OptDesc(nameof(CityLifeSetting.LlmFastThinking)), "默认关——快轨收益主在省 token。高=与慢轨同质量，但更慢更贵。" },

            { EnumVal("FEEDMODEOPTION", "Always"), "常开" },
            { EnumVal("FEEDMODEOPTION", "OpenOnly"), "仅展开" },
            { EnumVal("FEEDMODEOPTION", "Throttled"), "节流" },
            { EnumVal("WRITEBACKTIEROPTION", "Mild"), "体验" },
            { EnumVal("WRITEBACKTIEROPTION", "Normal"), "正常" },
            { EnumVal("WRITEBACKTIEROPTION", "Crazy"), "疯狂" },
            { EnumVal("LLMPROVIDEROPTION", "KimiCli"), "Kimi Code CLI（本机订阅）" },
            { EnumVal("LLMPROVIDEROPTION", "DeepSeek"), "DeepSeek" },
            { EnumVal("LLMPROVIDEROPTION", "SiliconFlow"), "硅基流动" },
            { EnumVal("LLMPROVIDEROPTION", "CustomOpenAi"), "自定义（OpenAI 兼容）" },
            { EnumVal("LLMFASTPROVIDEROPTION", "SameAsSlow"), "同慢轨" },
            { EnumVal("LLMFASTPROVIDEROPTION", "KimiCli"), "Kimi Code CLI（本机订阅）" },
            { EnumVal("LLMFASTPROVIDEROPTION", "DeepSeek"), "DeepSeek" },
            { EnumVal("LLMFASTPROVIDEROPTION", "SiliconFlow"), "硅基流动" },
            { EnumVal("LLMFASTPROVIDEROPTION", "CustomOpenAi"), "自定义（OpenAI 兼容）" },
            { EnumVal("THINKINGTIEROPTION", "Off"), "关" },
            { EnumVal("THINKINGTIEROPTION", "High"), "高" },
        };
    }
}
