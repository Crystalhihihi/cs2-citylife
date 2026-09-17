using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// 闲聊炉写法规格签（§12 #69，2026-09-16 玩家拍板；实验依据 logs/prompt-eval/eval-20260916-变体实验.md）：
    /// 多样性不靠 LLM 自觉——每张处境卡尾缀系统分配规格 <c>｜写：句型｜情：情绪</c>，LLM 只服从不参与分配
    /// （单向阀门）。句型签必须：三个长句签（带数字/带物件/带因果）对冲撤掉【样子】例句后的短句引力
    /// （实验 V1 纯去例句塌 71% 短句实锤）；同炉同（写,情）签顺延去重（实验抓到"困得眼皮打架"×2 撞车）。
    /// §12 #70 情绪签处境子集化（2026-09-17 玩家拍板"随机不适合所有情况，6 词太少要扩"）：
    /// 情绪词库扩三套——日常套 10 词（含原 6 词）、受害者套（事件当事人）、围观套（事件现场围观）；
    /// 配签按处境子集抽签（病人不配乐、现场不配馋），句型签维持 6 词不变。
    /// 确定性：组合基址=炉计数×7+卡序×13 取模（同炉次同卡序必同签，可复现），撞签顺推到下一空闲组合（确定性）。
    /// 共享纪律：BubbleChatterSystem 组卡与 tools/PromptEval 卡具同用这一处定义（一处定义别复制粘贴）。
    /// 如何扩展：加句型/情绪=直接往 Shapes 或对应情绪套加词（组合数=Shapes×该套词数，需 ≥ 每炉卡数 16，
    /// 否则去重循环兜底会重复出签）；规格口径文案在 PromptBuilder.BuildChatterPrompt【规格】段，与词表同炉改。
    /// </summary>
    public static class ChatterSpec
    {
        /// <summary>情绪处境套（§12 #70）：Daily=日常闲聊（默认路径）、Victim=事件受害者、Onlooker=事件围观者。</summary>
        public enum MoodSet { Daily, Victim, Onlooker }

        /// <summary>句型签词表（短反应=≤15字脱口而出；带数字/带物件/带因果长句=20-40字各带数字/具体物件/前因后果；
        /// 吐槽一句=一句牢骚；自言自语=低声嘟囔）。</summary>
        public static readonly string[] Shapes = { "短反应", "带数字长句", "带物件长句", "带因果长句", "吐槽一句", "自言自语" };

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

        /// <summary>该炉第 cardIdx 张卡的规格分配（句型/情绪索引）。salt=炉计数；
        /// used=本炉已占（写,情）组合集（调用方每炉新建一个，撞签顺推到下一空闲组合——同签去重）；
        /// set=情绪处境套（§12 #70 子集化，默认日常）。情绪索引是**所选套内**下标（MoodsOf(set)[mood] 取词）。
        /// 指标/评测要拿句型索引用本方法（别解析签文本）。</summary>
        public static (int Shape, int Mood) Assign(uint salt, int cardIdx, HashSet<(int Shape, int Mood)> used,
                                                   MoodSet set = MoodSet.Daily)
            => Assign(salt, cardIdx, used, MoodsOf(set));

        /// <summary>自定义情绪子集重载（§12 #71 场景签联动：贴景卡从场景行的推荐子集抽情绪）。
        /// 情绪索引是 moods 数组内下标；used 集合与日常套共用（同炉同签去重跨子集也成立）。</summary>
        public static (int Shape, int Mood) Assign(uint salt, int cardIdx, HashSet<(int Shape, int Mood)> used,
                                                   string[] moods)
        {
            var total = Shapes.Length * moods.Length;
            var baseIdx = (int)((salt * 7 + (uint)cardIdx * 13) % (uint)total);
            for (var step = 0; step < total; step++)
            {
                var idx = (baseIdx + step) % total;
                var s = idx % Shapes.Length;
                var m = idx / Shapes.Length;
                if (used.Add((s, m)))
                    return (s, m);
            }
            return (-1, -1); // 组合耗尽（词表缩到小于炉卡数才会到），调用方不缀签——兜底不炸
        }

        /// <summary>该炉第 cardIdx 张卡的规格签后缀（"<c>｜写：…｜情：…</c>"）；组合耗尽返回空串（不缀签）。</summary>
        public static string TagFor(uint salt, int cardIdx, HashSet<(int Shape, int Mood)> used,
                                    MoodSet set = MoodSet.Daily)
        {
            var (s, m) = Assign(salt, cardIdx, used, set);
            return s < 0 ? "" : $"｜写：{Shapes[s]}｜情：{MoodsOf(set)[m]}";
        }

        /// <summary>自定义情绪子集的签后缀（§12 #71 场景签联动）。</summary>
        public static string TagFor(uint salt, int cardIdx, HashSet<(int Shape, int Mood)> used,
                                    string[] moods)
        {
            var (s, m) = Assign(salt, cardIdx, used, moods);
            return s < 0 ? "" : $"｜写：{Shapes[s]}｜情：{moods[m]}";
        }
    }
}
