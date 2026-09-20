using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// 闲聊炉写法规格签（§12 #69 句型/情绪 + §12 #72 活人感专项第一批重构 2026-09-20）：
    /// 多样性不靠 LLM 自觉——每张处境卡尾缀系统分配规格 <c>｜写：句型｜情：情绪｜口：口吻</c>，
    /// LLM 只服从不参与分配（单向阀门）。
    /// 句型签 6→3（#72：短≤12/中 13-25/长 26-40——带数字/物件/因果强制签退役=写文腔通道，
    /// 具体感要求留闲聊头【语域】，不归签）；情绪签三套照旧（#70）；口吻结构签 12 词转正
    /// （离线三臂实验 8 词胜者+扩编：复读/反问连发/拟声插入/自嘲——#72 定稿）。
    /// 性格签（按人锚）：市民实体 Index 哈希粘滞 6 性格，<b>不上卡面</b>——只做口吻子集/权重选择器
    /// （碎嘴→复读/反问连发/碎嘴短句高权；闷葫芦→省略号高权…），同一人永远同一口吻倾向。
    /// 确定性：句型/情绪组合=炉计数+卡序锚定+同炉同签顺延去重；口吻=性格权重表内按签序加权取。
    /// 共享纪律：BubbleChatterSystem 组卡与 tools/PromptEval 卡具同用这一处定义（一处定义别复制粘贴）。
    /// 如何扩展：加情绪=对应情绪套加词；加口吻=Voices 加词+六张性格权重表各加一列；
    /// 加性格=Personalities 加词+k_VoiceWeights 加一行+"如何调"注释更新。
    /// </summary>
    public static class ChatterSpec
    {
        /// <summary>情绪处境套（§12 #70）：Daily=日常闲聊（默认路径）、Victim=事件受害者、Onlooker=事件围观者。</summary>
        public enum MoodSet { Daily, Victim, Onlooker }

        /// <summary>句型签词表（#72 重构 6→3）：短=≤12字脱口而出；中=13-25字一句话说清；长=26-40字带一件具体的事。
        /// （带数字/物件/因果强制签已退役——具体感归闲聊头【语域】，别加回来。）</summary>
        public static readonly string[] Shapes = { "短", "中", "长" };

        /// <summary>句型档字数边界（落档率/校验口径，与【规格】段文案同改）：短≤12、中≤25、长≤40。</summary>
        public static bool InShapeTier(int shape, int len) => shape switch
        {
            0 => len <= 12,
            1 => len is >= 13 and <= 25,
            2 => len is >= 26 and <= 40,
            _ => true,
        };

        /// <summary>口吻结构签词表（16 词：实验 8 词转正+#72 第二批扩编——小弧签三变体+开场叹词；
        /// 甩话头归对话结构签不进独白，别往里加）。
        /// 重签稀有化（2026-09-20 玩家"不用刻意"实锤：哎呦 18 次/~120 行=15%）：把戏感重的
        /// 省略号/口头叹词/复读/反问连发/拟声插入/自嘲 在权重表里一律低压（各 ~4-6%），
        /// 轻签（句尾语气词×2/碎嘴/自我纠正/重复半句）常态。</summary>
        public static readonly string[] Voices =
        {
            "句尾爱带语气词（啊/呗/呢）",
            "句尾爱带语气词（嘛/咯/嘞）",
            "爱反问",
            "会自我纠正（\"哦不对\"/\"我是说\"）",
            "碎嘴短句（三五字一顿）",
            "爱重复半句",
            "爱用省略号（话说一半）",
            "爱用口头叹词（好家伙/嚯/哎呦）",
            "复读（知道了知道了）",
            "反问连发",
            "拟声插入（哎呦哎呦）",
            "自嘲",
            "恐慌弧（\"完了完了\"型——坏事将至先喊一嗓子）",
            "自我激励弧（先打鸡血后泄气——\"最后一单搞完吃顿好的…困死我了\"型）",
            "侥幸弧（抱怨到一半解决——\"找不到…找到了！\"型）",
            "开场叹词（我去/不是/搞啥呢）",
        };

        /// <summary>性格签词表（6 种，口吻子集选择器——不上卡面；按市民/车辆实体 Index 哈希粘滞）。</summary>
        public static readonly string[] Personalities = { "碎嘴", "闷葫芦", "毒舌", "慢性子", "杠精", "热心肠" };

        // 六性格 × 16 口吻 权重表（如何调：数值=相对权重，0=不选；行序与 Personalities、列序与 Voices 对齐。
        // 重签（列 6-11）全表压 0-2（≈4-6%，玩家"不用刻意"实锤——碎嘴也别顶格重签；闷葫芦的省略号是其
        // 标志但同样降到一成内）。小弧签（12-14）中档；开场叹词（15）轻中档。
        private static readonly int[][] k_VoiceWeights =
        {
            new[] { 8, 6, 5, 5, 12, 8, 1, 1, 1, 2, 1, 1, 3, 3, 3, 4 }, // 碎嘴（碎嘴短句是其招牌但不靠重签撑）
            new[] { 3, 1, 1, 1, 5, 3, 2, 0, 1, 0, 0, 1, 1, 1, 2, 2 }, // 闷葫芦
            new[] { 4, 1, 6, 3, 2, 2, 1, 0, 1, 2, 0, 2, 2, 1, 1, 1 }, // 毒舌
            new[] { 4, 6, 1, 2, 2, 4, 2, 0, 1, 0, 0, 1, 1, 2, 2, 2 }, // 慢性子
            new[] { 4, 1, 8, 2, 2, 2, 1, 0, 1, 2, 0, 1, 2, 1, 1, 1 }, // 杠精
            new[] { 8, 4, 2, 2, 2, 2, 1, 2, 1, 0, 2, 1, 2, 2, 2, 3 }, // 热心肠
        };

        /// <summary>日常情绪套（10 词，含原 6 词；§12 #70 扩：累/闲/期待/郁闷）。</summary>
        public static readonly string[] DailyMoods = { "烦", "乐", "麻木", "急", "馋", "困", "累", "闲", "期待", "郁闷" };

        /// <summary>受害者情绪套（§12 #70：事件当事人——疼/怕/怒/懵/绝望/后怕；不配"乐/馋"等日常词）。</summary>
        public static readonly string[] VictimMoods = { "疼", "怕", "怒", "懵", "绝望", "后怕" };

        /// <summary>围观者情绪套（§12 #70：事件现场围观——惊/好奇/同情/看热闹/担心）。</summary>
        public static readonly string[] OnlookerMoods = { "惊", "好奇", "同情", "看热闹", "担心" };

        /// <summary>日常套别名（向后兼容：#69 时代的 Moods 引用=DailyMoods，一处定义）。</summary>
        public static readonly string[] Moods = DailyMoods;

        /// <summary>处境套 → 词表。</summary>
        public static string[] MoodsOf(MoodSet set) => set switch
        {
            MoodSet.Victim => VictimMoods,
            MoodSet.Onlooker => OnlookerMoods,
            _ => DailyMoods,
        };

        /// <summary>性格签（按人锚）：实体 Index 哈希 → 性格索引（同一人永远同一性格，市民/车辆同口径）。</summary>
        public static int PersonalityOf(int entityIndex) => (entityIndex & 0x7FFFFFFF) % Personalities.Length;

        /// <summary>该炉第 cardIdx 张卡的规格分配（句型/情绪/口吻三元）。salt=炉计数；
        /// used=本炉已占（写,情,口）组合集（调用方每炉新建一个，撞签顺推到下一空闲组合——同签去重）；
        /// personality=性格索引（<see cref="PersonalityOf"/> 按人锚）——口吻从该性格权重表加权取；
        /// set=情绪处境套（默认日常）。情绪索引是所选套内下标；口吻索引是 Voices 下标。</summary>
        public static (int Shape, int Mood, int Voice) Assign(uint salt, int cardIdx,
            HashSet<(int Shape, int Mood, int Voice)> used, int personality,
            MoodSet set = MoodSet.Daily)
            => Assign(salt, cardIdx, used, personality, MoodsOf(set));

        /// <summary>自定义情绪子集重载（§12 #71 场景签联动）。</summary>
        public static (int Shape, int Mood, int Voice) Assign(uint salt, int cardIdx,
            HashSet<(int Shape, int Mood, int Voice)> used, int personality,
            string[] moods)
        {
            var total = Shapes.Length * moods.Length * Voices.Length;
            var baseIdx = (int)((salt * 7 + (uint)cardIdx * 13) % (uint)total);
            var weights = k_VoiceWeights[((personality & 0x7FFFFFFF) % k_VoiceWeights.Length + k_VoiceWeights.Length) % k_VoiceWeights.Length];
            for (var step = 0; step < total; step++)
            {
                var idx = (baseIdx + step) % total;
                var s = idx % Shapes.Length;
                var rest = idx / Shapes.Length;
                var m = rest % moods.Length;
                // 口吻按性格权重表加权取（同一（写,情）组合下口吻也随签序变——组合空间 3×10×16=480 ≫ 炉卡数）
                var v = PickVoice(weights, salt * 7919u + (uint)cardIdx * 104729u + (uint)step * 31u);
                if (used.Add((s, m, v)))
                    return (s, m, v);
            }
            return (-1, -1, -1); // 组合耗尽（词表缩到极小才会到），调用方不缀签——兜底不炸
        }

        /// <summary>性格权重表内加权取签（确定性：roll 锚定盐值，经 Mix32 充分打散——
        /// 小权重总和下相邻盐值不串区（2026-09-20 sameperson 具组实锤：朴素"salt+卡序"roll
        /// 在 29 总权下相邻炉/卡强相关，毒舌高权签命中 1/12 病理值）。</summary>
        private static int PickVoice(int[] weights, uint roll)
        {
            roll = Mix32(roll);
            var total = 0;
            for (var i = 0; i < weights.Length; i++)
                total += weights[i];
            if (total <= 0)
                return (int)(roll % (uint)weights.Length);
            var r = (int)(roll % (uint)total);
            for (var i = 0; i < weights.Length; i++)
            {
                r -= weights[i];
                if (r < 0)
                    return i;
            }
            return weights.Length - 1;
        }

        /// <summary>32 位整数混合（lowbias 三步乘加：相邻输入雪崩到全空间）。</summary>
        private static uint Mix32(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CED68u;
            x ^= x >> 16;
            return x;
        }

        /// <summary>该炉第 cardIdx 张卡的规格签后缀（"<c>｜写：…｜情：…｜口：…</c>"）；组合耗尽返回空串（不缀签）。</summary>
        public static string TagFor(uint salt, int cardIdx, HashSet<(int Shape, int Mood, int Voice)> used,
                                    int personality, MoodSet set = MoodSet.Daily)
        {
            var (s, m, v) = Assign(salt, cardIdx, used, personality, set);
            return s < 0 ? "" : $"｜写：{Shapes[s]}｜情：{MoodsOf(set)[m]}｜口：{Voices[v]}";
        }

        /// <summary>自定义情绪子集的签后缀（§12 #71 场景签联动）。</summary>
        public static string TagFor(uint salt, int cardIdx, HashSet<(int Shape, int Mood, int Voice)> used,
                                    int personality, string[] moods)
        {
            var (s, m, v) = Assign(salt, cardIdx, used, personality, moods);
            return s < 0 ? "" : $"｜写：{Shapes[s]}｜情：{moods[m]}｜口：{Voices[v]}";
        }
    }
}
