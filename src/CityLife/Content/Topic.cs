namespace CityLife.Content
{
    /// <summary>
    /// 话题类型。M1 v1 只覆盖城市聚合指标可达的话题；突发事件类（事故/火灾/犯罪）
    /// 待 EventJournal 接入后扩展（设计文档 §4 M1 已验证其回调 API）。
    /// 如何扩展：加枚举值 → TemplateEngine 补模板与作者 → ContentDirector.DetectTopic 补阈值规则。
    /// </summary>
    public enum Topic
    {
        Daily,              // 日常闲聊（兜底）
        Rain,               // 下雨中
        Snow,               // 下雪中
        HeatWave,           // 高温（预留）
        HighUnemployment,   // 高失业
        HighHappiness,      // 高幸福
        LowHappiness,       // 低幸福
        TouristBoom,        // 游客潮
        Breaking,           // 突发事件（EventJournal 实体级话题；不进 DetectTopic/模板引擎，由 EventNewsSystem 直接成文）
        Petition,           // 民意沸腾（请愿热议串；不进 DetectTopic，由 PetitionSystem 写 LiveContext.PendingPetition 触发）
    }
}
