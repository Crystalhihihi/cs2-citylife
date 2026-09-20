// PromptEval —— prompt 优化专项离线评测 harness（§12 #60 刀⓪）
// 目的：替代"实机看感觉"——固定快照+处境卡具，走真实 PromptBuilder 组装、真实 provider 发 K 炉，
//       用确定性指标打分：猫密度/场合分布(Any率)/句长方差/近重复率/分区熵。
//       改动 prompt 前后各跑一轮对比报告（logs/prompt-eval/ 下留档，含原始输出供盲评）。
// 用法（仓库根目录）：
//   dotnet run --project tools/PromptEval -- [--scenario chatter|topics|all] [--k N]
//        [--set collapsed|diverse|both] [--count 30] [--config llm.json路径]
//        [--model 覆盖] [--thinking disabled|""] [--out 报告目录]
//        [--variant v0|v1|v2|all]（闲聊炉规格签对照 2026-09-16 §12 #69 落地后：v0/v2=生产形态含系统规格签/
//         v1=无签对照组；实验期"去例句"语义已随生产头删【样子】而退役——镜像率基准例句冻结在 harness 内）
//        [--scenevar v0|v1|v2|all]（贴景措辞变体 2026-09-20 §12 #71 玩家指示：v0 生产原样/景签注短指针
//         +卡列表后独立【场景】段/v2 段内加反通用句判别线；harness 对生产 prompt 做确定性段落手术，
//         锚点缺失即抛错人工对齐——定胜者前生产 PromptBuilder 零改动）
//        [--set collapsed|diverse|both|scene]（scene=2026-09-20 新增景签具组：8 张全 Indoor 景签卡，
//         话核严格取自 SceneWords.k_Table——表改了手抄跟上，别自编）
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
    // §12 #62（2026-09-16）：卡具刷新为业态细分后的生产形态（便利店/软件公司/服装厂进卡文，住宅无业态照旧）
    // §12 #71（2026-09-17）：卡具带"｜景：｜话核："场景签（生产组卡 60/40 掷签后的形态），验证贴景服从
    private static readonly string[] k_Collapsed =
    {
        "手头紧的退休大妈，在住宅区呆着",
        "退休大爷，在住宅区呆着",
        "退休大爷，在医院里看病｜景：医院｜话核：打针/排队",
        "家庭主妇，在住宅区呆着",
        "退休大爷，在公园里溜达｜对：退休大妈，在公园里跳广场舞",
        "手头紧的退休大妈，在住宅区呆着",
        "无业青年，在住宅区呆着",
        "退休大妈，在便利店里逛",
    };

    // 多样组：分层配额落地后的目标分布（途中/室内/路上/载具/游客俱全）——对照组
    private static readonly string[] k_Diverse =
    {
        "上班族，坐公交下班回家路上（去住宅区）",
        "学生，打车上学路上（去学校）",
        "手头紧的上班族，开私家车上班路上（去建材厂）",
        "游客，在公园里溜达｜对：游客，在公园里拍照",
        "退休大爷，在公园里溜达",
        "学生，在学校里上课｜景：中学｜话核：考试/晚自习",
        "货车司机，开货车送货路上（去便利店）",
        "青年，走路去服装店路上｜对：上班族，走路去地铁站路上",
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

    // 场景签具组（--set scene，2026-09-20 §12 #71 贴景措辞实验）：8 张全 Indoor 景签卡——
    // 卡文照生产 DescribeCitizen 文风（身份，在场所做事｜景：词｜话核：核1/核2）；
    // 话核词严格取自 SceneWords.k_Table 对应行（SceneWords 在 GameBridge 不链接进 harness，
    // 手抄同步靠人工对齐——表改了这里跟上，别自编）。pairs 全 false、场合全 Indoor。
    private static readonly string[] k_Scene =
    {
        "小学生，在学校里上课｜景：小学｜话核：作业/课间",
        "退休大爷，在医院里看病｜景：医院｜话核：挂号/打针",
        "上班族，在诊所里看病｜景：诊所｜话核：开药/问诊",
        "游客，在火车站里候车｜景：火车站｜话核：晚点/行李",
        "上班族，在邮局里寄包裹｜景：邮局｜话核：包裹/排队",
        "上班族，在市政厅里办事｜景：市政厅｜话核：盖章/投诉",
        "上班族，在派出所里报案｜景：派出所｜话核：报案/调解",
        "退休大妈，在监狱里探视｜景：监狱｜话核：探视/放风",
    };

    private static readonly BubbleOccasion[] k_SceneOcc =
    {
        BubbleOccasion.Indoor, BubbleOccasion.Indoor, BubbleOccasion.Indoor, BubbleOccasion.Indoor,
        BubbleOccasion.Indoor, BubbleOccasion.Indoor, BubbleOccasion.Indoor, BubbleOccasion.Indoor,
    };

    private static readonly bool[] k_ScenePair =
    {
        false, false, false, false, false, false, false, false,
    };

    private static readonly (string A, string B)[] k_ScenePairNames =
    {
        ("", ""), ("", ""), ("", ""), ("", ""), ("", ""), ("", ""), ("", ""), ("", ""),
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

    // —— 闲聊炉 prompt 变体实验支持（2026-09-16，§12 #60 既定用途：固定卡具离线发炉、改动前后对比）——
    // 实验已定案落生产（§12 #69）：生产头删【样子】例句、组卡缀系统规格签。harness 语义随之切换：
    // v0/v2=生产形态（含规格签，v2 为实验历史编号保留别名）；v1=去规格对照组（无签，回归用——
    // 对照"规格签到底有没有用"）。例句手术已退役（生产头里【样子】已不存在）。
    private const int k_V0 = 0, k_V1 = 1, k_V2 = 2;

    /// <summary>镜像率基准例句（历史 6 行【样子】内容例句的冻结副本，2026-09-16 §12 #69 后生产头已删——
    /// 冻结在此只为镜像率回归基准：新版本输出对它们算相似度，照镜子倾向永不许回潮）。</summary>
    private static readonly string[] k_ExampleLines =
    {
        "这雨啥时候停啊，鞋全湿透了",
        "解放路那个红灯九十秒，我数了三回才过去",
        "又堵死了，今儿第三回",
        "楼下包子从一块五涨到两块，老板说面也贵了，没法子",
        "公交再不来我真走回去了",
        "这花开得还行，拍一张",
    };

    // AI 腔巡检词表（直播腔/书面腔/动作神态描写——【语域】明令禁止的残留探针；emoji 另按代理对判）
    private static readonly string[] k_AiToneWords =
        { "家人们", "宝子", "谁懂", "绝绝子", "狠狠", "破防", "yyds", "emo",
          "笑了笑", "叹了口气", "耸耸肩", "挠头", "指着", "翻了个白眼",
          "进行", "相关", "予以", "据悉", "首先", "其次" };

    // —— 贴景措辞变体（2026-09-20 玩家指示"细分提示词优化"实验，--scenevar 轴）——
    // 诊断：景签解释塞【处境卡】头部超长连环句注意力稀释；"（学校/医院/车站这类）"是镜像诱饵
    // （45 个场景词被 3 个通用名带偏）；话核用法没说清=忽略话核/逐字硬塞两种失败；"视角都行"含糊。
    // V0=生产原样；V1=景签注缩短指独立段+卡列表后独立【场景】段（删场景例名、话核用法说清）；
    // V2=V1 段内加反通用句判别线。对生产 prompt 做确定性段落手术（锚点缺失即抛错人工对齐——
    // 定胜者前生产 PromptBuilder 零改动）。卡侧"｜景：｜话核："段格式本轮不动（玩家说粒度他亲自过）。
    private const string k_SceneClauseProd = "；\"｜景：\"是这人所在的场景（学校/医院/车站这类），\"｜话核：\"是该场景的话题核——带景签的卡必须写与这场景相关的话（当事人/唠嗑/排队视角都行，别跑题）；没景签的卡自由发挥";
    private const string k_SceneClauseShort = "；\"｜景：\"/\"｜话核：\"是场景签（口径见【场景】段，带签必须服从）";
    private const string k_SceneSectionV1 = "【场景】带\"｜景：\"的卡：这人此刻就在那个场所里，话要从那个场所里长出来——在那办事、等人、干活、陪人，说的就是那地方的事。\"｜话核：\"是这地方人们嘴边的事，顺着它自然聊，别逐字复读、别当成必答清单。没\"｜景：\"的卡自由发挥。\n";
    private const string k_SceneSectionV2 = "【场景】带\"｜景：\"的卡：这人此刻就在那个场所里，话要从那个场所里长出来——在那办事、等人、干活、陪人，说的就是那地方的事。\"｜话核：\"是这地方人们嘴边的事，顺着它自然聊，别逐字复读、别当成必答清单。写\"困/累/饿/好无聊\"这种在哪个地方都能说的话，算跑题。没\"｜景：\"的卡自由发挥。\n";

    /// <summary>贴景措辞手术：V1/V2 把生产景签注换短指针 + 卡列表后（【规格】/【任务】锚点前）插独立【场景】段。</summary>
    private static string ApplySceneVar(string prompt, int sceneVar)
    {
        if (sceneVar == 0)
            return prompt;
        if (!prompt.Contains(k_SceneClauseProd))
            throw new InvalidOperationException("生产【处境卡】景签注原文没找到——结构漂移，变体手术先人工对齐");
        var p = prompt.Replace(k_SceneClauseProd, k_SceneClauseShort);
        var specIdx = p.IndexOf("【规格】", StringComparison.Ordinal);
        var taskIdx = p.IndexOf("【任务】", StringComparison.Ordinal);
        var at = specIdx >= 0 ? specIdx : taskIdx;
        if (at < 0)
            throw new InvalidOperationException("【规格】/【任务】锚点没找到——变体手术先人工对齐");
        return p.Insert(at, sceneVar == 2 ? k_SceneSectionV2 : k_SceneSectionV1);
    }

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var scenario = ArgOf(args, "--scenario", "all");
        var k = int.Parse(ArgOf(args, "--k", "2"));
        var setSel = ArgOf(args, "--set", "both");
        var variantSel = ArgOf(args, "--variant", "v0"); // 闲聊炉规格签对照：v0/v2 生产形态 / v1 无签对照（仅 chatter 场景有效）
        var sceneVarSel = ArgOf(args, "--scenevar", "v0"); // 贴景措辞变体（2026-09-20）：v0 生产原样 / v1 独立【场景】段 / v2 加反通用线 / all 三连（仅 chatter 场景有效）
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
            var variants = variantSel switch
            {
                "all" => new[] { k_V0, k_V1, k_V2 },
                "v1" => new[] { k_V1 },
                "v2" => new[] { k_V2 },
                _ => new[] { k_V0 },
            };
            var sceneVars = sceneVarSel switch
            {
                "all" => new[] { 0, 1, 2 },
                "v1" => new[] { 1 },
                "v2" => new[] { 2 },
                _ => new[] { 0 },
            };
            foreach (var v in variants)
            foreach (var sv in sceneVars)
            {
                if (setSel is "collapsed" or "both")
                    exit |= await RunChatter(provider, reservoir, "collapsed", v, sv, k_Collapsed, k_CollapsedOcc, k_CollapsedPair, k_CollapsedPairNames, 100u, k, report);
                if (setSel is "diverse" or "both")
                    exit |= await RunChatter(provider, reservoir, "diverse", v, sv, k_Diverse, k_DiverseOcc, k_DiversePair, k_DiversePairNames, 200u, k, report);
                if (setSel is "scene")
                    exit |= await RunChatter(provider, reservoir, "scene", v, sv, k_Scene, k_SceneOcc, k_ScenePair, k_ScenePairNames, 300u, k, report);
            }
        }
        if (scenario is "topics" or "all")
            exit |= await RunTopics(provider, reservoir, topicCount, k, report);
        if (scenario is "theater" or "all")
            exit |= await RunTheater(provider, k, report);
        if (scenario is "reaction" or "all")
            exit |= await RunReaction(provider, k, report);

        Directory.CreateDirectory(outDir);
        var file = Path.Combine(outDir, "eval-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
        File.WriteAllText(file, report.ToString());
        Console.WriteLine("[Eval] 报告已写：" + file);
        return exit;
    }

    /// <summary>闲聊炉场景：真实 BuildChatterPrompt 发 K 炉，ParseBatch 解析后打分。
    /// occs=具卡场合真值（§12 #60 刀①后实机由 CitizenPoolSystem 盖章）——按 card 号映射验证归属质量。
    /// pairs/pairNames=双人卡真值与拼装假名（§12 #63：模拟执行层 m_CurrentPairs，验证对话行产出与 40 硬顶拼装防线）。
    /// variant=规格签对照（§12 #69 落地后）：v0/v2=生产形态（卡尾缀 ChatterSpec 系统签，每炉重抽与生产同链）/
    /// v1=无签对照组。指标对照（镜像率/规格服从）三变体同口径计算，v1 的规格指标=假设性无签对照。</summary>
    private static async Task<int> RunChatter(ICliProvider provider, TopicReservoir reservoir,
                                              string setName, int variant, int sceneVar, string[] cards, BubbleOccasion[] occs,
                                              bool[] pairs, (string A, string B)[] pairNames,
                                              uint batchBase, int k, StringBuilder report)
    {
        var head = PromptBuilder.BuildChatterHead(); // 生产头（§12 #69 后已无【样子】例句段）
        var label = $"v{variant}s{sceneVar}/{setName}";
        // 规格签（§12 #69 落生产后口径）：v0/v2=生产形态（卡尾缀"｜写：｜情："，ChatterSpec 与生产同一条分配链，
        // 每炉按 具基址+炉次 重抽——同生产炉计数锚定）；v1=无签对照组。规格对三变体同口径计算（对照组测量用）
        var specOn = variant != k_V1;
        var all = new List<ParsedChatterLine>();
        var lineForge = new List<int>(); // 每条解析行出自哪炉（规格签按炉重抽，指标对照要按炉对号）
        var forgeShapes = new List<int[]>(); // 每炉每卡的句型签索引（对照指标用；v1 也照算=假设性对照）
        var raws = new List<(int Batch, string Raw, long Ms, int? Pt, int? Rt)>();
        var totalSkipped = 0;
        var pairCount = pairs.Count(p => p);
        // 双人卡号列表（1 起）——与生产侧 BubbleChatterSystem 同名锚定口径，prompt 段头/任务行点名用
        var pairCardNos = pairs.Select((p, i) => (p, i)).Where(x => x.p).Select(x => x.i + 1).ToArray();
        for (var b = 0; b < k; b++)
        {
            // 每卡配一题：真实 TopicFor 确定性抽题（与实机同一代码路径）
            var topics = cards.Select((_, i) => reservoir.TopicFor(batchBase + (uint)b, i)).ToArray();
            // §12 #69 规格签：走生产同一条 ChatterSpec 分配链（具基址+炉次锚定、同炉同签顺延去重）
            var used = new HashSet<(int Shape, int Mood)>();
            var shapes = new int[cards.Length];
            var effCards = new string[cards.Length];
            for (var i = 0; i < cards.Length; i++)
            {
                var (s, m) = ChatterSpec.Assign(batchBase + (uint)b, i, used);
                shapes[i] = s;
                effCards[i] = specOn && s >= 0 ? cards[i] + $"｜写：{ChatterSpec.Shapes[s]}｜情：{ChatterSpec.Moods[m]}" : cards[i];
            }
            forgeShapes.Add(shapes);
            var prompt = PromptBuilder.BuildChatterPrompt(head, k_Snap, effCards, topics, k_Rumors, pairCardNos);
            prompt = ApplySceneVar(prompt, sceneVar); // 贴景措辞变体手术（2026-09-20；V0=原样直通）
            Console.WriteLine($"[Eval·chatter/{label}] 第 {b + 1}/{k} 炉发出（{prompt.Length} 字符）…");
            var r = await provider.OneShotAsync(prompt, CancellationToken.None);
            if (!r.Success)
            {
                Console.WriteLine($"[Eval·chatter/{label}] 第 {b + 1} 炉失败：{r.Error}");
                return 1;
            }
            var parsed = BubbleSnippetPool.ParseBatch(r.Text, out var skipped);
            totalSkipped += skipped;
            all.AddRange(parsed);
            for (var i = 0; i < parsed.Count; i++)
                lineForge.Add(b);
            raws.Add((b, r.Text, r.LatencyMs, r.PromptTokens, r.ResponseTokens));
            Console.WriteLine($"[Eval·chatter/{label}] 第 {b + 1} 炉：{parsed.Count} 条（丢 {skipped}）{r.LatencyMs / 1000.0:0.0}s");
        }

        // —— 指标 ——
        var n = Math.Max(1, all.Count);
        // 猫密度/句长的文本口径：独白条=Text，对话条=A+B（台词本身，不含执行层名字前缀）
        string BodyOf(ParsedChatterLine e) => e.IsDialogue ? e.A! + e.B! : e.Text ?? "";
        var bodies = all.Select(BodyOf).ToArray();
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
        var (dupPairs, dupSample) = NearDup(bodies);
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
                var text = na + "：" + e.A + "\n" + nb + "：" + e.B; // 历史拼装形态模拟（生产已于 2026-09-17 改即席剧串行双泡，§12 #63 修正注记——本指标仅剩 schema 句长观测意义）
                composed++;
                if (text.Length > 40)
                    composedOver++;
            }
        }
        var pairExpected = Math.Max(1, pairCount * k);

        // 镜像率（变体实验核心指标）：每条输出对 6 行例句取最高字符 bigram Jaccard；
        // ≥0.5=镜像嫌疑、≥0.7=照抄实锤；留最高分样本对供报告引用
        var mirrorSims = new double[bodies.Length];
        var topMirror = new List<(double Sim, string Line, string Example)>();
        var exampleGrams = k_ExampleLines.Select(Bigrams).ToArray();
        for (var i = 0; i < bodies.Length; i++)
        {
            var g = Bigrams(bodies[i]);
            var best = 0.0;
            var bestEx = -1;
            if (g.Count > 0)
                for (var j = 0; j < exampleGrams.Length; j++)
                {
                    var inter = g.Intersect(exampleGrams[j]).Count();
                    var jac = (double)inter / (g.Count + exampleGrams[j].Count - inter);
                    if (jac > best) { best = jac; bestEx = j; }
                }
            mirrorSims[i] = best;
            if (bestEx >= 0)
                topMirror.Add((best, bodies[i], k_ExampleLines[bestEx]));
        }
        topMirror.Sort((a, b) => b.Sim.CompareTo(a.Sim));
        var mirrorMean = mirrorSims.Length > 0 ? mirrorSims.Average() : 0;
        var mirror50 = mirrorSims.Count(s => s >= 0.5);
        var mirror70 = mirrorSims.Count(s => s >= 0.7);

        // 词汇多样性：去重句占比 + 相异字符 bigram 占比（题材同质化的粗探针）
        var distinctLines = bodies.Distinct().Count();
        var allGrams = bodies.SelectMany(Bigrams).ToArray();
        var vocabRate = allGrams.Length > 0 ? (double)allGrams.Distinct().Count() / allGrams.Length : 0;

        // AI 腔巡检：直播腔/书面腔/动作描写词表 + emoji（代理对）
        var aiHits = bodies.Where(b => k_AiToneWords.Any(w => b.Contains(w)) || b.Any(char.IsSurrogate)).ToArray();

        // 规格服从对照：锚定"带数字长句"规格的卡（三变体同口径按炉抽签），其条目带数字占比——
        // v0/v2 是规格生效实测，v1 是无签对照。
        // 数字口径=ASCII 数字 ∪ 中文数词（零~十/百/千/万/两/几）——中文语料里"两天/三点七"才是常态，
        // 只认 char.IsDigit 会把中文数字表达全漏掉（2026-09-16 变体实验首轮 0% 误报实锤）
        const string cjkDigits = "零一二三四五六七八九十百千万两几";
        bool HasDigit(string s) => s.Any(c => char.IsDigit(c) || cjkDigits.Contains(c));
        var digitShapeIdx = Array.IndexOf(ChatterSpec.Shapes, "带数字长句");
        var digitTotal = 0; var digitHit = 0;
        for (var i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (e.Card < 1 || e.Card > cards.Length)
                continue;
            if (forgeShapes[lineForge[i]][e.Card - 1] != digitShapeIdx)
                continue;
            digitTotal++;
            if (HasDigit(BodyOf(e)))
                digitHit++;
        }

        // §12 #71 场景贴题率：卡具里带"｜景：…｜话核：词1/词2"的卡，其条目含任一话核词的占比
        // （话核从卡文现解，不硬编码——卡具换词指标自动跟上）
        var sceneCores = new string[]?[cards.Length];
        for (var i = 0; i < cards.Length; i++)
        {
            var mk = cards[i].IndexOf("｜话核：", StringComparison.Ordinal);
            if (mk < 0)
                continue;
            var rest = cards[i][(mk + "｜话核：".Length)..];
            var cut = rest.IndexOf('｜');
            sceneCores[i] = (cut >= 0 ? rest[..cut] : rest).Split('/');
        }
        var sceneTotal = 0; var sceneHit = 0;
        if (sceneCores.Any(c => c != null))
            for (var i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (e.Card < 1 || e.Card > cards.Length || sceneCores[e.Card - 1] == null)
                    continue;
                sceneTotal++;
                var body = BodyOf(e);
                if (sceneCores[e.Card - 1]!.Any(w => body.Contains(w)))
                    sceneHit++;
            }

        Console.WriteLine($"\n== chatter/{label} 汇总（{all.Count} 条，解析丢 {totalSkipped}）==");
        Console.WriteLine($"  猫密度     : {catHits}/{all.Count} = {Pct(catHits, n)}");
        Console.WriteLine($"  场合映射   : {string.Join("  ", Enum.GetValues<BubbleOccasion>().Select(o => $"{o}={occ.GetValueOrDefault(o)}"))}  无归属={Pct(orphan, n)}");
        Console.WriteLine($"  句长(独白) : 均值 {mean:0.0} 字，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), Math.Max(1, lens.Length))}，>20字 {Pct(lens.Count(l => l > 20), Math.Max(1, lens.Length))}");
        Console.WriteLine($"  卡覆盖     : 空卡 {zeroCards}/{cards.Length}，每卡每炉 {perCard.Min() / (double)Math.Max(1, k):0.0}-{perCard.Max() / (double)Math.Max(1, k):0.0} 条（独白卡应 2-3，双人卡应 1）");
        Console.WriteLine($"  对话       : {dialogues.Length} 条（双人卡 {pairCount} 张×K{k}，齐全率 {Pct(dialogues.Length, pairExpected)}），a/b 句长 {(aLens.Length > 0 ? aLens.Average() : 0):0.0}/{(bLens.Length > 0 ? bLens.Average() : 0):0.0} 字，>12字 {over12} 条，拼装>40字 {composedOver}/{composed}");
        Console.WriteLine($"  近重复     : {dupPairs} 对（Jaccard≥0.7）{(dupSample != null ? " 例：" + dupSample : "")}");
        Console.WriteLine($"  镜像率     : 均值 {mirrorMean:0.00}，≥0.5 嫌疑 {mirror50}/{bodies.Length}，≥0.7 实锤 {mirror70}/{bodies.Length}");
        Console.WriteLine($"  词汇多样性 : 去重句 {distinctLines}/{bodies.Length}，相异bigram {vocabRate:P0}");
        Console.WriteLine($"  AI腔       : {aiHits.Length}/{bodies.Length}{(aiHits.Length > 0 ? "（" + aiHits[0] + "）" : "")}");
        Console.WriteLine($"  规格对照   : \"带数字长句\"锚定卡 {digitTotal} 条带数字 {Pct(digitHit, Math.Max(1, digitTotal))}（v0/v2=实测 v1=无签对照）");
        if (sceneTotal > 0)
            Console.WriteLine($"  场景贴题率 : {sceneHit}/{sceneTotal} = {Pct(sceneHit, sceneTotal)}（§12 #71 景签卡条目含话核词占比）");

        report.Append("## chatter / ").Append(label).Append("\n\n");
        report.Append("- 条数：").Append(all.Count).Append("（解析丢 ").Append(totalSkipped).Append("）\n");
        report.Append("- 猫密度：").Append(Pct(catHits, n)).Append($" ({catHits}/{all.Count})\n");
        report.Append("- 场合映射：").Append(string.Join("  ", Enum.GetValues<BubbleOccasion>().Select(o => $"{o}={occ.GetValueOrDefault(o)}"))).Append($"，无归属 {Pct(orphan, n)}\n");
        report.Append($"- 句长(独白)：均值 {mean:0.0}，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), Math.Max(1, lens.Length))}，>20字 {Pct(lens.Count(l => l > 20), Math.Max(1, lens.Length))}\n");
        report.Append($"- 卡覆盖：空卡 {zeroCards}/{cards.Length}，每卡每炉 {perCard.Min() / (double)Math.Max(1, k):0.0}-{perCard.Max() / (double)Math.Max(1, k):0.0} 条\n");
        report.Append($"- 对话：{dialogues.Length} 条（双人卡 {pairCount}×K{k}，齐全率 {Pct(dialogues.Length, pairExpected)}），a/b 句长 {(aLens.Length > 0 ? aLens.Average() : 0):0.0}/{(bLens.Length > 0 ? bLens.Average() : 0):0.0}，>12字 {over12} 条，拼装>40字 {composedOver}/{composed}\n");
        report.Append("- 近重复：").Append(dupPairs).Append(" 对").Append(dupSample != null ? "（" + dupSample + "）" : "").Append("\n");
        report.Append($"- 镜像率：均值 {mirrorMean:0.00}，≥0.5 嫌疑 {Pct(mirror50, n)}，≥0.7 实锤 {Pct(mirror70, n)}（对 {k_ExampleLines.Length} 行例句字符 bigram Jaccard）\n");
        report.Append($"- 词汇多样性：去重句 {distinctLines}/{bodies.Length}，相异 bigram 占比 {vocabRate:P0}\n");
        report.Append($"- AI腔巡检：{aiHits.Length}/{bodies.Length} 命中\n");
        report.Append($"- 规格对照（\"带数字长句\"锚定卡条目数字出现率，含中文数词；v0/v2=实测 v1=无签对照）：{Pct(digitHit, Math.Max(1, digitTotal))} ({digitHit}/{digitTotal})\n");
        if (sceneTotal > 0)
            report.Append($"- 场景贴题率（§12 #71 景签卡条目含话核词占比）：{Pct(sceneHit, sceneTotal)} ({sceneHit}/{sceneTotal})\n");
        if (topMirror.Count > 0)
        {
            report.Append("- 镜像最高分样本：\n");
            foreach (var m in topMirror.Take(3))
                report.Append($"  - {m.Sim:0.00} 「{m.Line}」 ≈ 例句「{m.Example}」\n");
        }
        if (aiHits.Length > 0)
            report.Append("- AI腔样本：").Append(string.Join("｜", aiHits.Take(3))).Append("\n");
        report.Append("\n");
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
            // 缺口分区走真实 DeficitScenes（2026-09-16 分区水位修正）：空库=六分区全缺口，与生产冷启动同形
            var prompt = PromptBuilder.BuildTheaterStockPrompt(head, k_Snap, stock, k_Diverse, 4, 3, // streetMaxCast=设置页默认 3（§12 #67）
                stock.DeficitScenes(2));
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

    /// <summary>事件围观炉场景（§12 #70）：真实 BuildEventReactionPrompt 发 K 炉（固定卡具=车祸现场+围观情绪签），
    /// 真实 BubbleSnippetPool.ParseBatch 解析——schema 合规（{"text"} 单行）、条数达成、句长分布、近重复、AI 腔。</summary>
    private static async Task<int> RunReaction(ICliProvider provider, int k, StringBuilder report)
    {
        var head = PromptBuilder.BuildEventReactionHead();
        // 固定卡具：P0 车祸现场（生产 SceneDesc 同构——地点+事件+烈度档+应急到场）+ 围观情绪签（生产抽签同词表）
        const string k_SceneDesc = "「和平路」路口两车相撞，挺严重，警车已到";
        var moods = new List<string> { ChatterSpec.OnlookerMoods[0], ChatterSpec.OnlookerMoods[2], ChatterSpec.OnlookerMoods[4] };
        var all = new List<ParsedChatterLine>();
        var raws = new List<(int Batch, string Raw, long Ms)>();
        var totalSkipped = 0;
        for (var b = 0; b < k; b++)
        {
            var prompt = PromptBuilder.BuildEventReactionPrompt(head, k_SceneDesc, moods, 10);
            Console.WriteLine($"[Eval·reaction] 第 {b + 1}/{k} 炉发出（{prompt.Length} 字符）…");
            var r = await provider.OneShotAsync(prompt, CancellationToken.None);
            if (!r.Success)
            {
                Console.WriteLine($"[Eval·reaction] 第 {b + 1} 炉失败：{r.Error}");
                return 1;
            }
            var parsed = BubbleSnippetPool.ParseBatch(r.Text, out var skipped);
            totalSkipped += skipped;
            var valid = parsed.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToArray();
            all.AddRange(valid);
            raws.Add((b, r.Text, r.LatencyMs));
            Console.WriteLine($"[Eval·reaction] 第 {b + 1} 炉：有效 {valid.Length} 条（丢 {skipped}）{r.LatencyMs / 1000.0:0.0}s");
        }

        var n = Math.Max(1, all.Count);
        var bodies = all.Select(p => p.Text!).ToArray();
        var lens = bodies.Select(t => t.Length).ToArray();
        var (mean, std) = MeanStd(lens.Length > 0 ? lens : new[] { 0 });
        var (dupPairs, dupSample) = NearDup(bodies);
        var aiHits = bodies.Where(t => k_AiToneWords.Any(w => t.Contains(w)) || t.Any(char.IsSurrogate)).ToArray();

        Console.WriteLine($"\n== reaction 汇总（{all.Count} 条，解析丢 {totalSkipped}）==");
        Console.WriteLine($"  句长       : 均值 {mean:0.0} 字，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), n)}，>30字 {Pct(lens.Count(l => l > 30), n)}");
        Console.WriteLine($"  近重复     : {dupPairs} 对（Jaccard≥0.7）{(dupSample != null ? " 例：" + dupSample : "")}");
        Console.WriteLine($"  AI腔       : {aiHits.Length}/{bodies.Length}");

        report.Append("## reaction（事件围观炉，§12 #70）\n\n");
        report.Append("- 条数：").Append(all.Count).Append("（解析丢 ").Append(totalSkipped).Append("）\n");
        report.Append($"- 句长：均值 {mean:0.0}，标准差 {std:0.0}，≤15字 {Pct(lens.Count(l => l <= 15), n)}，>30字 {Pct(lens.Count(l => l > 30), n)}\n");
        report.Append("- 近重复：").Append(dupPairs).Append(" 对").Append(dupSample != null ? "（" + dupSample + "）" : "").Append("\n");
        report.Append("- AI腔巡检：").Append(aiHits.Length).Append('/').Append(bodies.Length).Append("\n\n");
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
