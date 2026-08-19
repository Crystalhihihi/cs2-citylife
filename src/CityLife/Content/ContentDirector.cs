namespace CityLife.Content
{
    /// <summary>
    /// 内容导演（纯逻辑）：话题检测的全部阈值知识都在这里——"什么算新闻"。
    /// 优先级：突发天气 > 异常指标（高失业/低幸福）> 正向指标（游客潮/高幸福）> 日常兜底；
    /// 同一话题不连发（调用方传入上一话题，重复则退回日常）。
    /// 阈值即情绪：治理好才夸、治理烂才骂（设计文档 §6 情绪配比原则的执行层落点）。
    /// </summary>
    public static class ContentDirector
    {
        public static Topic DetectTopic(in CitySnapshot s, Topic lastTopic)
        {
            var topic =
                s.IsSnowing ? Topic.Snow :
                s.IsRaining ? Topic.Rain :
                s.UnemploymentPercent >= 8f ? Topic.HighUnemployment :
                s.Happiness <= 40f ? Topic.LowHappiness :
                (s.Tourists >= 20 && s.Tourists >= s.Citizens / 20) ? Topic.TouristBoom :
                s.Happiness >= 70f ? Topic.HighHappiness :
                Topic.Daily;

            return topic == lastTopic ? Topic.Daily : topic;
        }
    }
}
