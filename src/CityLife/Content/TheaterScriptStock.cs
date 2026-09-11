using System;
using System.Collections.Generic;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// 一部库存剧本（§12 #52 炉→池→放送）：剧本不再为具体市民定制，是"场景标签+人数+台词"的
    /// 库存货；放送时哪组真人成立就绑给谁演。
    /// Scene=场景标签（station/park/shop/home，见 <see cref="TheaterScriptStock.Scenes"/>）；
    /// Cast=人数（2-3，放送时绑这么多真人）；Lines=台词队列，Speaker=0 基角色序号（&lt; Cast）。
    /// </summary>
    public sealed class TheaterScript
    {
        public string Scene = "";
        public int Cast;
        public readonly List<(int Speaker, string Text)> Lines = new();
    }

    /// <summary>
    /// 小剧场剧本池（§12 #52，话题库/片段池同款"炉→池"缓冲）：剧本炉水位触发产库存
    /// （总库存 &lt;6 开炉、一炉 4 部，触发在 BubbleTheaterSystem），放送时按场景标签+人数
    /// 取一条即时开播——池是缓冲，LLM 分钟级延迟被池子吸收，不再播前绑定等 LLM。
    /// 纯数据模块，不碰游戏 API；装载点=BubbleTheaterSystem.OnTheaterResult（收炉入池），
    /// 消费口=同系统放送评估（TryTake/Return）。
    ///
    /// 消耗语义（#52 定案）：<b>开播才消耗</b>——TryTake 取出即离池，绑锚失败走 Return 退回
    /// 不消耗；开播后终了即弃不退。容量上限 MaxCapacity（默认 12），超容先进先出逐出最旧。
    ///
    /// 如何扩展（社区贡献点）：
    ///   · 新场景标签：Scenes 加常量 + 剧本炉 prompt 头（PromptBuilder.BuildTheaterStockHead）
    ///     【场景】段补一条映射 + BubbleTheaterSystem 候选侧场景标签推导（ScanOutdoor/ScanIndoor
    ///     的 SceneTag 赋值）——三处必须同炉改，漏一处=新标签剧本永远绑不出去或永远产不出；
    ///   · 新取件策略（按新鲜度/随机等）：加 TryTake 重载，别改既有 TryTake 的确定性语义
    ///     （同池状态必同条——"取最旧匹配"与 FIFO 逐出配对，库存自然轮换）。
    /// </summary>
    public sealed class TheaterScriptStock
    {
        /// <summary>场景标签：车站候车。</summary>
        public const string Station = "station";
        /// <summary>场景标签：公园/景点。</summary>
        public const string Park = "park";
        /// <summary>场景标签：商店里。</summary>
        public const string Shop = "shop";
        /// <summary>场景标签：住宅里。</summary>
        public const string Home = "home";

        /// <summary>全部合法场景标签（schema 校验白名单 + 剧本炉 prompt 库存计数迭代用）。</summary>
        public static readonly string[] Scenes = { Station, Park, Shop, Home };

        private const int k_MinCast = 2;   // 人数下限（小剧场定义即 ≥2 人）
        private const int k_MaxCast = 3;   // 人数上限：2-3 人好绑定（#52——开放场所绑得到真人的概率随人数陡降）
        private const int k_MinLines = 2;  // 有效剧本最少句数（<2 句不成对话，作废）
        private const int k_MaxLines = 12; // 单部句数硬顶（prompt 目标 6-8 句；气泡一句一泡，拖太长地点早散了）
        private const int k_MaxTextLen = 40; // 单句硬顶（气泡排版硬顶，与片段池同口径）

        private readonly List<TheaterScript> m_Entries = new();

        /// <summary>池容上限：超出先进先出逐出最旧剧本。</summary>
        public int MaxCapacity = 12;

        /// <summary>池内全部剧本（只读视图，诊断用）。</summary>
        public IReadOnlyList<TheaterScript> Entries => m_Entries;

        /// <summary>当前部数。</summary>
        public int Count => m_Entries.Count;

        /// <summary>指定场景标签的现存部数（剧本炉 prompt 动态尾"低水位分区多配题"用）。</summary>
        public int CountOf(string scene)
        {
            var n = 0;
            for (int i = 0; i < m_Entries.Count; i++)
                if (m_Entries[i].Scene == scene)
                    n++;
            return n;
        }

        /// <summary>
        /// 放送取件口：场景标签严格相符 + cast ≤ rosterMax（绑得到的人才演得起），确定性取最旧一条
        /// （与 FIFO 逐出配对，库存自然轮换）。<b>取出即离池</b>——绑锚失败须由调用方 Return 退回；
        /// 开播后终了即弃不退。无匹配返回 null（调用方本拍跳过，不打炉——炉只由水位触发）。
        /// </summary>
        public TheaterScript? TryTake(string scene, int rosterMax)
        {
            for (int i = 0; i < m_Entries.Count; i++)
            {
                var e = m_Entries[i];
                if (e.Scene != scene || e.Cast > rosterMax)
                    continue;
                m_Entries.RemoveAt(i);
                return e;
            }
            return null;
        }

        /// <summary>退回口（绑锚失败/开播中止专用）：塞回队首——它是该标签下最旧匹配，下拍仍优先被取。</summary>
        public void Return(TheaterScript script) => m_Entries.Insert(0, script);

        /// <summary>
        /// 剧本炉产出批量入库（收炉装载口）：JSONL salvage 解析（坏行跳过计数）→ 逐部入库，
        /// 超容先进先出逐出最旧。返回入库部数。
        /// </summary>
        public int AddBatch(string jsonl, out int skipped)
        {
            var scripts = ParseBatch(jsonl, out skipped);
            foreach (var s in scripts)
            {
                m_Entries.Add(s);
                while (m_Entries.Count > MaxCapacity)
                    m_Entries.RemoveAt(0); // FIFO 逐出最旧
            }
            return scripts.Count;
        }

        /// <summary>
        /// 剧本 JSONL salvage 解析（JsonMini 同款纪律——LLM 输出非法 JSON 是最高频故障）：
        /// 一行一整部 {"scene":"station|park|shop|home","cast":2,"lines":[{"speaker":1,"text":"…"}]}；
        /// 空行/注释行跳过不计数；scene 非法/cast 缺或越界（2-3）/有效台词 &lt;2 句的整行丢弃计数。
        /// 台词句内 salvage：speaker 越界（1..cast）/text 空/超 40 字的单句丢弃不丢整部；超 k_MaxLines 截断。
        /// </summary>
        public static List<TheaterScript> ParseBatch(string jsonl, out int skipped)
        {
            var list = new List<TheaterScript>();
            skipped = 0;
            if (string.IsNullOrWhiteSpace(jsonl))
                return list;
            foreach (var raw in jsonl.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                var scene = JsonMini.GetStr(line, "scene");
                var cast = JsonMini.GetInt(line, "cast");
                if (scene == null || !IsScene(scene) || cast == null || cast.Value < k_MinCast || cast.Value > k_MaxCast)
                {
                    skipped++;
                    continue;
                }
                var script = new TheaterScript { Scene = scene!, Cast = cast!.Value };
                ParseLines(line, script);
                if (script.Lines.Count < k_MinLines)
                {
                    skipped++;
                    continue;
                }
                list.Add(script);
            }
            return list;
        }

        /// <summary>场景标签白名单校验（schema 的 scene 字段只认 Scenes 四值）。</summary>
        private static bool IsScene(string s)
        {
            for (int i = 0; i < Scenes.Length; i++)
                if (Scenes[i] == s)
                    return true;
            return false;
        }

        /// <summary>
        /// 台词数组解析：定位 "lines" 的 [...] 区间，逐个 {...} 对象取 speaker/text。
        /// 区间/对象边界按首个 ']'/'}' 切（JsonMini 同款启发式——台词含 ']'/'}' 字符会切歪，
        /// 后果只是该部句数变少或整行落 skipped，salvage 纪律可接受）。
        /// </summary>
        private static void ParseLines(string json, TheaterScript dst)
        {
            var k = json.IndexOf("\"lines\"", StringComparison.Ordinal);
            if (k < 0)
                return;
            var lb = json.IndexOf('[', k);
            var rb = lb >= 0 ? json.IndexOf(']', lb) : -1;
            if (lb < 0 || rb <= lb)
                return;
            var i = lb + 1;
            while (i < rb && dst.Lines.Count < k_MaxLines)
            {
                var ob = json.IndexOf('{', i);
                if (ob < 0 || ob >= rb)
                    break;
                var cb = json.IndexOf('}', ob);
                if (cb < 0 || cb > rb)
                    break; // 对象未闭合/越界——数组畸形，取已解析到的
                var obj = json.Substring(ob, cb - ob + 1);
                var speaker = JsonMini.GetInt(obj, "speaker");
                var text = JsonMini.GetStr(obj, "text");
                if (speaker != null && speaker.Value >= 1 && speaker.Value <= dst.Cast
                    && !string.IsNullOrWhiteSpace(text))
                {
                    text = text!.Trim();
                    if (text.Length <= k_MaxTextLen)
                        dst.Lines.Add((speaker.Value - 1, text)); // 转 0 基（Theater.Script 同口径）
                }
                i = cb + 1;
            }
        }
    }
}
