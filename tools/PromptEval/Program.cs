// PromptEval —— prompt 优化专项离线评测 harness（§12 #60 刀⓪）
// 目的：替代"实机看感觉"——固定快照+处境卡具，走真实 PromptBuilder 组装、真实 provider 发 K 炉，
//       用确定性指标打分：猫密度/场合分布(Any率)/句长方差/近重复率/分区熵。
//       改动 prompt 前后各跑一轮对比报告（logs/prompt-eval/ 下留档，含原始输出供盲评）。
// 用法（仓库根目录）：
//   dotnet run --project tools/PromptEval -- [--scenario chatter|topics|all] [--k N]
//        [--set collapsed|diverse|both] [--count 30] [--config llm.json路径]
//        [--model 覆盖] [--thinking disabled|""] [--out 报告目录]
// 纪律：API key 只从 llm.json 读入内存，绝不打印不落盘；报告只记供给名/模型名/thinking 状态。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CityLife.Content;
using CityLife.Llm;
using CityLife.Util;

internal static class Program
{
    // 固定城市快照（夜晚 20 点晴夏——复刻 2026-09-11 实机塌缩现场的时间带）
    private static readonly CitySnapshot k_Snap = new CitySnapshot
    {
        Citizens = 42000, Households = 18000, Tourists = 1200,
        Happiness = 58f, UnemploymentPercent = 7f, HomelessPercent = 1f,
        IsRaining = false, IsSnowing = false, Temperature = 27f, SeasonName = "夏", HourOfDay = 20,
    };

    // 塌缩组：照抄实机日志分布（夜晚住宅区，退休/呆着占绝对大头）——复现"10句4猫"的输入条件
    private static readonly string[] k_Collapsed =
    {
        "手头紧的退休大妈，在住宅区呆着",
        "退休大爷，在住宅区呆着",
        "手头紧的退休大妈，在住宅区呆着",
        "家庭主妇，在住宅区呆着",
        "退休大爷，在公园里溜达",
        "手头紧的退休大妈，在住宅区呆着",
        "无业青年，在住宅区呆着",
        "退休大妈，在商店里逛",
    };

    // 多样组：分层配额落地后的目标分布（途中/室内/路上/载具/游客俱全）——对照组
    private static readonly string[] k_Diverse =
    {
        "上班族，坐公交下班回家路上（去住宅区）",
        "学生，打车上学路上（去学校）",
        "手头紧的上班族，开私家车上班路上（去工业区）",
        "游客，在公园里溜达",
        "退休大爷，在公园里溜达",
        "上班族，在办公室里摸鱼",
        "货车司机，开货车送货路上（去商业区）",
        "青年，走路去商店路上",
    };

    // 猫灾关键词（中文语料最安全万能题材，密度=卡片信息熵的晴雨表）
    private static readonly string[] k_CatWords = { "猫", "狗", "宠物", "喵", "汪" };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var scenario = ArgOf(args, "--scenario", "all");
        var k = int.Parse(ArgOf(args, "--k", "2"));
        var setSel = ArgOf(args, "--set", "both");
        var topicCount = int.Parse(ArgOf(args, "--count", "30"));
        var cfgPath = ArgOf(args, "--config", DefaultConfigPath());
        var modelOverride = ArgOfOpt(args, "--model");
        var thinkingOverride = ArgOfOpt(args, "--thinking");
        var outDir = ArgOf(args, "--out", Path.Combine("logs", "prompt-eval"));

