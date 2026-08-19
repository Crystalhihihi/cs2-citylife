using System.Collections.Generic;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// 事件包：声明式的"市长发言→真实事件"映射单元（社区可扩展，设计文档 §4 M6）。
    /// 内置在代码里（JSONL 字符串），玩家覆写走 ModsSettings/CityLife/eventpacks.jsonl——同 schema。
    /// 字段（JSONL 一行一个）：
    ///   id        唯一标识，LLM 意图映射的锚
    ///   name      显示名（确认弹窗标题用）
    ///   match     匹配描述（喂给意图解析 LLM："玩家说这种话就选我"）
    ///   venue     场馆类型：park（公园/景点，锚点池取）——v1 只有 park
    ///   budgets   预算三档花费（低/中/高，单位：元，真扣财政）
    ///   scale     注入规模（目标到场人数，按写回档位再乘系数）
    ///   cooldownH 冷却（游戏小时，同场馆防刷）
    ///   durationH 活动时长（游戏小时）
    /// 如何扩展：照内置包加一行 JSONL 即可，执行层只认字段不写死任何包。
    /// </summary>
    public sealed class EventPack
    {
        public string Id = "";
        public string Name = "";
        public string Match = "";
        public string Venue = "park";
        public int[] Budgets = { 50000, 200000, 500000 };
        public int Scale = 300;
        public int CooldownH = 24;
        public int DurationH = 3;

        /// <summary>内置事件包（首发：泛活动）。文案字段是给意图解析 LLM 看的，不是给玩家看的。</summary>
        private static readonly string[] k_Builtin =
        {
            "{\"id\":\"festival\",\"name\":\"市民活动\",\"match\":\"玩家要举办活动/市集/游园会/音乐节/嘉年华/庆典/集市——任何'在某地办活动'的发言\",\"venue\":\"park\",\"budgets\":[50000,200000,500000],\"scale\":300,\"cooldownH\":24,\"durationH\":3}",
        };

        public static List<EventPack> Load(string? overridePath, System.Action<string> log)
        {
            var packs = Parse(string.Join("\n", k_Builtin), log, "内置事件包");
            if (!string.IsNullOrEmpty(overridePath) && System.IO.File.Exists(overridePath))
            {
                var custom = Parse(System.IO.File.ReadAllText(overridePath), log, "玩家事件包");
                var added = 0;
                foreach (var p in custom)
                {
                    var idx = packs.FindIndex(b => b.Id == p.Id);
                    if (idx >= 0) packs[idx] = p; else { packs.Add(p); added++; }
                }
                log($"[EventPack] 内置 {packs.Count - added} 个 + 玩家新增 {added} 个");
            }
            else
            {
                log($"[EventPack] 内置 {packs.Count} 个（无玩家覆写）");
            }
            return packs;
        }

        private static List<EventPack> Parse(string jsonl, System.Action<string> log, string source)
        {
            var list = new List<EventPack>();
            foreach (var raw in jsonl.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                var p = new EventPack
                {
                    Id = JsonMini.GetStr(line, "id") ?? "",
                    Name = JsonMini.GetStr(line, "name") ?? "",
                    Match = JsonMini.GetStr(line, "match") ?? "",
                    Venue = JsonMini.GetStr(line, "venue") ?? "park",
                    Scale = JsonMini.GetInt(line, "scale") ?? 300,
                    CooldownH = JsonMini.GetInt(line, "cooldownH") ?? 24,
                    DurationH = JsonMini.GetInt(line, "durationH") ?? 3,
                };
                // budgets 数组：[低,中,高]（手工解析：找 [ ] 内三个整数）
                var bk = line.IndexOf("\"budgets\"", System.StringComparison.Ordinal);
                if (bk >= 0)
                {
                    var lb = line.IndexOf('[', bk);
                    var rb = line.IndexOf(']', lb);
                    if (lb >= 0 && rb > lb)
                    {
                        var parts = line.Substring(lb + 1, rb - lb - 1).Split(',');
                        var vals = new List<int>();
                        foreach (var part in parts)
                            if (int.TryParse(part.Trim(), out var v)) vals.Add(v);
                        if (vals.Count == 3) p.Budgets = vals.ToArray();
                    }
                }
                if (p.Id.Length == 0 || p.Name.Length == 0 || p.Match.Length == 0)
                {
                    log($"[EventPack] {source}：跳过缺字段的包：{line.Substring(0, System.Math.Min(40, line.Length))}…");
                    continue;
                }
                list.Add(p);
            }
            return list;
        }
    }
}
