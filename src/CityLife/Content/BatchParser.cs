using System;
using System.Collections.Generic;
using CityLife.Util;

namespace CityLife.Content
{
    /// <summary>
    /// 批量响应解析器（RimTalk 教训：LLM 输出非法 JSON 是最高频故障，设计文档 §10 风险 5）：
    /// 剥 markdown 围栏 → 定位 posts 数组 → 顶层对象切分（字符串/转义感知）→ 逐条校验，
    /// 坏条目丢弃、好条目 salvage；全坏返回空表，调用方回退 T0 模板。
    /// v2（M2-B）：每条帖带评论数组（可缺省）——神回复/细节补充/抬杠/共情复读（角色分工见 PromptBuilder 头部）。
    /// </summary>
    public static class BatchParser
    {
        /// <summary>一条解析出的帖子：人格 id + 正文 + 评论（可为空表，绝不 null）。</summary>
        public readonly struct Item
        {
            public readonly string PersonaId;
            public readonly string Text;
            public readonly List<(string PersonaId, string Text)> Comments;

            public Item(string personaId, string text, List<(string, string)> comments)
            {
                PersonaId = personaId;
                Text = text;
                Comments = comments;
            }
        }

        /// <summary>从 LLM 响应里捞出帖子列表（含评论）。log 只记丢弃原因，不记内容全文。</summary>
        public static List<Item> ParsePosts(string raw, Action<string>? log = null)
        {
            var result = new List<Item>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            // 剥围栏：模型偶尔裹 ```json …```，取第一个 { 到最后一个 }
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                log?.Invoke("[Batch] 响应无 JSON 主体，整炉丢弃");
                return result;
            }
            var json = raw.Substring(start, end - start + 1);

            var pk = json.IndexOf("\"posts\"", StringComparison.Ordinal);
            var lb = pk >= 0 ? json.IndexOf('[', pk) : -1;
            var rb = json.LastIndexOf(']');
            if (lb < 0 || rb <= lb)
            {
                log?.Invoke("[Batch] posts 数组缺失，整炉丢弃");
                return result;
            }

            foreach (var obj in SplitTopLevelObjects(json, lb + 1, rb))
            {
                var persona = JsonMini.GetStr(obj, "persona") ?? "";
                var text = JsonMini.GetStr(obj, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                text = text!.Trim();
                if (text.Length > 80)
                {
                    log?.Invoke($"[Batch] 超 80 字丢弃一条（{text.Substring(0, 16)}…）");
                    continue;
                }
                result.Add(new Item(persona, text, ParseComments(obj, log)));
            }
            return result;
        }

        /// <summary>解析纯评论数组响应（市长回应炉专用）：{"comments":[{persona,text}…]}。</summary>
        public static List<(string PersonaId, string Text)> ParseCommentArray(string raw, Action<string>? log = null)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                log?.Invoke("[Batch] 回应炉响应无 JSON 主体，丢弃");
                return result;
            }
            var json = raw.Substring(start, end - start + 1);

            var range = FindArrayRange(json, "comments");
            if (range == null)
            {
                log?.Invoke("[Batch] 回应炉无 comments 数组，丢弃");
                return result;
            }

            foreach (var c in SplitTopLevelObjects(json, range.Value.start, range.Value.end))
            {
                var who = JsonMini.GetStr(c, "persona") ?? "";
                var text = JsonMini.GetStr(c, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                text = text!.Trim();
                if (text.Length > 60)
                {
                    log?.Invoke("[Batch] 回应评论超 60 字丢弃一条");
                    continue;
                }
                result.Add((who, text));
            }
            return result;
        }

        /// <summary>解析单帖的评论数组：坏评论丢弃、好评论 salvage（评论 ≤40 字，比主帖短是铁律）。</summary>
        private static List<(string, string)> ParseComments(string postObj, Action<string>? log)
        {
            var comments = new List<(string, string)>();
            var range = FindArrayRange(postObj, "comments");
            if (range == null)
                return comments;

            foreach (var c in SplitTopLevelObjects(postObj, range.Value.start, range.Value.end))
            {
                var who = JsonMini.GetStr(c, "persona") ?? "";
                var text = JsonMini.GetStr(c, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                text = text!.Trim();
                if (text.Length > 60)
                {
                    log?.Invoke($"[Batch] 评论超 60 字丢弃一条");
                    continue;
                }
                comments.Add((who, text));
            }
            return comments;
        }

        /// <summary>定位指定键的数组值区间（内容区间，不含方括号；字符串/转义感知）。</summary>
        private static (int start, int end)? FindArrayRange(string s, string key)
        {
            var k = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            var lb = s.IndexOf('[', k + key.Length + 2);
            if (lb < 0) return null;

            int depth = 0;
            bool inStr = false, esc = false;
            for (int i = lb; i < s.Length; i++)
            {
                char c = s[i];
                if (esc) { esc = false; continue; }
                if (c == '\\' && inStr) { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                        return (lb + 1, i);
                }
            }
            return null;
        }

        /// <summary>切分 [start, end) 区间内的顶层 {...} 对象（字符串与转义感知，内容里含 {} 也不怕）。</summary>
        private static IEnumerable<string> SplitTopLevelObjects(string s, int start, int end)
        {
            int depth = 0, objStart = -1;
            bool inStr = false, esc = false;
            for (int i = start; i < end; i++)
            {
                char c = s[i];
                if (esc) { esc = false; continue; }
                if (c == '\\' && inStr) { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;

                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        yield return s.Substring(objStart, i - objStart + 1);
                        objStart = -1;
                    }
                }
            }
        }
    }
}
