#!/usr/bin/env bash
# M0 spike ③：CLI 链路实测（Kimi Code headless one-shot）
#
# 用法：
#   ./scripts/cli-spike.sh [次数，默认 50]
#   CLI_CMD=kimi ./scripts/cli-spike.sh 20      # 换 CLI 命令
#
# 观测目标（对应设计文档 §10 风险 9）：
#   1. 延迟分布（每次 one-shot 的墙钟时间）
#   2. 输出纯净度：能不能稳定拿到纯 JSON（mod 直接解析的前提）
#   3. 错误率：非零退出 / 429 / 超时
#   4. stream-json 输出里有没有 token/usage 统计（喂给网关的 token 统计与缓存观测）
#
# 缓存纪律验证：HEAD 逐字节稳定 + 动态内容只放尾部（编号在最后）。
# 注意：本脚本真实消耗你的 CLI 订阅额度/API 费用，次数自己定。
# 传输层对比项（手工）：kimi web 提供长驻本地 REST 服务，可免除每次进程起停，
#   one-shot 语义不变；进程调用 vs 本地服务的延迟对比另行实测后定网关默认传输。

set -u
N="${1:-50}"
CLI="${CLI_CMD:-kimi}"
OUT_DIR="$(cd "$(dirname "$0")/.." && pwd)/logs/cli-spike-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT_DIR"
CSV="$OUT_DIR/results.csv"
echo "i,exit_code,seconds,json_ok,err_has_429" > "$CSV"

# prompt 头：逐字节稳定，禁时间戳/随机 ID（否则吃不到服务端 prompt 缓存）
HEAD='你是城市社交平台的内容生成器。只输出一个 JSON 对象，格式：{"posts":[{"author":"string","text":"string"}]}。禁止输出 JSON 以外的任何字符。'

command -v "$CLI" >/dev/null || { echo "找不到 CLI 命令：$CLI（先安装并登录，或 CLI_CMD=xxx 指定）"; exit 1; }

for i in $(seq 1 "$N"); do
  start=$SECONDS
  # -p：非交互 one-shot；auto 权限不弹审批；stderr 捕获思考/工具进度噪音
  "$CLI" -p "$HEAD 本轮编号：$i。生成 1 条晚高峰堵车吐槽，市民口吻，30 字以内。" \
    > "$OUT_DIR/$i.out" 2> "$OUT_DIR/$i.err"
  code=$?
  secs=$((SECONDS - start))
  # text 模式输出带 "• " 前缀（kimi -p transcript 风格，官方文档已载），剥掉再判 JSON 纯净度；
  # 注意：stream-json 样本里未发现 usage/token 字段（2026-08-19 实测），token 统计需另寻出路
  json_ok=0; sed '1s/^• //' "$OUT_DIR/$i.out" | head -c 1 | grep -q '{' && json_ok=1
  has_429=0; grep -qiE '429|rate.?limit' "$OUT_DIR/$i.err" && has_429=1
  echo "$i,$code,$secs,$json_ok,$has_429" >> "$CSV"
  sleep 1  # 温和限速，别自己打出 429
done

# stream-json 模式抽样 2 次：检查是否带 usage/token 字段（网关统计的数据来源）
"$CLI" -p "$HEAD 本轮编号：S1。生成 1 条停电商家吐槽，30 字以内。" \
  --output-format stream-json > "$OUT_DIR/stream-json-sample-1.jsonl" 2>/dev/null
"$CLI" -p "$HEAD 本轮编号：S2。生成 1 条新公园好评，30 字以内。" \
  --output-format stream-json > "$OUT_DIR/stream-json-sample-2.jsonl" 2>/dev/null

echo "--- 汇总 ---"
awk -F, 'NR>1 {n++; ok+=($2==0); js+=$4; r429+=$5; sum+=$3; if(min==""||$3<min)min=$3; if($3>max)max=$3}
  END {printf "总数=%d 成功=%d JSON纯净=%d 429=%d\n延迟(s)：avg=%.1f min=%s max=%s\n", n, ok, js, r429, sum/n, min, max}' "$CSV"
echo "原始输出与 stream-json 样本在：$OUT_DIR"
echo "手工检查：grep -o '\"usage\"[^}]*}' $OUT_DIR/stream-json-sample-*.jsonl"
