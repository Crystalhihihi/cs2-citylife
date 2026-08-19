namespace CityLife.Util
{
    /// <summary>
    /// 极简 JSON 工具（netstandard2.1 无 System.Text.Json、禁 NuGet 的现实）：
    /// 只够读"字符串字段/整数"，写请求体用 Escape。
    /// 解析失败一律返回 null，由调用方降级——绝不吐半截脏数据。
    /// 全项目通用（Llm 的响应解析、Content 的卡包/批量解析都走这里）。
    /// </summary>
    public static class JsonMini
    {
        /// <summary>取 JSON 文本中指定键的字符串值（含转义解码），取不到返回 null。</summary>
        public static string? GetStr(string json, string key)
        {
            var k = json.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return null;
            var colon = json.IndexOf(':', k + key.Length + 2);
            if (colon < 0) return null;
            var i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            return ParseString(json, i, out _);
        }

        /// <summary>取 JSON 文本中指定键的整数值（用于 usage.prompt_tokens 等），取不到返回 null。</summary>
        public static int? GetInt(string json, string key)
        {
            var k = json.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return null;
            var colon = json.IndexOf(':', k + key.Length + 2);
            if (colon < 0) return null;
            var i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            var start = i;
            while (i < json.Length && char.IsDigit(json[i])) i++;
            if (i == start) return null;
            return int.TryParse(json.Substring(start, i - start), out var v) ? v : (int?)null;
        }

        /// <summary>取 JSON 文本中指定键的原始字面值（布尔/数字/裸字符串通用），取不到返回 null。</summary>
        public static string? GetRaw(string json, string key)
        {
            var k = json.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return null;
            var colon = json.IndexOf(':', k + key.Length + 2);
            if (colon < 0) return null;
            var i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            var start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']' && !char.IsWhiteSpace(json[i])) i++;
            return i == start ? null : json.Substring(start, i - start).Trim('"');
        }

        /// <summary>从 json[quoteIndex]（必须是引号）解析字符串字面量，处理全部 JSON 转义。</summary>
        public static string? ParseString(string json, int quoteIndex, out int endIndex)
        {
            endIndex = quoteIndex;
            if (quoteIndex >= json.Length || json[quoteIndex] != '"') return null;
            var sb = new System.Text.StringBuilder(json.Length - quoteIndex);
            var i = quoteIndex + 1;
            while (i < json.Length)
            {
                var c = json[i];
                if (c == '"')
                {
                    endIndex = i + 1;
                    return sb.ToString();
                }
                if (c == '\\' && i + 1 < json.Length)
                {
                    var e = json[++i];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length && int.TryParse(json.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
                i++;
            }
            return null; // 未闭合
        }

        /// <summary>把任意文本转义成可嵌入 JSON 字符串字面量的形式（写请求体用）。</summary>
        public static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
