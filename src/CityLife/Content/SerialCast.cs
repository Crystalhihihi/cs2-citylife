using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// 连续剧角色（M5 v1：连续性="有看头"第三要素——认得出的人，追得动的故事）。
    /// 纯 Content 数据结构（版本免疫），状态由内容导演按炉推进。
    /// 一个角色 = 真实市民名 + 一张固定人格卡（声音可辨认）+ 一条故事线（分集推进，完结退休）。
    /// </summary>
    public sealed class SerialCharacter
    {
        public string Name = "";        // 真实市民名（CitizenPoolSystem 取样）
        public Persona Card = default!; // 固定人格卡（语域稳定=声音可辨认）
        public string Story = "";       // 故事线名（"找工作"）
        public string StoryHint = "";   // 处境提示（"正在找工作"）
        public int Episode;             // 已播集数
        public int MaxEpisodes = 5;     // 完结集数（4-7，抽签）
        public string? LastText;        // 上集正文（喂回 prompt 作前情）

        /// <summary>本季是否该收官（最后一集给"大结局"提示）。</summary>
        public bool IsFinaleNext => Episode == MaxEpisodes - 1;

        /// <summary>是否已完结（导演下一轮将其退休换新人）。</summary>
        public bool IsDone => Episode >= MaxEpisodes;
    }

    /// <summary>故事线池（起承转合由模型自由发挥，执行层只给"什么剧"）。</summary>
    public static class SerialStories
    {
        private static readonly (string Story, string Hint)[] k_All =
        {
            ("找工作", "正在找工作"),
            ("开店", "在筹备开家小店"),
            ("恋爱", "刚谈了对象"),
            ("搬家", "在找房准备搬家"),
            ("学车", "在考驾照"),
            ("备考", "在备考"),
            ("健身", "在减肥健身"),
            ("养娃", "娃刚上学"),
        };

        public static (string Story, string Hint) Pick(int seed) => k_All[seed % k_All.Length];
    }
}
