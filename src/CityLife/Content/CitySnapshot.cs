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
        public float UnemploymentRate;  // 原始值，量纲见 UnemploymentPercent 注释
        public float HomelessnessRate;
        public bool IsRaining;
        public bool IsSnowing;
        public float Temperature;
        public string? SeasonName;

        /// <summary>失业率百分数。原始值量纲未实测（可能 0-1 或 0-100），>1.5 视为已是百分数。</summary>
        public readonly float UnemploymentPercent => UnemploymentRate > 1.5f ? UnemploymentRate : UnemploymentRate * 100f;
    }
}
