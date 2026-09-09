using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// 风格卡：语域滤镜，只管"怎么说"不管"说什么"（设计文档 §4 M2 v3.1，严禁写成话题笼）。
    /// 卡面纪律：
    /// 1. 只写说话方式，禁可复读口头禅（"谢邀"教训，docs/spikes/2026-08-20-content-diversity.md）；
    /// 2. 每张卡必须带"具体化义务"（报数字/地点/物件）——治白开水的药（docs/spikes/2026-08-20-platform-styles.md §4a）；
    /// 3. style ≤50 字，语域=对谁说+怎么具体化，不堆性格形容词。
    /// 数据格式 JSONL（每行一张卡，玩家手写/分享零门槛）：
    /// {"id":"tieba","names":"贴吧老哥,老哥不差事","style":"…","tone":"default","always":"true"}
    /// </summary>
    public sealed class Persona
    {
        public string Id = "";
        public string[] Names = Array.Empty<string>();
        public string Style = "";
        public string Tone = "default";
        public bool Always;

        public string DisplayName(uint seed)
            => Names.Length == 0 ? Id : Names[seed % Names.Length];
    }

    /// <summary>
    /// 风格卡册：内置卡包（中文首发；英文平行包随发布补，§12 #21）+ 玩家 personas.jsonl + 全网名字池。
    /// 加载：内置 → personas.jsonl 默认**追加**；文件首行 {"mode":"replace"} 才整册替换（与 topics.jsonl 同口径，§12 #48）；
    /// usernames.jsonl（一行一个网名）进全局名字池，与各卡自有候选名混抽——首发版向社区征集网名即更新此文件（社区贡献零门槛入口）。
    /// </summary>
    public static class PersonaBook
    {
        // 内置卡包 v2（2026-08-20 按真实平台调研 §4a 改写；语域描述 ≤50 字、禁口头禅、带具体化义务）
        private static readonly string[] k_Builtin =
        {
            "{\"id\":\"tieba\",\"names\":\"贴吧老哥,老哥不差事\",\"style\":\"呛人带刺但占理，张口就是具体的人和事，爱用反问收尾，从不抒情\",\"tone\":\"default\"}",
            "{\"id\":\"xhs\",\"names\":\"小红书博主,种草小能手\",\"style\":\"跟闺蜜唠嗑的口气，情绪外放，爱报价格地点和踩坑细节，安利落实在实物上\",\"tone\":\"default\"}",
            "{\"id\":\"weibo\",\"names\":\"微博大V,城市观察者\",\"style\":\"追着热点跑，一句话定调亮立场，爱对比爱反问，像在对广场喊话\",\"tone\":\"default\"}",
            "{\"id\":\"zhihu\",\"names\":\"知乎答主,理性分析帝\",\"style\":\"先给结论再摆理由，拿亲身经历和数据作证，偶尔抖个冷知识，克制不煽情\",\"tone\":\"default\"}",
            "{\"id\":\"dama\",\"names\":\"热心大妈,广场舞领队\",\"style\":\"热心肠大嗓门，谁的事都爱操心，说话必带具体价钱和门牌，见不得别人受苦\",\"tone\":\"default\"}",
            "{\"id\":\"worker\",\"names\":\"打工人,摸鱼学徒\",\"style\":\"拿自己开涮的丧，账算得细，再累也给明天留个具体的小盼头\",\"tone\":\"default\"}",
            "{\"id\":\"chengjian\",\"names\":\"城建迷,规划数据党\",\"style\":\"看城市像看图纸，恨铁不成钢，数字只说大概其，火力集中在具体路口和楼栋\",\"tone\":\"default\"}",
            "{\"id\":\"guide\",\"names\":\"本地向导,活地图\",\"style\":\"本地活地图，安利必带店名价格和时段，见不得游客只挤网红街\",\"tone\":\"default\"}",
            // 作者彩蛋：Crystalhihihi——永远是明星人员（每炉必有的 fixed star，M5 明星系统落地后转永久 L2）
            "{\"id\":\"crystal\",\"names\":\"Crystalhihihi\",\"style\":\"城市头号迷妹，再小的变化也能发现萌点，开心落在一草一木上，不喊口号\",\"tone\":\"default\",\"always\":\"true\"}",
        };

        /// <summary>
        /// 加载卡册：内置卡包 + 玩家 personas.jsonl。默认**追加**进内置卡册（社区加卡零门槛）；
        /// 文件第一条有效行（非空非注释）是 {"mode":"replace"} 才整册替换内置——社区"完整卡包"的口子
        /// （与 topics.jsonl 同口径，§12 #48）。任何解析问题都降级跳过，绝不让一张坏卡炸掉整册；
        /// 文件缺失/读取失败/replace 出空册一律回退内置兜底，不致命。
        /// </summary>
        public static List<Persona> Load(string? overridePath, Action<string> log)
        {
            var builtin = Parse(string.Join("\n", k_Builtin), out _);
            var book = new List<Persona>(builtin);
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            {
                try
                {
                    var text = File.ReadAllText(overridePath);
                    var custom = Parse(text, out var skipped);
                    if (IsReplaceMode(text))
                    {
                        if (custom.Count > 0)
                        {
                            book = custom;
                            log($"[Persona] 卡册就绪：replace 模式——玩家整册 {custom.Count} 张替换内置（非法行跳过 {skipped} 张）");
                        }
                        else
                        {
                            log($"[Persona] 卡册就绪：replace 模式但玩家包 0 张有效——保留内置 {builtin.Count} 张兜底（非法行跳过 {skipped} 张）");
                        }
                    }
                    else
                    {
                        book.AddRange(custom);
                        log($"[Persona] 卡册就绪：内置 {builtin.Count} 张 + 玩家追加 {custom.Count} 张（非法行跳过 {skipped} 张）");
                    }
                }
                catch (Exception e)
                {
                    log($"[Persona] 卡册就绪：玩家卡包读取失败，内置 {builtin.Count} 张兜底：{e.Message}");
                }
            }
            else
            {
                log($"[Persona] 卡册就绪：内置 {builtin.Count} 张（无玩家卡包文件）");
            }
            return book;
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

        /// <summary>加载全网名字池（usernames.jsonl，一行一个网名；# 开头为注释）。文件不存在返回空表。</summary>
        public static List<string> LoadUsernames(string? path, Action<string> log)
        {
            var names = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length > 0 && !line.StartsWith("#") && line.Length <= 24)
                            names.Add(line);
                    }
                }
            }
            catch (Exception e)
            {
                log($"[Persona] 网名字池读取失败（忽略）：{e.Message}");
            }
            log($"[Persona] 网名字池：{names.Count} 个");
            return names;
        }

        /// <summary>解析 JSONL 卡包：缺 id/style 的行跳过并计入 skipped（由 Load 一行汇总，不刷屏）；{"mode":…} 指令行不算卡。</summary>
        private static List<Persona> Parse(string jsonl, out int skipped)
        {
            var list = new List<Persona>();
            skipped = 0;
            foreach (var raw in jsonl.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                if (JsonMini.GetStr(line, "mode") != null)
                    continue; // 模式指令行，不是卡

                var p = new Persona
                {
                    Id = JsonMini.GetStr(line, "id") ?? "",
                    Style = JsonMini.GetStr(line, "style") ?? "",
                    Tone = JsonMini.GetStr(line, "tone") ?? "default",
                    Always = JsonMini.GetStr(line, "always") == "true",
                };
                var names = JsonMini.GetStr(line, "names");
                p.Names = string.IsNullOrEmpty(names)
                    ? new[] { p.Id }
                    : names!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(t => t.Trim())
                             .Where(t => t.Length > 0)
                             .ToArray();

                if (p.Id.Length == 0 || p.Style.Length == 0)
                {
                    skipped++;
                    continue;
                }
                list.Add(p);
            }
            return list;
        }
    }
}
