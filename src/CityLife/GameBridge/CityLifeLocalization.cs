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

            { Opt(nameof(CityLifeSetting.BubbleEnabled)), "Dialogue bubbles" },
            { OptDesc(nameof(CityLifeSetting.BubbleEnabled)), "Master switch for street dialogue bubbles. Ctrl+9 is an in-session quick toggle AND-ed with this switch." },
            { Opt(nameof(CityLifeSetting.BubbleDistHuman)), "Bubble distance · pedestrians" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistHuman)), "Max visible distance multiplier for pedestrian bubbles (100% = base 125m)." },
            { Opt(nameof(CityLifeSetting.BubbleDistCar)), "Bubble distance · vehicles" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistCar)), "Max visible distance multiplier for vehicle bubbles (100% = base 320m)." },
            { Opt(nameof(CityLifeSetting.BubbleDistBuilding)), "Bubble distance · buildings" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistBuilding)), "Max visible distance multiplier for building bubbles (100% = base 800m)." },
            { Opt(nameof(CityLifeSetting.BubbleDensity)), "Bubble density" },
            { OptDesc(nameof(CityLifeSetting.BubbleDensity)), "Bubble sampling pool size: Low 30 / Medium 60 / High 120." },
            { Opt(nameof(CityLifeSetting.BubblePlate)), "Bubble background plate" },
            { OptDesc(nameof(CityLifeSetting.BubblePlate)), "Draw a dark rounded plate under bubble text (off = text with outline only)." },
            { Opt(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "Auto-hide in build tools" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "Hide all bubbles while a build tool is active (bulldozer counts as a tool too)." },
            { Opt(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "Auto-hide in photo mode" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "Hide all bubbles while photo mode is active." },

            { Opt(nameof(CityLifeSetting.FeedMode)), "Feed generation mode" },
            { OptDesc(nameof(CityLifeSetting.FeedMode)), "Always = the city lives on its own; OpenOnly = generation stops while the panel is closed (saves tokens); Throttled = reduced rate while closed." },
            { Opt(nameof(CityLifeSetting.T0Fallback)), "T0 template fallback" },
            { OptDesc(nameof(CityLifeSetting.T0Fallback)), "Fill the feed with template posts when LLM supply runs out. This is an AI mod at heart — default off means the feed stays silent." },
            { Opt(nameof(CityLifeSetting.WriteBackTier)), "Write-back intensity" },
            { OptDesc(nameof(CityLifeSetting.WriteBackTier)), "Scales crowd injection etc.: Mild ×0.5 / Normal ×1.0 / Crazy ×2.0 (may paralyze traffic; warned in-game)." },
            { Opt(nameof(CityLifeSetting.FeedMaxItems)), "Feed capacity" },
            { OptDesc(nameof(CityLifeSetting.FeedMaxItems)), "Max posts kept in the feed store (20-500; above 500 full pushes start to cost performance)." },

            { EnumVal("BUBBLEDENSITYLEVEL", "Low"), "Low" },
            { EnumVal("BUBBLEDENSITYLEVEL", "Medium"), "Medium" },
            { EnumVal("BUBBLEDENSITYLEVEL", "High"), "High" },
            { EnumVal("FEEDMODEOPTION", "Always"), "Always on" },
            { EnumVal("FEEDMODEOPTION", "OpenOnly"), "Only while open" },
            { EnumVal("FEEDMODEOPTION", "Throttled"), "Throttled" },
            { EnumVal("WRITEBACKTIEROPTION", "Mild"), "Mild" },
            { EnumVal("WRITEBACKTIEROPTION", "Normal"), "Normal" },
            { EnumVal("WRITEBACKTIEROPTION", "Crazy"), "Crazy" },
        };

        /// <summary>中文字典（同时注册 "zh-HANS" 与 "zh-CN" 保险）。</summary>
        public static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            { kSection, "CityLife 市民圈" },
            { kTab, "市民圈" },
            { kGroupBubble, "对话气泡" },
            { kGroupFeed, "信息流" },

            { Opt(nameof(CityLifeSetting.BubbleEnabled)), "对话气泡总开关" },
            { OptDesc(nameof(CityLifeSetting.BubbleEnabled)), "街头对话气泡的权威总闸；Ctrl+9 会话内快速开关与本开关为 AND 关系。" },
            { Opt(nameof(CityLifeSetting.BubbleDistHuman)), "气泡可见距离·行人" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistHuman)), "行人气泡最大可见距离倍率（100% = 基础 125m）。" },
            { Opt(nameof(CityLifeSetting.BubbleDistCar)), "气泡可见距离·车辆" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistCar)), "车辆气泡最大可见距离倍率（100% = 基础 320m）。" },
            { Opt(nameof(CityLifeSetting.BubbleDistBuilding)), "气泡可见距离·建筑" },
            { OptDesc(nameof(CityLifeSetting.BubbleDistBuilding)), "建筑气泡最大可见距离倍率（100% = 基础 800m）。" },
            { Opt(nameof(CityLifeSetting.BubbleDensity)), "气泡密度" },
            { OptDesc(nameof(CityLifeSetting.BubbleDensity)), "同屏气泡采样池大小：低 30 / 中 60 / 高 120。" },
            { Opt(nameof(CityLifeSetting.BubblePlate)), "气泡底板" },
            { OptDesc(nameof(CityLifeSetting.BubblePlate)), "在气泡文字下绘制深色圆角底板（关掉只剩文字+描边）。" },
            { Opt(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "建造工具激活时自动隐藏" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHideBuildTool)), "建造工具激活期间隐藏全部气泡（推土机也算工具激活，一并隐藏）。" },
            { Opt(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "拍照模式下自动隐藏" },
            { OptDesc(nameof(CityLifeSetting.BubbleAutoHidePhotoMode)), "拍照模式激活期间隐藏全部气泡。" },

            { Opt(nameof(CityLifeSetting.FeedMode)), "信息流生成节拍" },
            { OptDesc(nameof(CityLifeSetting.FeedMode)), "常开=城市自己在活着；仅展开=收起面板即停生成省 token；节流=收起时降频保温。" },
            { Opt(nameof(CityLifeSetting.T0Fallback)), "T0 模板兜底" },
            { OptDesc(nameof(CityLifeSetting.T0Fallback)), "LLM 断粮时用模板帖填充信息流。本 mod 本质是 AI mod，默认关闭即断粮静默。" },
            { Opt(nameof(CityLifeSetting.WriteBackTier)), "写回强度" },
            { OptDesc(nameof(CityLifeSetting.WriteBackTier)), "缩放人群注入等写回规模：体验 ×0.5 / 正常 ×1.0 / 疯狂 ×2.0（可能交通瘫痪，游戏内带警示）。" },
            { Opt(nameof(CityLifeSetting.FeedMaxItems)), "信息流容量" },
            { OptDesc(nameof(CityLifeSetting.FeedMaxItems)), "信息流仓库最多保留的帖子条数（20-500；超 500 全量推送开始亏性能）。" },

            { EnumVal("BUBBLEDENSITYLEVEL", "Low"), "低" },
            { EnumVal("BUBBLEDENSITYLEVEL", "Medium"), "中" },
            { EnumVal("BUBBLEDENSITYLEVEL", "High"), "高" },
            { EnumVal("FEEDMODEOPTION", "Always"), "常开" },
            { EnumVal("FEEDMODEOPTION", "OpenOnly"), "仅展开" },
            { EnumVal("FEEDMODEOPTION", "Throttled"), "节流" },
            { EnumVal("WRITEBACKTIEROPTION", "Mild"), "体验" },
            { EnumVal("WRITEBACKTIEROPTION", "Normal"), "正常" },
            { EnumVal("WRITEBACKTIEROPTION", "Crazy"), "疯狂" },
        };
    }
}
