using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// 闲聊炉写法规格签（§12 #69，2026-09-16 玩家拍板；实验依据 logs/prompt-eval/eval-20260916-变体实验.md）：
    /// 多样性不靠 LLM 自觉——每张处境卡尾缀系统分配规格 <c>｜写：句型｜情：情绪</c>，LLM 只服从不参与分配
    /// （单向阀门）。句型签必须：三个长句签（带数字/带物件/带因果）对冲撤掉【样子】例句后的短句引力
    /// （实验 V1 纯去例句塌 71% 短句实锤）；同炉同（写,情）签顺延去重（实验抓到"困得眼皮打架"×2 撞车）。
    /// 确定性：组合基址=炉计数×7+卡序×13 取模（同炉次同卡序必同签，可复现），撞签顺推到下一空闲组合（确定性）。
    /// 共享纪律：BubbleChatterSystem 组卡与 tools/PromptEval 卡具同用这一处定义（一处定义别复制粘贴）。
    /// 如何扩展：加句型/情绪=直接往 Shapes/Moods 加词（组合数=两表乘积，需 ≥ 每炉卡数 16，否则去重循环兜底
    /// 会重复出签）；规格口径文案在 PromptBuilder.BuildChatterPrompt【规格】段，与词表同炉改。
    /// </summary>
    public static class ChatterSpec
    {
        /// <summary>句型签词表（短反应=≤15字脱口而出；带数字/带物件/带因果长句=20-40字各带数字/具体物件/前因后果；
        /// 吐槽一句=一句牢骚；自言自语=低声嘟囔）。</summary>
        public static readonly string[] Shapes = { "短反应", "带数字长句", "带物件长句", "带因果长句", "吐槽一句", "自言自语" };

        /// <summary>情绪签词表（情绪底色，给 LLM 的语气锚）。</summary>
        public static readonly string[] Moods = { "烦", "乐", "麻木", "急", "馋", "困" };

        /// <summary>该炉第 cardIdx 张卡的规格分配（句型/情绪索引）。salt=炉计数；
        /// used=本炉已占（写,情）组合集（调用方每炉新建一个，撞签顺推到下一空闲组合——同签去重）。
        /// 指标/评测要拿句型索引用本方法（别解析签文本）。</summary>
        public static (int Shape, int Mood) Assign(uint salt, int cardIdx, HashSet<(int Shape, int Mood)> used)
        {
            var total = Shapes.Length * Moods.Length;
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
        public static string TagFor(uint salt, int cardIdx, HashSet<(int Shape, int Mood)> used)
        {
            var (s, m) = Assign(salt, cardIdx, used);
            return s < 0 ? "" : $"｜写：{Shapes[s]}｜情：{Moods[m]}";
        }
    }
}
