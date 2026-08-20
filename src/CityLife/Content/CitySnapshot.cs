namespace CityLife.Content
{
    /// <summary>
    /// 城市状态快照：GameBridge 每 N 帧从游戏系统蒸馏一次，喂给话题检测与（M1 后期的）LLM prompt。
    /// 纯数据结构，不引用任何游戏/Unity 类型——Content 命名空间全程版本免疫（架构铁律 4）。
    /// 如何扩展：加字段（如"垃圾堆积指数"）→ 同步改 GameBridge.TopicRadarSystem 的采样与 TemplateEngine.Fill 的插槽。
    /// </summary>
    public struct CitySnapshot
    {
        public int Citizens;
        public int Households;
        public int Tourists;
        public float Happiness;         // 平均幸福度（0-100 量级）
        public float UnemploymentRate;  // 原始值（量纲不可靠：低失业率城市会被启发式误判，仅留档对照）
        public float HomelessnessRate;  // 原始值（同）
        public float UnemploymentPercent;   // 失业率%（TopicRadarSystem 用 WorkableCitizenCount/CityWorkerCount 计数直算，量纲实锤）
        public float HomelessPercent;       // 无家可归率%（HomelessCitizenCount/Citizens 计数直算）
        public bool IsRaining;
        public bool IsSnowing;
        public float Temperature;
        public string? SeasonName;
        public int HourOfDay;           // 游戏时刻 0-23（昼夜节律：深夜帖少且内容不同）
    }
}
