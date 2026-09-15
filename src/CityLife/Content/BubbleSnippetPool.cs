using System.Collections.Generic;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// 气泡场合（粗粒度）：§12 #60 刀①起由 CitizenPoolSystem 采样处境卡时执行层盖章（乘车/在建筑/走路
    /// 是采样时已确定事实，LLM 只报 card 归属不再判场合）。给 S5 按锚点类型匹配用：步行的人吃 Walk、
    /// 车里的人吃 Vehicle、建筑锚点吃 Indoor，Any 是万用兜底（card 缺失/越界的 salvage，室内室外都说得通的话）。
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
    /// 闲聊炉一行的解析结果（§12 #63 对话场景卡）：独白行只有 Text；对卡行（{"card":N,"a":..,"b":..}）
    /// A/B 非空（台词本身，不含名字——名字前缀由执行层收炉拼装，LLM 不碰名字，单向阀门）。
    /// </summary>
    public readonly struct ParsedChatterLine
    {
        /// <summary>独白正文（≤40 字；对卡行可空——缺半句/超长的降级独白才带）。</summary>
        public readonly string? Text;
        /// <summary>归属处境卡号（1 起；缺失/非正数=0 无归属，场合/分区由调用方落 Any/留空）。</summary>
        public readonly int Card;
        /// <summary>对卡甲台词（≤12 字；非对卡行为 null）。</summary>
        public readonly string? A;
        /// <summary>对卡乙台词（≤12 字；非对卡行为 null）。</summary>
        public readonly string? B;

        public ParsedChatterLine(string? text, int card, string? a, string? b)
        {
            Text = text;
            Card = card;
            A = a;
            B = b;
        }

        /// <summary>是否对卡行（a/b 双全且合规）。</summary>
        public bool IsDialogue => A != null && B != null;
    }

    /// <summary>
    /// 一条气泡片段：Text=正文（≤40 字硬顶；§12 #60 刀④长短句规格：短 ≤15 纯反应/长 20-40 带信息骨架；
    /// §12 #63 对话条=执行层拼"名字A：台词\n名字B：台词"单条 Text 内嵌 \n 两行，渲染零改动）；
    /// Occasion=场合（S5 按锚点匹配的主键；§12 #60 刀①起由处境卡执行层盖章，card 缺失/越界落 Any）；
    /// Zone=话题分区（收炉时按 card 号逐条对齐回填，孤儿行留空）；BornCycle=出生炉次（新鲜度基准，
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
    /// 容量上限 MaxCapacity（默认 200），先进先出逐出（表头最旧）；同文本去重（炉内重复/跨炉撞车都丢）。
    /// **取用即消耗**（2026-09-11 玩家定案，§12 #53：真一次性——同一句话全城一辈子只说一遍；
    /// 池空调用方落"……"沉默泡，宁沉默不重复）。
    ///
    /// 如何扩展（社区贡献点）：
    ///   · 新场合：BubbleOccasion 加枚举值 + CitizenPoolSystem 盖章规则（DescribeSituation）补一支
    ///     + S5 取用侧按锚点类型映射补一支——三处同炉改，漏一处=新场合静默落 Any；
    ///   · 新匹配维度（按 Zone/新鲜度取泡等）：加 PickFor 重载，别改既有 PickFor 的语义契约
    ///     （同 salt+池状态必同条——取用即消耗本身就是池状态变化，气泡换文案据此可复现）。
    /// </summary>
    public sealed class BubbleSnippetPool
    {
        private readonly List<BubbleSnippet> m_Entries = new();

        /// <summary>池容上限：超出先进先出逐出最旧片段。</summary>
        public int MaxCapacity = 200;

        // 猫密度闸（2026-09-11 玩家实机"宠物占比太高"）：模型先验爱写猫——prompt 配额（闲聊炉头【配额】）是劝，
        // 这里是拦。含猫词的片段在池内猫占比 ≥k_CatShareCap 时确定性半数拒收（不删猫，限流）
        private const double k_CatShareCap = 0.05;
        private static readonly string[] k_CatWords = { "猫", "狗", "宠物", "喵", "汪" };

        /// <summary>当前炉次（闲聊炉炉计数同步；BornCycle 的基准）。</summary>
        public uint CurrentCycle;

        /// <summary>池内全部片段（只读视图，诊断/S5 取用）。</summary>
        public IReadOnlyList<BubbleSnippet> Entries => m_Entries;

        /// <summary>当前条数。</summary>
        public int Count => m_Entries.Count;

        /// <summary>入库一条：空文本/与池内同文本直接丢弃（返回 false）；超容先进先出逐出；
        /// 含猫词且池内猫占比超 k_CatShareCap 的确定性半数拒收（猫密度闸）。返回是否真入。</summary>
        public bool Add(string text, BubbleOccasion occasion, string? zone = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            if (OverCatCap(text))
                return false;
            for (int i = 0; i < m_Entries.Count; i++)
                if (m_Entries[i].Text == text)
                    return false; // 同文本去重
            m_Entries.Add(new BubbleSnippet { Text = text, Occasion = occasion, Zone = zone ?? "", BornCycle = CurrentCycle });
            while (m_Entries.Count > MaxCapacity)
                m_Entries.RemoveAt(0); // FIFO 逐出最旧
            return true;
        }

        /// <summary>猫密度闸：text 含猫词且池内猫占比已超 k_CatShareCap → FNV 哈希+CurrentCycle 确定性半数拒收。</summary>
        private bool OverCatCap(string text)
        {
            var hit = false;
            foreach (var w in k_CatWords)
                if (text.Contains(w)) { hit = true; break; }
            if (!hit || m_Entries.Count == 0)
                return false;
            var cats = 0;
            foreach (var e in m_Entries)
                foreach (var w in k_CatWords)
                    if (e.Text.Contains(w)) { cats++; break; }
            if ((double)cats / m_Entries.Count < k_CatShareCap)
                return false;
            uint h = 2166136261u; // FNV-1a（跨进程稳定，不依赖 string.GetHashCode）
            foreach (var c in text) { h ^= c; h *= 16777619u; }
            return (h + CurrentCycle) % 2 == 0;
        }

        /// <summary>
        /// 闲聊炉 JSONL 批量解析（JsonMini 同款 salvage 纪律——RimTalk 教训：LLM 输出非法 JSON 是最高频故障）：
        /// 独白行 {"text":..,"card":N}——text 缺失/空白/超 40 字（气泡排版硬顶：13 格×4 行≈52 格，留余量取 40）
        /// 计 skipped 丢弃；对卡行（§12 #63）{"card":N,"a":..,"b":..}——a/b 双全且各 ≤12 字才成立，
        /// 缺半句或超长降级：有合规 text 落独白、无 text 计 skipped 丢弃。
        /// markdown 围栏行/残行天然没有合法字段，自动落进 skipped；card 缺失/非数/负数不丢整行、落 0
        /// （=无归属，场合/分区由调用方落 Any/留空——§12 #60 刀①：场合由处境卡执行层盖章，LLM 只报归属）。
        /// </summary>
        public static List<ParsedChatterLine> ParseBatch(string jsonl, out int skipped)
        {
            var list = new List<ParsedChatterLine>();
            skipped = 0;
            if (string.IsNullOrWhiteSpace(jsonl))
                return list;
            foreach (var raw in jsonl.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                var card = JsonMini.GetInt(line, "card") ?? 0;
                if (card < 0)
                    card = 0;
                // §12 #63 对卡行优先：a/b 双全且各 ≤12 字 → 对话（text 顺带保留，拼装失败可回退独白）
                var a = JsonMini.GetStr(line, "a")?.Trim();
                var b = JsonMini.GetStr(line, "b")?.Trim();
                if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                    && a!.Length <= 12 && b!.Length <= 12)
                {
                    var fallback = JsonMini.GetStr(line, "text")?.Trim();
                    list.Add(new ParsedChatterLine(
                        string.IsNullOrEmpty(fallback) || fallback!.Length > 40 ? null : fallback,
                        card, a, b));
                    continue;
                }
                // 独白路径（对卡缺半句/超长也落这里——salvage 纪律：有合规 text 就不丢）
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
                list.Add(new ParsedChatterLine(text, card > 0 ? card : 0, null, null));
            }
            return list;
        }

        /// <summary>
        /// S5 取泡口：按场合确定性抽一条（候选 = exact 场合 ∪ Any；同 salt+池状态必同条）。
        /// maxLen 限长（§12 #51 长文稳锚：动的锚点只配短句；入库已 ≤40 字）；限长后无候选回退不限长。
        /// **纯探测不消耗**——顺探被拒的候选不能白烧；真正采用时调用方必须紧跟 <see cref="Consume"/>
        /// （§12 #53 真一次性：取用即消耗=从池删除，同一句话全城只说一遍）。
        /// 池空/无候选返回 null（调用方落"……"沉默泡，宁沉默不重复）。
        /// </summary>
        public BubbleSnippet? PickFor(BubbleOccasion occasion, uint salt, int maxLen = int.MaxValue)
        {
            var picked = PickFiltered(occasion, salt, maxLen);
            if (picked == null && maxLen != int.MaxValue)
                picked = PickFiltered(occasion, salt, int.MaxValue);
            return picked;
        }

        /// <summary>取用消耗（§12 #53）：从池删除该条。只有真正采用才调——探测候选不算用。</summary>
        public void Consume(BubbleSnippet entry) => m_Entries.Remove(entry);

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
