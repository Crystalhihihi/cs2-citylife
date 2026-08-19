using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// T0 模板引擎：零 token 内容层。模板 = 固定文本 + 插槽。
    /// 文案纪律（2026-08-19 玩家反馈修订）：**真人不念精确数字**——统计值一律模糊化
    /// （3543→"3千多"、27.4%→"两成多"），插槽带 _h 后缀的是"人话版"。
    /// 如何扩展（社区贡献点）：
    /// 1. 加句子：往 s_Templates 对应话题的数组里加一条即可；
    /// 2. 加话题：Topic 枚举加值 + 这里补模板和作者 + ContentDirector 补阈值；
    /// 3. 加插槽：CitySnapshot 加字段 + Fill 加一行 Replace（精确值）+ 按需加人话插槽。
    /// 情绪纪律（设计文档 §6）：吐槽对事不对人；情绪跟着真实指标走，不为毒舌而毒舌。
    /// </summary>
    public static class TemplateEngine
    {
        private static readonly Dictionary<Topic, string[]> s_Templates = new()
        {
            [Topic.Daily] = new[]
            {
                "今天也是普普通通的一天，挺好。",
                "下班回家！{citizens_h}人的城市，晚高峰各回各家。",
                "住这儿也有阵子了，{citizens_h}人，说大不大说小不小。",
            },
            [Topic.Rain] = new[]
            {
                "又下雨了，出门记得带伞，路滑慢点开。",
                "这雨下得，刚晾出去的衣服又白搭了。",
            },
            [Topic.Snow] = new[]
            {
                "下雪了！一觉醒来全城变白，明早堆雪人约不约？",
                "初雪！环卫工人要辛苦了，大家出行注意安全。",
            },
            [Topic.HeatWave] = new[]
            {
                "今天{temperature}度？！空调外机都要罢工了。",
            },
            [Topic.HighUnemployment] = new[]
            {
                "工作也太难找了，投了八份简历没一个回的……",
                "又一个熟人被优化了。听说咱们这儿失业的{unemployment_h}了？愁人。",
            },
            [Topic.HighHappiness] = new[]
            {
                "最近感觉咱城市挺顺的，街坊见面都乐呵呵。",
                "不得不说，住这儿是真舒心，早上公园遛弯全是熟人。",
            },
            [Topic.LowHappiness] = new[]
            {
                "最近心里总有点烦，说不上来，就感觉城里哪儿不对劲。",
                "街坊最近火气都大，楼下又吵起来了。这日子咋了？",
            },
            [Topic.TouristBoom] = new[]
            {
                "最近街上游客是不是变多了？买个早点都得排队。",
                "景点附近全是举相机的，本地人快没地儿站了。",
            },
        };

        private static readonly Dictionary<Topic, string> s_Authors = new()
        {
            [Topic.Daily] = "街坊老周",
            [Topic.Rain] = "看天的老王",
            [Topic.Snow] = "看天的老王",
            [Topic.HeatWave] = "怕热的胖哥",
            [Topic.HighUnemployment] = "找工作的阿强",
            [Topic.HighHappiness] = "热心网友小赵",
            [Topic.LowHappiness] = "热心网友小赵",
            [Topic.TouristBoom] = "本地向导小李",
        };

        /// <summary>按种子从话题模板池取一条并填充插槽。种子由调用方递增，保证连续两条不同。</summary>
        public static Post Compose(in CitySnapshot s, Topic topic, uint seed)
        {
            var templates = s_Templates[topic];
            var text = Fill(templates[seed % templates.Length], s);
            return new Post(s_Authors[topic], text, topic, "t0");
        }

        private static string Fill(string t, in CitySnapshot s)
        {
            return t
                // 人话插槽（默认用这些）
                .Replace("{citizens_h}", FuzzPeople(s.Citizens))
                .Replace("{tourists_h}", FuzzPeople(s.Tourists))
                .Replace("{unemployment_h}", FuzzPercent(s.UnemploymentPercent))
                // 精确插槽（留给确有需要的新模板，慎用——真人不念精确数字）
                .Replace("{citizens}", s.Citizens.ToString())
                .Replace("{households}", s.Households.ToString())
                .Replace("{tourists}", s.Tourists.ToString())
                .Replace("{happiness}", s.Happiness.ToString("F0"))
                .Replace("{unemployment}", s.UnemploymentPercent.ToString("F1"))
                .Replace("{temperature}", s.Temperature.ToString("F0"))
                .Replace("{season}", string.IsNullOrEmpty(s.SeasonName) ? "这个季节" : s.SeasonName);
        }

        /// <summary>人数模糊化：3543→"3千多"、28600→"2万多"、217843→"21来万"。</summary>
        private static string FuzzPeople(int n)
        {
            if (n < 1000) return n.ToString();
            if (n < 9500) return $"{n / 1000}千多";
            if (n < 10000) return "快一万";
            if (n < 95000) return $"{n / 10000}万多";
            if (n < 100000) return "快十万";
            return $"{n / 10000}来万";
        }

        /// <summary>百分比模糊化：27.4→"两成多"、55→"一半多"。</summary>
        private static string FuzzPercent(float p)
        {
            if (p < 5f) return "不到半成";
            if (p < 10f) return "不到一成";
            if (p < 20f) return "一成多";
            if (p < 30f) return "两成多";
            if (p < 40f) return "三成多";
            if (p < 50f) return "快一半";
            if (p < 60f) return "一半多";
            if (p < 75f) return "六成往上";
            return "大半";
        }
    }
}
