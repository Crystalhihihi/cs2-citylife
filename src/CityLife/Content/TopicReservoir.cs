using System;
using System.Collections.Generic;
using System.IO;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>话题来源：内置=默认货架；社区=玩家 topics.jsonl；生成=话题创建炉（S3，未到）产出。</summary>
    public enum TopicSource
    {
        Builtin,
        Community,
        Generated,
    }

    /// <summary>
    /// 话题条目：Zone=分区（美食/通勤/…，一帖一题分区制的粗筛维度）；Topic=题面；
    /// Tags=场合标签（可空，给后续"场合→话题"匹配留口，§12 #48）；
    /// BornCycle=生成源出生炉次（新鲜度衰减的年龄基准；内置/社区恒 0，不衰减）。
    /// </summary>
    public sealed class TopicEntry
    {
        public string Zone = "";
        public string Topic = "";
        public string[] Tags = Array.Empty<string>();
        public TopicSource Source;
        public uint BornCycle;
    }

    /// <summary>
    /// 话题库（设计文档 §12 #48 社区拓展段）：Daily 炉每席位抽题的唯一货架。
    /// 纯数据模块，不碰游戏 API；装载点在 ContentDirectorSystem.OnCreate（与风格卡册同处同款 cfgDir 推导）。
    ///
    /// 如何扩展（社区贡献点）：
    ///   把 topics.jsonl 放进 ModsSettings/CityLife/ 即可加话题，一行一条：
    ///     {"zone":"美食","topic":"夜宵哪家强","tags":["夜"]}   // zone/topic 必填，tags 可空
    ///   默认**追加**进内置池；文件第一条有效行（非空非注释）是 {"mode":"replace"} 则整池替换内置
    ///   ——社区"完整话题包"的口子。缺文件=纯内置；非法行跳过，加载日志一行汇总警告数，不刷屏。
    ///
    /// 抽样语义（TopicFor）：纯确定性——同 batch+slot+池状态必同结果。
    ///   先按 (batch+slot) 在池内分区上轮转（一帖一题分区制，2026-08-20 玩家定案），
    ///   再在区内按新鲜度加权抽：内置/社区权重恒 1；生成源权重 = 0.5^(炉龄/半衰期8炉)、下限 0.05
    ///   ——越新越易被抽中，货架常新而非"说完"（#48 话题创建炉的出水口）。
    ///
    /// 容量：上限 MaxCapacity（默认 200），超出先逐出最旧的**生成**源条目；内置/社区永不逐出。
    ///   生成源条目由 S3 话题创建炉经 AddGenerated 入库，炉节拍推进 CurrentCycle（衰减基准）。
    /// </summary>
    public sealed class TopicReservoir
    {
        private const int k_HalfLifeCycles = 8;   // 生成话题新鲜度半衰期（炉次）：每过 8 炉权重减半
        private const double k_MinWeight = 0.05;  // 生成话题权重下限：再老也留一口被抽中的机会，直到被容量逐出

        private readonly List<TopicEntry> m_Entries = new();

        /// <summary>库容上限：超出先逐出最旧的生成源条目（内置/社区永不逐出）。</summary>
        public int MaxCapacity = 200;

        /// <summary>当前炉次（S3 话题创建炉推进；生成话题衰减的年龄基准）。未接 S3 前恒 0，权重全 1，行为等同均匀抽。</summary>
        public uint CurrentCycle;

        /// <summary>池内全部条目（只读视图，诊断/调试/S3 水位判断用）。</summary>
        public IReadOnlyList<TopicEntry> Entries => m_Entries;

        // 内置话题池（自 PromptBuilder.k_DailyZones 原样迁入，一座不动——默认货架）：
        // 8 分区 28 题，JSONL 与社区文件同 schema，走同一条解析路径
        private static readonly string[] k_Builtin =
        {
            "{\"zone\":\"美食\",\"topic\":\"一日三餐吃什么\"}",
            "{\"zone\":\"美食\",\"topic\":\"夜宵哪家强\"}",
            "{\"zone\":\"美食\",\"topic\":\"楼下新店的尝鲜报告\"}",
            "{\"zone\":\"美食\",\"topic\":\"外卖红包又没了\"}",
            "{\"zone\":\"通勤\",\"topic\":\"通勤路上那些事\"}",
            "{\"zone\":\"通勤\",\"topic\":\"停车又绕了三圈\"}",
            "{\"zone\":\"通勤\",\"topic\":\"公交挤成相片\"}",
            "{\"zone\":\"通勤\",\"topic\":\"油价/电费又动了\"}",
            "{\"zone\":\"职场\",\"topic\":\"加班那点事\"}",
            "{\"zone\":\"职场\",\"topic\":\"发工资前后的日子\"}",
            "{\"zone\":\"职场\",\"topic\":\"办公室八卦\"}",
            "{\"zone\":\"职场\",\"topic\":\"摸鱼心得\"}",
            "{\"zone\":\"家里\",\"topic\":\"家里长短\"}",
            "{\"zone\":\"家里\",\"topic\":\"娃的作业/学校\"}",
            "{\"zone\":\"家里\",\"topic\":\"楼上装修的电钻声\"}",
            "{\"zone\":\"家里\",\"topic\":\"小区快递柜又满了\"}",
            "{\"zone\":\"萌宠\",\"topic\":\"家里的猫/狗今天又干了什么\"}",
            "{\"zone\":\"萌宠\",\"topic\":\"楼下那只流浪猫\"}",
            "{\"zone\":\"消费\",\"topic\":\"快递又卡半路\"}",
            "{\"zone\":\"消费\",\"topic\":\"最近买的好东西/踩的坑\"}",
            "{\"zone\":\"消费\",\"topic\":\"换季添件衣服\"}",
            "{\"zone\":\"娱乐\",\"topic\":\"最近在追的剧或玩的游戏\"}",
            "{\"zone\":\"娱乐\",\"topic\":\"周末打算怎么过\"}",
            "{\"zone\":\"娱乐\",\"topic\":\"阳台种点什么好\"}",
            "{\"zone\":\"沙雕\",\"topic\":\"今天的糗事\"}",
            "{\"zone\":\"沙雕\",\"topic\":\"随手拍的离谱一幕\"}",
            "{\"zone\":\"沙雕\",\"topic\":\"隔壁邻居的八卦\"}",
            "{\"zone\":\"沙雕\",\"topic\":\"健身房办卡纠结\"}",
        };

        /// <summary>
        /// 装载话题库：内置 + 社区 topics.jsonl（默认追加；首行 mode:replace 整池替换）。
        /// 任何失败（缺文件/读取异常/replace 出空池）都退回内置兜底，不致命。
        /// </summary>
        public static TopicReservoir Load(string? overridePath, Action<string> log)
        {
            var r = new TopicReservoir();
            var builtin = Parse(string.Join("\n", k_Builtin), TopicSource.Builtin, out _);
            r.m_Entries.AddRange(builtin);
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            {
                try
                {
                    var text = File.ReadAllText(overridePath);
                    var custom = Parse(text, TopicSource.Community, out var skipped);
                    if (IsReplaceMode(text))
                    {
                        if (custom.Count > 0)
                        {
                            r.m_Entries.Clear();
                            r.m_Entries.AddRange(custom);
                            log($"[Topic] 话题库：replace 模式——社区整包 {custom.Count} 题替换内置（非法行跳过 {skipped} 条）");
                        }
                        else
                        {
                            log($"[Topic] 话题库：replace 模式但社区包 0 条有效——保留内置 {builtin.Count} 题兜底（非法行跳过 {skipped} 条）");
                        }
                    }
                    else
                    {
                        r.m_Entries.AddRange(custom);
                        log($"[Topic] 话题库：内置 {builtin.Count} 题 + 社区追加 {custom.Count} 题（非法行跳过 {skipped} 条）");
                    }
                }
                catch (Exception e)
                {
                    log($"[Topic] 话题库：社区文件读取失败，内置 {builtin.Count} 题兜底：{e.Message}");
                }
            }
            else
            {
                log($"[Topic] 话题库：内置 {builtin.Count} 题（无社区 topics.jsonl）");
            }
            return r;
        }

        /// <summary>
        /// 每席位抽题（替代原 PromptBuilder.DailyTopicFor）：确定性——同 batch+slot+池状态同结果。
        /// 先分区轮转（一帖一题分区制），再区内按新鲜度加权抽（生成源越新越易被抽中）。
        /// </summary>
        public string TopicFor(uint batch, int slot)
        {
            if (m_Entries.Count == 0)
                return "生活闲聊"; // 理论到不了（装载有内置兜底），纯防御

            // 分区轮转：池内分区按首现序去重（社区新分区自动进轮转）
            var zones = new List<string>(8);
            foreach (var e in m_Entries)
                if (!zones.Contains(e.Zone))
                    zones.Add(e.Zone);
            var zone = zones[(int)((batch + (uint)slot) % (uint)zones.Count)];

            // 区内新鲜度加权抽样：r 落在累计权重的哪一段就抽哪题
            var total = 0.0;
            foreach (var e in m_Entries)
                if (e.Zone == zone)
                    total += WeightOf(e);
            var r = Hash01(batch, slot) * total;
            var acc = 0.0;
            string? chosen = null;
            foreach (var e in m_Entries)
            {
                if (e.Zone != zone) continue;
                chosen = e.Topic;
                acc += WeightOf(e);
                if (r < acc) break;
            }
            return chosen ?? "生活闲聊"; // 浮点尾巴兜底：返回区内最后一题
        }

        /// <summary>
        /// 生成源条目入库（S3 话题创建炉的产出口，先备好）：BornCycle 记当前炉次；
        /// 入库后超库容先逐出最旧的生成源条目。
        /// </summary>
        public void AddGenerated(string zone, string topic, string[]? tags = null)
        {
            if (string.IsNullOrEmpty(zone) || string.IsNullOrEmpty(topic))
                return;
            m_Entries.Add(new TopicEntry
            {
                Zone = zone,
                Topic = topic,
                Tags = tags ?? Array.Empty<string>(),
                Source = TopicSource.Generated,
                BornCycle = CurrentCycle,
            });
            EvictOverflow();
        }

        /// <summary>超容逐出：只逐生成源里最旧的（BornCycle 最小）；池里全是内置/社区则允许超容——默认货架永不丢。</summary>
        private void EvictOverflow()
        {
            while (m_Entries.Count > MaxCapacity)
            {
                var oldest = -1;
                for (int i = 0; i < m_Entries.Count; i++)
                {
                    if (m_Entries[i].Source != TopicSource.Generated) continue;
                    if (oldest < 0 || m_Entries[i].BornCycle < m_Entries[oldest].BornCycle)
                        oldest = i;
                }
                if (oldest < 0) break;
                m_Entries.RemoveAt(oldest);
            }
        }

        /// <summary>条目权重：内置/社区恒 1；生成源按炉龄指数衰减（半衰期 k_HalfLifeCycles 炉，下限 k_MinWeight）。</summary>
        private double WeightOf(TopicEntry e)
        {
            if (e.Source != TopicSource.Generated)
                return 1.0;
            var age = CurrentCycle >= e.BornCycle ? CurrentCycle - e.BornCycle : 0u;
            var w = Math.Pow(0.5, age / (double)k_HalfLifeCycles);
            return w < k_MinWeight ? k_MinWeight : w;
        }

        /// <summary>batch+slot → [0,1) 确定性哈希（整数混合，不依赖 Random——种子语义的锚）。</summary>
        private static double Hash01(uint batch, int slot)
        {
            var h = batch * 0x9E3779B1u + (uint)(slot + 1) * 0x85EBCA77u;
            h ^= h >> 13;
            h *= 0x5BD1E995u;
            h ^= h >> 15;
            return (h >> 8) * (1.0 / 16777216.0); // 取高 24 位归一
        }

        /// <summary>replace 判定：文件第一条有效行（非空非注释）是 {"mode":"replace"}。</summary>
        private static bool IsReplaceMode(string jsonl)
        {
            foreach (var raw in jsonl.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                return JsonMini.GetStr(line, "mode") == "replace";
            }
            return false;
        }

        /// <summary>解析 JSONL：缺 zone/topic 的行跳过并计入 skipped（由 Load 一行汇总，不刷屏）。</summary>
        private static List<TopicEntry> Parse(string jsonl, TopicSource source, out int skipped)
        {
            var list = new List<TopicEntry>();
            skipped = 0;
            foreach (var raw in jsonl.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                if (JsonMini.GetStr(line, "mode") != null)
                    continue; // 模式指令行，不是话题
                var zone = JsonMini.GetStr(line, "zone");
                var topic = JsonMini.GetStr(line, "topic");
                if (string.IsNullOrEmpty(zone) || string.IsNullOrEmpty(topic))
                {
                    skipped++;
                    continue;
                }
                list.Add(new TopicEntry
                {
                    Zone = zone!,
                    Topic = topic!,
                    Tags = ParseTags(line),
                    Source = source,
                    BornCycle = 0,
                });
            }
            return list;
        }

        /// <summary>解析 "tags":["a","b"] 字符串数组（走引号用 JsonMini.ParseString，逗号/转义安全）；无 tags 返回空数组。</summary>
        private static string[] ParseTags(string line)
        {
            var k = line.IndexOf("\"tags\"", StringComparison.Ordinal);
            if (k < 0) return Array.Empty<string>();
            var lb = line.IndexOf('[', k);
            var rb = lb >= 0 ? line.IndexOf(']', lb) : -1;
            if (lb < 0 || rb <= lb) return Array.Empty<string>();
            var tags = new List<string>();
            var i = lb + 1;
            while (i < rb)
            {
                if (line[i] == '"')
                {
                    var t = JsonMini.ParseString(line, i, out var end);
                    if (t == null || end > rb) break; // 未闭合/越界——数组畸形，取已解析到的
                    if (t.Length > 0) tags.Add(t);
                    i = end;
                }
                else
                {
                    i++;
                }
            }
            return tags.ToArray();
        }
    }
}
