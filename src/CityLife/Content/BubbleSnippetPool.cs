using System.Collections.Generic;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// 气泡场合（粗粒度）：由处境卡推导——推导规则写死在闲聊炉 prompt 头里（PromptBuilder.BuildChatterHead），
    /// 模型按卡打标随 JSONL 行输出。给 S5 按锚点类型匹配用：步行的人吃 Walk、车里的人吃 Vehicle、
    /// 建筑锚点吃 Indoor，Any 是万用兜底（拿不准/室内室外都说得通的话）。
    /// </summary>
    public enum BubbleOccasion
    {
        /// <summary>通用（拿不准时落这里，任何锚点都可匹配）。</summary>
        Any = 0,
        /// <summary>步行（在街上走，没乘车）。</summary>
        Walk = 1,
        /// <summary>乘车（私家车/出租车/公交/货车里）。</summary>
        Vehicle = 2,
        /// <summary>室内（建筑/场所内，不在路上）。</summary>
        Indoor = 3,
    }

    /// <summary>
    /// 一条气泡片段：Text=正文（≤40 字硬顶；prompt 目标 ≤20 字，"张嘴说话"语域）；
    /// Occasion=场合（S5 按锚点匹配的主键）；Zone=话题分区（收炉时与处境卡按序对齐回填——
    /// 模型掉行/加行导致对不齐则整批留空，宁缺勿错配）；BornCycle=出生炉次（新鲜度基准，
    /// S5 轮换/衰减可用；衰减口径未定，v1 只记不判）。
    /// </summary>
    public sealed class BubbleSnippet
    {
        public string Text = "";
        public BubbleOccasion Occasion;
        public string Zone = "";
        public uint BornCycle;
    }

    /// <summary>
    /// 气泡片段池（S4，§12 #48 双炉定案：闲聊炉产片段入池，展示零 token——气泡刷屏不花 token）。
    /// 纯数据模块，不碰游戏 API；装载点=BubbleChatterSystem（闲聊炉收炉），消费口=S5 气泡世界层
    /// （经 BubbleChatterSystem.Snippets 取用——本池不知道也不关心谁读）。
    ///
    /// 容量上限 MaxCapacity（默认 100），先进先出逐出（表头最旧）；同文本去重（炉内重复/跨炉撞车都丢）。
    ///
    /// 如何扩展（社区贡献点）：
    ///   · 新场合：BubbleOccasion 加枚举值 + OccasionFromString 加映射 + 闲聊炉 prompt 头
    ///     （PromptBuilder.BuildChatterHead）补一条推导规则——三处必须同炉改，漏一处=新场合静默落 Any；
    ///   · 新匹配维度（按 Zone/新鲜度取泡等）：加 PickFor 重载，别改既有 PickFor 的确定性语义
    ///     （同 salt+池状态必同条——气泡换文案要可复现，世界层据此排重）。
    /// </summary>
    public sealed class BubbleSnippetPool
    {
        private readonly List<BubbleSnippet> m_Entries = new();

        /// <summary>池容上限：超出先进先出逐出最旧片段。</summary>
        public int MaxCapacity = 100;

        /// <summary>当前炉次（闲聊炉炉计数同步；BornCycle 的基准）。</summary>
        public uint CurrentCycle;

        /// <summary>池内全部片段（只读视图，诊断/S5 取用）。</summary>
        public IReadOnlyList<BubbleSnippet> Entries => m_Entries;

        /// <summary>当前条数。</summary>
        public int Count => m_Entries.Count;

        /// <summary>入库一条：空文本/与池内同文本直接丢弃（返回 false）；超容先进先出逐出。返回是否真入。</summary>
        public bool Add(string text, BubbleOccasion occasion, string? zone = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            for (int i = 0; i < m_Entries.Count; i++)
                if (m_Entries[i].Text == text)
                    return false; // 同文本去重
            m_Entries.Add(new BubbleSnippet { Text = text, Occasion = occasion, Zone = zone ?? "", BornCycle = CurrentCycle });
            while (m_Entries.Count > MaxCapacity)
                m_Entries.RemoveAt(0); // FIFO 逐出最旧
            return true;
        }

        /// <summary>occasion 字符串 → 枚举（"walk|vehicle|indoor|any"）；null/未知一律落 Any——salvage 纪律：场合是提示不是命门，不因此丢整行。</summary>
        public static BubbleOccasion OccasionFromString(string? s) => s switch
        {
            "walk" => BubbleOccasion.Walk,
            "vehicle" => BubbleOccasion.Vehicle,
            "indoor" => BubbleOccasion.Indoor,
            _ => BubbleOccasion.Any,
        };

        /// <summary>
        /// 闲聊炉 JSONL 批量解析（JsonMini 同款 salvage 纪律——RimTalk 教训：LLM 输出非法 JSON 是最高频故障）：
        /// 逐行解析 {"text":..,"occasion":..}；空行/注释行跳过不计数；text 缺失/空白/超 40 字
        /// （气泡排版硬顶：13 格×4 行≈52 格，留余量取 40）计 skipped 丢弃——markdown 围栏行/残行
        /// 天然没有合法 text 字段，自动落进 skipped；occasion 缺失/未知不丢整行、落 Any。
        /// </summary>
        public static List<(string Text, BubbleOccasion Occasion)> ParseBatch(string jsonl, out int skipped)
        {
            var list = new List<(string, BubbleOccasion)>();
            skipped = 0;
            if (string.IsNullOrWhiteSpace(jsonl))
                return list;
            foreach (var raw in jsonl.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                var text = JsonMini.GetStr(line, "text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    skipped++;
                    continue;
                }
                text = text!.Trim();
                if (text.Length > 40)
                {
                    skipped++;
                    continue;
                }
                list.Add((text, OccasionFromString(JsonMini.GetStr(line, "occasion"))));
            }
            return list;
        }

        /// <summary>
        /// S5 取泡口：按场合确定性抽一条（候选 = exact 场合 ∪ Any；同 salt+池状态必同条——气泡换文案要可复现）。
        /// maxLen 限长（§12 #51 长文稳锚：动的锚点只配短句；入库已 ≤40 字）；限长后无候选回退不限长（宁长勿模板）。
        /// 池空/无候选返回 null（调用方回退占位文案）。
        /// </summary>
        public BubbleSnippet? PickFor(BubbleOccasion occasion, uint salt, int maxLen = int.MaxValue)
        {
            var picked = PickFiltered(occasion, salt, maxLen);
            return picked ?? (maxLen == int.MaxValue ? null : PickFiltered(occasion, salt, int.MaxValue));
        }

        /// <summary>限长过滤的确定性抽取（候选计数 → salt 取模定位）。无候选返回 null。</summary>
        private BubbleSnippet? PickFiltered(BubbleOccasion occasion, uint salt, int maxLen)
        {
            var n = 0;
            for (int i = 0; i < m_Entries.Count; i++)
                if ((m_Entries[i].Occasion == occasion || m_Entries[i].Occasion == BubbleOccasion.Any)
                    && m_Entries[i].Text.Length <= maxLen)
                    n++;
            if (n == 0)
                return null;
            var target = (int)(salt % (uint)n);
            for (int i = 0; i < m_Entries.Count; i++)
            {
                var e = m_Entries[i];
                if (e.Occasion != occasion && e.Occasion != BubbleOccasion.Any)
                    continue;
                if (e.Text.Length > maxLen)
                    continue;
                if (target-- == 0)
                    return e;
            }
            return null; // 理论到不了，纯防御
        }
    }
}
