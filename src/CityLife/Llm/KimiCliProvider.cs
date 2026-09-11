using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CityLife.Llm
{
    /// <summary>
    /// Kimi Code CLI 供给（首发实现）。调用形态来自 2026-08-19 实测
    /// （docs/spikes/2026-08-19-cli-link.md）：
    /// <c>kimi -p "&lt;prompt&gt;" --output-format stream-json</c>，进程全 one-shot；
    /// stdout 为 JSONL，取 <c>role=="assistant"</c> 行的 <c>content</c>，
    /// <c>meta</c> 行（session 恢复提示）直接丢弃。
    /// 延迟实测 9-15s，默认超时 120s 足够宽裕。
    /// </summary>
    public sealed class KimiCliProvider : ICliProvider
    {
        /// <summary>
        /// 诊断日志出口，宿主注入（如 mod 日志）。本模块不引用任何游戏日志类型，
        /// 保持纯 C# 可单测。未注入时静默。
        /// </summary>
        public static Action<string>? Log { get; set; }

        /// <summary>单发超时秒数。实测 9-15s/发，默认 120s；超时杀进程并返回超时错误。</summary>
        public int TimeoutSeconds { get; set; } = 120;

        private readonly string? _exePathOverride;
        private string? _resolvedExePath;
        private bool _exeResolved;

        /// <summary>
        /// <paramref name="exePathOverride"/> 供测试/非常规安装指定 kimi 可执行文件完整路径；
        /// 传 null 走自动解析（默认安装位 → PATH）。
        /// </summary>
        public KimiCliProvider(string? exePathOverride = null)
        {
            _exePathOverride = exePathOverride;
        }

        /// <inheritdoc/>
        public string Name => "KimiCli";

        /// <inheritdoc/>
        public int MaxConcurrency => 1; // CLI 子进程轨串行（进程起停贵+缓存命中纪律）

        /// <inheritdoc/>
        public bool IsAvailable() => ResolveExePath() != null;

        /// <inheritdoc/>
        public async Task<CliResult> OneShotAsync(string prompt, CancellationToken ct)
        {
            string? exe = ResolveExePath();
            if (exe == null)
            {
                return CliResult.Fail("找不到 kimi 可执行文件（~/.kimi-code/bin/kimi 与 PATH 均未命中）", prompt.Length);
            }

            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // prompt 含中文，stdout/stderr 一律按 UTF-8 读
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            // ArgumentList 逐项传参，由运行时负责转义，杜绝引号注入/手工拼参问题
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("stream-json");

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return CliResult.Fail($"kimi 进程启动失败：{ex.Message}", prompt.Length, sw.ElapsedMilliseconds);
            }

            // 先起异步读取再等退出，避免管道缓冲写满导致子进程卡死
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            // netstandard2.1 没有 WaitForExitAsync，轮询等待以便响应取消与超时
            while (true)
            {
                if (process.WaitForExit(200))
                {
                    break;
                }
                if (ct.IsCancellationRequested)
                {
                    TryKill(process);
                    ct.ThrowIfCancellationRequested();
                }
                if (sw.Elapsed.TotalSeconds > TimeoutSeconds)
                {
                    TryKill(process);
                    Log?.Invoke($"[{Name}] 超时（>{TimeoutSeconds}s），已杀进程");
                    return CliResult.Fail($"kimi 超时（>{TimeoutSeconds}s）已杀进程", prompt.Length, sw.ElapsedMilliseconds);
                }
            }

            // 进程退出后管道随即 EOF，两个读取任务可正常完成
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            if (process.ExitCode != 0)
            {
                string digest = Summarize(stderr);
                Log?.Invoke($"[{Name}] 退出码 {process.ExitCode}：{digest}");
                return CliResult.Fail($"kimi 退出码 {process.ExitCode}：{digest}", prompt.Length, sw.ElapsedMilliseconds);
            }

            string? text = ExtractAssistantText(stdout);
            if (string.IsNullOrEmpty(text))
            {
                Log?.Invoke($"[{Name}] 输出中未找到 assistant 内容。stdout 摘要：{Summarize(stdout)}");
                return CliResult.Fail("stream-json 输出中未找到 assistant 内容", prompt.Length, sw.ElapsedMilliseconds);
            }

            return CliResult.Ok(text, prompt.Length, sw.ElapsedMilliseconds);
        }

        // ------------------------------------------------------------------
        // exe 解析：默认安装位 ~/.kimi-code/bin/kimi（Windows 为 kimi.exe）→ PATH。
        // 每个实例只解析一次并缓存；安装状态变化需重建 provider（mod 重启场景足够）。
        // ------------------------------------------------------------------

        private string? ResolveExePath()
        {
            if (_exeResolved)
            {
                return _resolvedExePath;
            }
            _exeResolved = true;

            if (!string.IsNullOrEmpty(_exePathOverride))
            {
                _resolvedExePath = File.Exists(_exePathOverride) ? _exePathOverride : null;
            }
            else
            {
                _resolvedExePath = FindKimiExe();
            }

            if (_resolvedExePath == null)
            {
                Log?.Invoke($"[{Name}] 未找到 kimi 可执行文件，IsAvailable=false");
            }
            return _resolvedExePath;
        }

        private static string? FindKimiExe()
        {
            // Windows 下可执行形态：kimi.exe / kimi.cmd / kimi.bat / kimi.com；
            // 类 Unix：kimi。覆盖 PATHEXT 的常见项，免去逐机器解析 PATHEXT。
            bool windows = Path.DirectorySeparatorChar == '\\';
            string[] candidates = windows
                ? new[] { "kimi.exe", "kimi.cmd", "kimi.bat", "kimi.com", "kimi" }
                : new[] { "kimi" };

            // 1) 默认安装位
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? hit = Probe(Path.Combine(home, ".kimi-code", "bin"), candidates);
            if (hit != null)
            {
                return hit;
            }

            // 2) PATH（which 逻辑）
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        continue;
                    }
                    hit = Probe(dir.Trim(), candidates);
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }
            return null;
        }

        private static string? Probe(string dir, string[] candidates)
        {
            foreach (string name in candidates)
            {
                try
                {
                    string full = Path.Combine(dir, name);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // 目录项非法（含非法字符等）时跳过，不影响后续探测
                }
            }
            return null;
        }

        private static void TryKill(Process process)
        {
            try
            {
                // netstandard2.1 的 Kill 不能带 entireProcessTree；one-shot 场景
                // 子进程随主进程退出被系统回收，可接受。
                process.Kill();
            }
            catch
            {
                // 进程已退出则忽略
            }
        }

        private static string Summarize(string s)
        {
            string oneLine = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            const int max = 500;
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
        }

        // ------------------------------------------------------------------
        // stream-json 解析
        //
        // netstandard2.1 没有内置 System.Text.Json，且本项目禁引 NuGet 包，
        // 因此自带一个"字段定位 + JSON 字符串解码"的轻量提取器（约 60 行）。
        // 它不是完整 JSON 解析器，依赖两个实测成立的前提：
        //   1. 每行一条消息，role 键出现在 content 键之前（kimi stream-json 实测格式）；
        //   2. 我们取的是"第一个" role/content 键，字段值内部的同名文本在键之后出现。
        // 格式漂移时提取失败 → 返回 null → 上层按失败记账，绝不解析出半截脏数据。
        // ------------------------------------------------------------------

        private static string? ExtractAssistantText(string stdout)
        {
            var sb = new StringBuilder();
            bool found = false;
            foreach (string rawLine in stdout.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                // meta 行（session 恢复提示）等一律丢弃，只取 assistant
                if (!TryGetJsonStringField(line, "role", out string role) || role != "assistant")
                {
                    continue;
                }
                if (!TryGetJsonStringField(line, "content", out string content))
                {
                    continue;
                }
                if (found)
                {
                    sb.Append('\n');
                }
                sb.Append(content);
                found = true;
            }
            return found ? sb.ToString() : null;
        }

        /// <summary>
        /// 从一行 JSON 中提取指定字符串字段的值（处理全部 JSON 转义，含 \uXXXX 与代理对）。
        /// </summary>
        private static bool TryGetJsonStringField(string json, string fieldName, out string value)
        {
            value = string.Empty;
            int i = json.IndexOf("\"" + fieldName + "\"", StringComparison.Ordinal);
            if (i < 0)
            {
                return false;
            }
            i += fieldName.Length + 2;

            // 跳过空白与冒号，容忍 "key" : "value" 的空格变体
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != ':') return false;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return false;
            i++;

            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"')
                {
                    value = sb.ToString();
                    return true;
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i >= json.Length) return false;
                char esc = json[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length || !TryParseHex4(json, i, out int code)) return false;
                        i += 4;
                        // 高代理后紧跟 \uDC00-\uDFFF 低代理则合并（emoji 等 BMP 外字符）
                        if (code >= 0xD800 && code <= 0xDBFF
                            && i + 6 <= json.Length && json[i] == '\\' && json[i + 1] == 'u'
                            && TryParseHex4(json, i + 2, out int low)
                            && low >= 0xDC00 && low <= 0xDFFF)
                        {
                            i += 6;
                            sb.Append((char)code).Append((char)low);
                        }
                        else
                        {
                            sb.Append((char)code);
                        }
                        break;
                    default:
                        // 非法转义：该行格式不符，放弃
                        return false;
                }
            }
            return false;
        }

        private static bool TryParseHex4(string s, int start, out int code)
        {
            code = 0;
            for (int k = 0; k < 4; k++)
            {
                char c = s[start + k];
                int v = c >= '0' && c <= '9' ? c - '0'
                    : c >= 'a' && c <= 'f' ? c - 'a' + 10
                    : c >= 'A' && c <= 'F' ? c - 'A' + 10
                    : -1;
                if (v < 0)
                {
                    return false;
                }
                code = (code << 4) | v;
            }
            return true;
        }
    }
}
