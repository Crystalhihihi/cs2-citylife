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
    // §12 #63：带"｜对："段的是双人卡（执行层拼好的生产形态——配对只发生在一开始的 Walk 卡上）
    private static readonly string[] k_Collapsed =
    {
        "手头紧的退休大妈，在住宅区呆着",
        "退休大爷，在住宅区呆着",
        "手头紧的退休大妈，在住宅区呆着",
        "家庭主妇，在住宅区呆着",
        "退休大爷，在公园里溜达｜对：退休大妈，在公园里跳广场舞",
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
        "游客，在公园里溜达｜对：游客，在公园里拍照",
        "退休大爷，在公园里溜达",
        "上班族，在办公室里摸鱼",
        "货车司机，开货车送货路上（去商业区）",
        "青年，走路去商店路上｜对：上班族，走路去地铁站路上",
    };

    // 双人卡真值（与卡具对齐；生产侧=BubbleChatterSystem.m_CurrentPairs 的 null/非空）
    private static readonly bool[] k_CollapsedPair =
    {
        false, false, false, false, true, false, false, false,
    };

    private static readonly bool[] k_DiversePair =
    {
        false, false, false, true, false, false, false, true,
    };

    // 双人卡拼装假名（与卡具对齐——模拟收炉时 m_CurrentPairs 里的 (nameA,nameB)，验证 40 硬顶防线）
    private static readonly (string A, string B)[] k_CollapsedPairNames =
    {
        ("", ""), ("", ""), ("", ""), ("", ""), ("王建国", "李秀兰"), ("", ""), ("", ""), ("", ""),
    };

    private static readonly (string A, string B)[] k_DiversePairNames =
    {
        ("", ""), ("", ""), ("", ""), ("小林", "周敏"), ("", ""), ("", ""), ("", ""), ("陈晨", "赵一鸣"),
    };

    // 具卡的场合真值（§12 #60 刀①后实机场合由 CitizenPoolSystem 盖章；具在此模拟盖章结果，验证 card→场合映射）
    private static readonly BubbleOccasion[] k_CollapsedOcc =
    {
        BubbleOccasion.Indoor, BubbleOccasion.Indoor, BubbleOccasion.Indoor, BubbleOccasion.Indoor,
        BubbleOccasion.Walk, BubbleOccasion.Indoor, BubbleOccasion.Indoor, BubbleOccasion.Indoor,
    };

    private static readonly BubbleOccasion[] k_DiverseOcc =
    {
        BubbleOccasion.Vehicle, BubbleOccasion.Vehicle, BubbleOccasion.Vehicle, BubbleOccasion.Walk,
        BubbleOccasion.Walk, BubbleOccasion.Indoor, BubbleOccasion.Vehicle, BubbleOccasion.Walk,
    };

    // 城市记忆具（§12 #60 刀②）：与实机同形的传闻榜样例，让评测 prompt 与生产 prompt 同结构
    private static readonly string[] k_Rumors =
    {
        "城东北路口发生车祸",
        "新修了 3 段路，路网还在长",
        "市民联署要求回应失业率高、工作难找的问题",
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
                exit |= await RunChatter(provider, reservoir, "collapsed", k_Collapsed, k_CollapsedOcc, k_CollapsedPair, k_CollapsedPairNames, 100u, k, report);
            if (setSel is "diverse" or "both")
                exit |= await RunChatter(provider, reservoir, "diverse", k_Diverse, k_DiverseOcc, k_DiversePair, k_DiversePairNames, 200u, k, report);
        }
        if (scenario is "topics" or "all")
            exit |= await RunTopics(provider, reservoir, topicCount, k, report);
        if (scenario is "theater" or "all")
            exit |= await RunTheater(provider, k, report);

        Directory.CreateDirectory(outDir);
        var file = Path.Combine(outDir, "eval-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
        File.WriteAllText(file, report.ToString());
        Console.WriteLine("[Eval] 报告已写：" + file);
        return exit;
    }

    /// <summary>闲聊炉场景：真实 BuildChatterPrompt 发 K 炉，ParseBatch 解析后打分。
    /// occs=具卡场合真值（§12 #60 刀①后实机由 CitizenPoolSystem 盖章）——按 card 号映射验证归属质量。
    /// pairs/pairNames=双人卡真值与拼装假名（§12 #63：模拟执行层 m_CurrentPairs，验证对话行产出与 40 硬顶拼装防线）。</summary>
    private static async Task<int> RunChatter(ICliProvider provider, TopicReservoir reservoir,
                                              string setName, string[] cards, BubbleOccasion[] occs,
                                              bool[] pairs, (string A, string B)[] pairNames,
                                              uint batchBase, int k, StringBuilder report)
    {
        var head = PromptBuilder.BuildChatterHead();
        var all = new List<ParsedChatterLine>();
        var raws = new List<(int Batch, string Raw, long Ms, int? Pt, int? Rt)>();
        var totalSkipped = 0;
        var pairCount = pairs.Count(p => p);
        // 双人卡号列表（1 起）——与生产侧 BubbleChatterSystem 同名锚定口径，prompt 段头/任务行点名用
        var pairCardNos = pairs.Select((p, i) => (p, i)).Where(x => x.p).Select(x => x.i + 1).ToArray();
        for (var b = 0; b < k; b++)
        {
            // 每卡配一题：真实 TopicFor 确定性抽题（与实机同一代码路径）
            var topics = cards.Select((_, i) => reservoir.TopicFor(batchBase + (uint)b, i)).ToArray();
            var prompt = PromptBuilder.BuildChatterPrompt(head, k_Snap, cards, topics, k_Rumors, pairCardNos);
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
        // 猫密度/句长的文本口径：独白条=Text，对话条=A+B（台词本身，不含执行层名字前缀）
        string BodyOf(ParsedChatterLine e) => e.IsDialogue ? e.A! + e.B! : e.Text ?? "";
        var catHits = all.Count(e => k_CatWords.Any(w => BodyOf(e).Contains(w)));
        // card→场合映射（模拟实机盖章回填）；卡号缺失/越界=无归属落 Any
        var occ = new Dictionary<BubbleOccasion, int>();
        var orphan = 0;
        var perCard = new int[cards.Length];
        foreach (var e in all)
        {
            if (e.Card >= 1 && e.Card <= cards.Length)
            {
                var o = occs[e.Card - 1];
                occ[o] = occ.GetValueOrDefault(o) + 1;
                perCard[e.Card - 1]++;
            }
            else
            {
                occ[BubbleOccasion.Any] = occ.GetValueOrDefault(BubbleOccasion.Any) + 1;
                orphan++;
            }
        }
        var monologues = all.Where(e => !e.IsDialogue).ToArray();
        var dialogues = all.Where(e => e.IsDialogue).ToArray();
        var lens = monologues.Select(e => e.Text!.Length).ToArray();
        var (mean, std) = MeanStd(lens.Length > 0 ? lens : new[] { 0 });
        var (dupPairs, dupSample) = NearDup(all.Select(BodyOf).ToArray());
        var zeroCards = perCard.Count(c => c == 0);

        // §12 #63 对话指标：齐全率（对话条数 / 对卡数×K——双人卡每张应只产 1 条）、a/b 句长、拼装防线
        var aLens = dialogues.Select(e => e.A!.Length).ToArray();
        var bLens = dialogues.Select(e => e.B!.Length).ToArray();
        var over12 = dialogues.Count(e => e.A!.Length > 12 || e.B!.Length > 12); // ParseBatch 已拦，应恒 0
        var composed = 0; var composedOver = 0;
        foreach (var e in dialogues)
        {
            if (e.Card >= 1 && e.Card <= cards.Length && pairs[e.Card - 1])
            {
                var (na, nb) = pairNames[e.Card - 1];
                var text = na + "：" + e.A + "\n" + nb + "：" + e.B; // 模拟生产 ComposeDialogue
                composed++;
                if (text.Length > 40)
                    composedOver++;
            }
        }
        var pairExpected = Math.Max(1, pairCount * k);

        Console.WriteLine($"\n== chatter/{setName} 汇总（{all.Count} 条，解析丢 {totalSkipped}）==");
        Console.WriteLine($"  猫密度     : {catHits}/{all.Count} = {Pct(catHits, n)}");
        Console.WriteLine($"  场合映射   : {string.Join("  ", Enum.GetValues<BubbleOccasion>().Select(o => $"{o}={occ.GetValueOrDefault(o)}"))}  无归属={Pct(orphan, n)}");
        Console.WriteLine($"  句长(独白) : 均值 {mean:0.0} 字，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), Math.Max(1, lens.Length))}，>20字 {Pct(lens.Count(l => l > 20), Math.Max(1, lens.Length))}");
        Console.WriteLine($"  卡覆盖     : 空卡 {zeroCards}/{cards.Length}，每卡每炉 {perCard.Min() / (double)Math.Max(1, k):0.0}-{perCard.Max() / (double)Math.Max(1, k):0.0} 条（独白卡应 2-3，双人卡应 1）");
        Console.WriteLine($"  对话       : {dialogues.Length} 条（双人卡 {pairCount} 张×K{k}，齐全率 {Pct(dialogues.Length, pairExpected)}），a/b 句长 {(aLens.Length > 0 ? aLens.Average() : 0):0.0}/{(bLens.Length > 0 ? bLens.Average() : 0):0.0} 字，>12字 {over12} 条，拼装>40字 {composedOver}/{composed}");
        Console.WriteLine($"  近重复     : {dupPairs} 对（Jaccard≥0.7）{(dupSample != null ? " 例：" + dupSample : "")}");

        report.Append("## chatter / ").Append(setName).Append("\n\n");
        report.Append("- 条数：").Append(all.Count).Append("（解析丢 ").Append(totalSkipped).Append("）\n");
        report.Append("- 猫密度：").Append(Pct(catHits, n)).Append($" ({catHits}/{all.Count})\n");
        report.Append("- 场合映射：").Append(string.Join("  ", Enum.GetValues<BubbleOccasion>().Select(o => $"{o}={occ.GetValueOrDefault(o)}"))).Append($"，无归属 {Pct(orphan, n)}\n");
        report.Append($"- 句长(独白)：均值 {mean:0.0}，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), Math.Max(1, lens.Length))}，>20字 {Pct(lens.Count(l => l > 20), Math.Max(1, lens.Length))}\n");
        report.Append($"- 卡覆盖：空卡 {zeroCards}/{cards.Length}，每卡每炉 {perCard.Min() / (double)Math.Max(1, k):0.0}-{perCard.Max() / (double)Math.Max(1, k):0.0} 条\n");
        report.Append($"- 对话：{dialogues.Length} 条（双人卡 {pairCount}×K{k}，齐全率 {Pct(dialogues.Length, pairExpected)}），a/b 句长 {(aLens.Length > 0 ? aLens.Average() : 0):0.0}/{(bLens.Length > 0 ? bLens.Average() : 0):0.0}，>12字 {over12} 条，拼装>40字 {composedOver}/{composed}\n");
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

    /// <summary>剧场剧本炉场景（§12 #60 刀⑥槽位化评测）：真实 BuildTheaterStockPrompt 发 K 炉，
    /// 真实 TheaterScriptStock.ParseBatch 解析——schema 合规率 + 占位符使用率/有效率是刀⑥的核心指标。</summary>
    private static async Task<int> RunTheater(ICliProvider provider, int k, StringBuilder report)
    {
        var head = PromptBuilder.BuildTheaterStockHead();
        var stock = new TheaterScriptStock(); // 空库存：与低水位开炉时同形（各场景现存 0 部）
        var scripts = new List<TheaterScript>();
        var raws = new List<(int Batch, string Raw, long Ms)>();
        var totalSkipped = 0;
        for (var b = 0; b < k; b++)
        {
            var prompt = PromptBuilder.BuildTheaterStockPrompt(head, k_Snap, stock, k_Diverse, 4, 3); // streetMaxCast=设置页默认 3（§12 #67）
            Console.WriteLine($"[Eval·theater] 第 {b + 1}/{k} 炉发出（{prompt.Length} 字符）…");
            var r = await provider.OneShotAsync(prompt, CancellationToken.None);
            if (!r.Success)
            {
                Console.WriteLine($"[Eval·theater] 第 {b + 1} 炉失败：{r.Error}");
                return 1;
            }
            var parsed = TheaterScriptStock.ParseBatch(r.Text, out var skipped);
            totalSkipped += skipped;
            scripts.AddRange(parsed);
            raws.Add((b, r.Text, r.LatencyMs));
            Console.WriteLine($"[Eval·theater] 第 {b + 1} 炉：{parsed.Count} 部（丢 {skipped}）{r.LatencyMs / 1000.0:0.0}s");
        }

        // —— 指标 ——
        var n = Math.Max(1, scripts.Count);
        var byScene = scripts.GroupBy(s => s.Scene).OrderByDescending(g => g.Count()).ToArray();
        var withSlot = scripts.Count(s => s.Lines.Any(l => l.Text.Contains("{place}") || l.Text.Contains("{name")));
        var slotBad = scripts.Count(s => s.Lines.Any(l => SlotInvalid(l.Text, s.Cast)));
        var linesPer = scripts.Select(s => (double)s.Lines.Count).ToArray();
        var slotUse = scripts.SelectMany(s => s.Lines).Count(l => l.Text.Contains("{place}") || RegexCount(l.Text, "{name") > 0);

        Console.WriteLine($"\n== theater 汇总（{scripts.Count} 部，解析丢 {totalSkipped}）==");
        Console.WriteLine($"  场景分布   : {string.Join("  ", byScene.Select(g => $"{g.Key}={g.Count()}"))}");
        Console.WriteLine($"  句数       : 均值 {linesPer.Average():0.0}（{linesPer.Min():0}-{linesPer.Max():0}）");
        Console.WriteLine($"  占位符     : {withSlot}/{scripts.Count} 部含占位（共 {slotUse} 处），越界无效占位 {slotBad} 部");

        report.Append("## theater\n\n");
        report.Append("- 部数：").Append(scripts.Count).Append("（解析丢 ").Append(totalSkipped).Append("）\n");
        report.Append("- 场景：").Append(string.Join("  ", byScene.Select(g => $"{g.Key}={g.Count()}"))).Append("\n");
        report.Append($"- 句数：均值 {linesPer.Average():0.0}（{linesPer.Min():0}-{linesPer.Max():0}）\n");
        report.Append($"- 占位符：{withSlot}/{scripts.Count} 部含占位（共 {slotUse} 处），越界无效占位 {slotBad} 部\n\n");
        foreach (var raw in raws)
            report.Append("### 第 ").Append(raw.Batch + 1).Append(" 炉原始输出（")
                  .Append((raw.Ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append("s）\n\n```\n")
                  .Append(raw.Raw.Trim()).Append("\n```\n\n");
        return 0;
    }

    /// <summary>占位越界判定：{nameN} 的 N 超 cast（放送时填不到真人=无效占位，FillSlots 落"朋友" salvage）。</summary>
    private static bool SlotInvalid(string text, int cast)
    {
        for (var n = cast + 1; n <= 9; n++)
            if (text.Contains("{name" + n + "}"))
                return true;
        return false;
    }

    private static int RegexCount(string text, string slot)
    {
        var n = 0;
        for (var i = 1; i <= 5; i++) // §12 #67：street cast 可到 5，占位槽统计同步放开
            if (text.Contains(slot + i + "}"))
                n++;
        return n;
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