        // 供给装配：复刻 Mod 的选择逻辑（openai-compatible / kimi-cli 两轨）
        var cfg = ProviderConfig.Load(cfgPath, m => Console.WriteLine(m));
        ICliProvider provider = cfg.Provider == "openai-compatible"
            ? new OpenAiCompatibleProvider(cfg.BaseUrl, cfg.ApiKey,
                                           modelOverride ?? cfg.Model,
                                           thinkingOverride ?? cfg.Thinking)
            : new KimiCliProvider();
        if (cfg.Provider == "openai-compatible" && (cfg.BaseUrl.Length == 0 || cfg.ApiKey.Length == 0))
        {
            Console.WriteLine("[Eval] 供给配置缺 baseUrl/apiKey——检查 " + cfgPath);
            return 2;
        }
        if (!provider.IsAvailable())
        {
            Console.WriteLine("[Eval] 供给不可用：" + provider.Name);
            return 2;
        }
        OpenAiCompatibleProvider.Log = m => Console.WriteLine(m);
        Console.WriteLine($"[Eval] 供给={provider.Name} 并发={provider.MaxConcurrency} 场景={scenario} 每具K={k}");

        var reservoir = TopicReservoir.Load(null, m => Console.WriteLine(m));
        var report = new StringBuilder(4096);
        report.Append("# PromptEval 报告 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n\n");
        report.Append("- 供给：").Append(provider.Name)
              .Append(" / 模型：").Append(modelOverride ?? cfg.Model)
              .Append(" / thinking：").Append(thinkingOverride ?? cfg.Thinking ?? "").Append('\n');
        report.Append("- 场景：").Append(scenario).Append("，每具 K=").Append(k).Append("\n\n");

        var exit = 0;
        if (scenario is "chatter" or "all")
        {
            if (setSel is "collapsed" or "both")
                exit |= await RunChatter(provider, reservoir, "collapsed", k_Collapsed, 100u, k, report);
            if (setSel is "diverse" or "both")
                exit |= await RunChatter(provider, reservoir, "diverse", k_Diverse, 200u, k, report);
        }
        if (scenario is "topics" or "all")
            exit |= await RunTopics(provider, reservoir, topicCount, k, report);

        Directory.CreateDirectory(outDir);
        var file = Path.Combine(outDir, "eval-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
        File.WriteAllText(file, report.ToString());
        Console.WriteLine("[Eval] 报告已写：" + file);
        return exit;
    }

