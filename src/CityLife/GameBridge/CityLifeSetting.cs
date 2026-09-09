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
    [SettingsUIGroupOrder(kGroupBubble, kGroupFeed)]
    [SettingsUIShowGroupName(kGroupBubble, kGroupFeed)] // 组名"对话气泡/信息流"显示为分组标题（无此标注 UI 不显示组名）
    public class CityLifeSetting : ModSetting
    {
        /// <summary>设置页 tab 名（全部选项收在一个 tab）。</summary>
        public const string kTab = "CityLife";
        /// <summary>气泡组名。</summary>
        public const string kGroupBubble = "Bubble";
        /// <summary>信息流组名。</summary>
        public const string kGroupFeed = "Feed";

        /// <summary>气泡密度档（对应采样池 30/60/120，见 BubbleWorldSpikeSystem.LevelCount）。</summary>
        public enum BubbleDensityLevel { Low, Medium, High }

        /// <summary>信息流生成节拍与面板开合的联动（§12 #45 自 settings.json 迁移）：
        /// Always 常开（城市自己在活着）/ OpenOnly 仅展开（收起即停生成，省 token）/ Throttled 节流（收起降频保温）。</summary>
        public enum FeedModeOption { Always, OpenOnly, Throttled }

        /// <summary>写回强度档（§6 红线）：Mild 体验档 ×0.5 / Normal 正常档 ×1.0 / Crazy 疯狂档 ×2.0（带警示）。</summary>
        public enum WriteBackTierOption { Mild, Normal, Crazy }

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

        /// <summary>气泡密度档（默认 High = 采样池 120）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public BubbleDensityLevel BubbleDensity { get; set; } = BubbleDensityLevel.High;

        /// <summary>气泡底板开关（默认开）：文字下垫深色圆角底板。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubblePlate { get; set; } = true;

        /// <summary>建造工具激活时自动隐藏气泡（默认开，§12 #46；推土机也算工具激活，一并隐藏）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubbleAutoHideBuildTool { get; set; } = true;

        /// <summary>拍照模式下自动隐藏气泡（默认开，§12 #46）。</summary>
        [SettingsUISection(kTab, kGroupBubble)]
        public bool BubbleAutoHidePhotoMode { get; set; } = true;

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

        /// <summary>恢复默认（游戏设置页"重置"入口调用；与属性初始化器保持同一份默认值）。</summary>
        public override void SetDefaults()
        {
            BubbleEnabled = true;
            BubbleDistHuman = 1f;
            BubbleDistCar = 1f;
            BubbleDistBuilding = 1f;
            BubbleDensity = BubbleDensityLevel.High;
            BubblePlate = true;
            BubbleAutoHideBuildTool = true;
            BubbleAutoHidePhotoMode = true;
            FeedMode = FeedModeOption.Always;
            T0Fallback = false;
            WriteBackTier = WriteBackTierOption.Normal;
            FeedMaxItems = 100;
        }
    }
}