    /// <summary>闲聊炉场景：真实 BuildChatterPrompt 发 K 炉，ParseBatch 解析后打分。</summary>
    private static async Task<int> RunChatter(ICliProvider provider, TopicReservoir reservoir,
                                              string setName, string[] cards, uint batchBase, int k,
                                              StringBuilder report)
    {
        var head = PromptBuilder.BuildChatterHead();
        var all = new List<(string Text, BubbleOccasion Occasion)>();
        var raws = new List<(int Batch, string Raw, long Ms, int? Pt, int? Rt)>();
        var totalSkipped = 0;
        for (var b = 0; b < k; b++)
        {
            // 每卡配一题：真实 TopicFor 确定性抽题（与实机同一代码路径）
            var topics = cards.Select((_, i) => reservoir.TopicFor(batchBase + (uint)b, i)).ToArray();
            var prompt = PromptBuilder.BuildChatterPrompt(head, k_Snap, cards, topics);
            Console.WriteLine($"[Eval·chatter/{setName}] 第 {b + 1}/{k} 炉发出（{prompt.Length} 字符）…");
            var r = await provider.OneShotAsync(prompt, CancellationToken.None);
            if (!r.Success)
            {
                Console.WriteLine($"[Eval·chatter/{setName}] 第 {b + 1} 炉失败：{r.Error}");
                return 1;
            }
            var parsed = BubbleSnippetPool.ParseBatch(r.Text, out var skipped);
            totalSkipped += skipped;
            all.AddRange(parsed);
            raws.Add((b, r.Text, r.LatencyMs, r.PromptTokens, r.ResponseTokens));
            Console.WriteLine($"[Eval·chatter/{setName}] 第 {b + 1} 炉：{parsed.Count} 条（丢 {skipped}）{r.LatencyMs / 1000.0:0.0}s");
        }

        // —— 指标 ——
        var n = Math.Max(1, all.Count);
        var catHits = all.Count(e => k_CatWords.Any(w => e.Text.Contains(w)));
        var occ = all.GroupBy(e => e.Occasion).ToDictionary(g => g.Key, g => g.Count());
        var lens = all.Select(e => e.Text.Length).ToArray();
        var (mean, std) = MeanStd(lens);
        var (dupPairs, dupSample) = NearDup(all.Select(e => e.Text).ToArray());

        Console.WriteLine($"\n== chatter/{setName} 汇总（{all.Count} 条，解析丢 {totalSkipped}）==");
        Console.WriteLine($"  猫密度     : {catHits}/{all.Count} = {Pct(catHits, n)}");
        Console.WriteLine($"  场合分布   : {string.Join("  ", Enum.GetValues<BubbleOccasion>().Select(o => $"{o}={occ.GetValueOrDefault(o)}"))}  Any率={Pct(occ.GetValueOrDefault(BubbleOccasion.Any), n)}");
        Console.WriteLine($"  句长       : 均值 {mean:0.0} 字，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), n)}，>20字 {Pct(lens.Count(l => l > 20), n)}");
        Console.WriteLine($"  近重复     : {dupPairs} 对（Jaccard≥0.7）{(dupSample != null ? " 例：" + dupSample : "")}");

        report.Append("## chatter / ").Append(setName).Append("\n\n");
        report.Append("- 条数：").Append(all.Count).Append("（解析丢 ").Append(totalSkipped).Append("）\n");
        report.Append("- 猫密度：").Append(Pct(catHits, n)).Append($" ({catHits}/{all.Count})\n");
        report.Append("- 场合：").Append(string.Join("  ", Enum.GetValues<BubbleOccasion>().Select(o => $"{o}={occ.GetValueOrDefault(o)}"))).Append("\n");
        report.Append($"- 句长：均值 {mean:0.0}，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), n)}，>20字 {Pct(lens.Count(l => l > 20), n)}\n");
        report.Append("- 近重复：").Append(dupPairs).Append(" 对").Append(dupSample != null ? "（" + dupSample + "）" : "").Append("\n\n");
        foreach (var raw in raws)
        {
            report.Append("### 第 ").Append(raw.Batch + 1).Append(" 炉原始输出（")
                  .Append((raw.Ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append("s")
                  .Append(raw.Pt != null ? $"，prompt {raw.Pt} tok / resp {raw.Rt} tok" : "").Append("）\n\n```\n")
                  .Append(raw.Raw.Trim()).Append("\n```\n\n");
        }
        return 0;
    }

    /// <summary>话题炉场景：真实 BuildTopicForgePrompt 发 K 炉，按行解析 zone 打分（分区熵是"别扎堆"的量化）。</summary>
    private static async Task<int> RunTopics(ICliProvider provider, TopicReservoir reservoir,
                                             int count, int k, StringBuilder report)
    {
        var head = PromptBuilder.BuildTopicForgeHead();
        var existing = reservoir.SampleForPrompt(42, 20);
        var zones = new List<string>();
        var topics = new List<string>();
        var raws = new List<(int Batch, string Raw, long Ms)>();
        var skipped = 0;
        for (var b = 0; b < k; b++)
        {
            var prompt = PromptBuilder.BuildTopicForgePrompt(head, k_Snap, existing, count);
            Console.WriteLine($"[Eval·topics] 第 {b + 1}/{k} 炉发出（{prompt.Length} 字符）…");
            var r = await provider.OneShotAsync(prompt, CancellationToken.None);
            if (!r.Success)
            {
                Console.WriteLine($"[Eval·topics] 第 {b + 1} 炉失败：{r.Error}");
                return 1;
            }
            foreach (var raw in r.Text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var zone = JsonMini.GetStr(line, "zone");
                var topic = JsonMini.GetStr(line, "topic");
                if (zone == null || topic == null) { skipped++; continue; }
                zones.Add(zone);
                topics.Add(topic);
            }
            raws.Add((b, r.Text, r.LatencyMs));
            Console.WriteLine($"[Eval·topics] 第 {b + 1} 炉解析完（{r.LatencyMs / 1000.0:0.0}s）");
        }

        var n = Math.Max(1, topics.Count);
        var byZone = zones.GroupBy(z => z).OrderByDescending(g => g.Count()).ToArray();
        var entropy = byZone.Select(g => (double)g.Count() / n).Aggregate(0.0, (h, p) => h - p * Math.Log2(p));
        var catHits = topics.Count(t => k_CatWords.Any(w => t.Contains(w)));

        Console.WriteLine($"\n== topics 汇总（{topics.Count} 条，解析丢 {skipped}）==");
        Console.WriteLine($"  分区熵     : {entropy:0.00} bit（{byZone.Length} 个分区）");
        Console.WriteLine($"  分区分布   : {string.Join("  ", byZone.Select(g => $"{g.Key}={g.Count()}"))}");
        Console.WriteLine($"  猫密度     : {catHits}/{topics.Count} = {Pct(catHits, n)}");

        report.Append("## topics\n\n");
        report.Append("- 条数：").Append(topics.Count).Append("（解析丢 ").Append(skipped).Append("）\n");
        report.Append($"- 分区熵：{entropy:0.00} bit（{byZone.Length} 个分区）：")
              .Append(string.Join("  ", byZone.Select(g => $"{g.Key}={g.Count()}"))).Append("\n");
        report.Append("- 猫密度：").Append(Pct(catHits, n)).Append($" ({catHits}/{topics.Count})\n\n");
        foreach (var raw in raws)
            report.Append("### 第 ").Append(raw.Batch + 1).Append(" 炉原始输出（")
                  .Append((raw.Ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append("s）\n\n```\n")
                  .Append(raw.Raw.Trim()).Append("\n```\n\n");
        return 0;
    }

    /// <summary>近重复对：去标点空白后按字符 bigram Jaccard ≥0.7 判重，返回对数与首个样例。</summary>
    private static (int Pairs, string? Sample) NearDup(string[] lines)
    {
        var grams = lines.Select(Bigrams).ToArray();
        var pairs = 0;
        string? sample = null;
        for (var i = 0; i < lines.Length; i++)
        for (var j = i + 1; j < lines.Length; j++)
        {
            var a = grams[i]; var b = grams[j];
            if (a.Count == 0 || b.Count == 0) continue;
            var inter = a.Intersect(b).Count();
            var jac = (double)inter / (a.Count + b.Count - inter);
            if (jac >= 0.7)
            {
                pairs++;
                sample ??= $"「{lines[i]}」≈「{lines[j]}」";
            }
        }
        return (pairs, sample);
    }

    private static HashSet<string> Bigrams(string s)
    {
        var clean = new string(s.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray());
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < clean.Length; i++)
            set.Add(clean.Substring(i, 2));
        return set;
    }

    private static (double Mean, double Std) MeanStd(int[] xs)
    {
        if (xs.Length == 0) return (0, 0);
        var mean = xs.Average();
        return (mean, Math.Sqrt(xs.Average(x => (x - mean) * (x - mean))));
    }

    private static string Pct(int part, int whole) => (100.0 * part / Math.Max(1, whole)).ToString("0") + "%";

    private static string ArgOf(string[] args, string key, string fallback)
        => ArgOfOpt(args, key) ?? fallback;

    private static string? ArgOfOpt(string[] args, string key)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == key)
                return args[i + 1];
        return null;
    }

    /// <summary>默认配置路径：游戏用户目录 ModsSettings/CityLife/llm.json（LocalLow，net8 无直接 KnownFolder 故从 Local 上跳一层）。</summary>
    private static string DefaultConfigPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "..", "LocalLow", "Colossal Order", "Cities Skylines II",
                        "ModsSettings", "CityLife", "llm.json");
}
