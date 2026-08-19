#!/usr/bin/env bash
# 本地部署脚本：编译并把 CityLife.dll 装进游戏本地 Mods 目录
#
# 用法：
#   ./scripts/deploy.sh              # 编译（Debug）+ 部署
#   ./scripts/deploy.sh -c Release   # 换配置
#   ./scripts/deploy.sh disable      # 临时禁用（目录改名 .CityLife，游戏忽略点开头目录）
#   ./scripts/deploy.sh enable       # 恢复
#
# 原理（官方 Modding Toolchain 惯例 + 社区验证，见 README）：
#   本地代码 mod = Mods/<名>/ 文件夹 + dll，游戏启动时自动加载，无需 playset 激活；
#   文件夹名以点开头视为禁用。
set -euo pipefail

USER_DIR="${USERPROFILE//\\//}/AppData/LocalLow/Colossal Order/Cities Skylines II"
MOD_DIR="$USER_DIR/Mods/CityLife"
MOD_DIR_DISABLED="$USER_DIR/Mods/.CityLife"
DLL_NAME="CityLife.dll"

# 旧名清理：CimLife 时代的部署产物（2026-08-20 改名 CityLife），改名禁用防双份加载
LEGACY="$USER_DIR/Mods/CimLife"
if [ -d "$LEGACY" ]; then
  mv "$LEGACY" "$USER_DIR/Mods/.CimLife-legacy" && echo "旧版 Mods/CimLife 已改名禁用为 .CimLife-legacy"
fi

case "${1:-deploy}" in
  disable)
    if [ -d "$MOD_DIR" ]; then mv "$MOD_DIR" "$MOD_DIR_DISABLED" && echo "已禁用：$MOD_DIR_DISABLED"; else echo "未找到启用的 mod 目录"; fi
    exit 0;;
  enable)
    if [ -d "$MOD_DIR_DISABLED" ]; then mv "$MOD_DIR_DISABLED" "$MOD_DIR" && echo "已启用：$MOD_DIR"; else echo "未找到禁用的 mod 目录"; fi
    exit 0;;
esac

CONFIG="Debug"
while [ $# -gt 0 ]; do
  case "$1" in -c|--configuration) CONFIG="$2"; shift 2;; *) shift;; esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "$ROOT/src/CityLife/CityLife.csproj" -c "$CONFIG" -v q --nologo

OUT="$ROOT/src/CityLife/bin/$CONFIG/netstandard2.1"
[ -f "$OUT/$DLL_NAME" ] || { echo "未找到编译产物 $OUT/$DLL_NAME"; exit 1; }

mkdir -p "$MOD_DIR"
cp "$OUT/$DLL_NAME" "$MOD_DIR/"
# pdb 一并复制：-developerMode 下异常堆栈才有行号
if [ -f "$OUT/CityLife.pdb" ]; then cp "$OUT/CityLife.pdb" "$MOD_DIR/"; fi
# Harmony 运行时（原版 chirp 过滤器用，M2-C 起引入；游戏不附带，必须随包）
if [ -f "$OUT/0Harmony.dll" ]; then cp "$OUT/0Harmony.dll" "$MOD_DIR/"; fi

# UI 构建产物（cohtml 从 Mods/CityLife/ 目录加载 CityLife.mjs / 同名 .css / images）
UI_DIST="$ROOT/src/CityLife/UI/dist"
if [ -d "$UI_DIST" ]; then
  cp "$UI_DIST"/*.mjs "$MOD_DIR/" 2>/dev/null || true
  cp "$UI_DIST"/*.css "$MOD_DIR/" 2>/dev/null || true
  if [ -d "$UI_DIST/images" ]; then
    mkdir -p "$MOD_DIR/images"
    cp -r "$UI_DIST/images/." "$MOD_DIR/images/"
  fi
  echo "UI 产物已拷贝（$(ls "$UI_DIST" | tr '\n' ' ')）"
else
  echo "提示：未找到 $UI_DIST，跳过 UI 产物拷贝"
  echo "  需要面板请先构建：cd src/CityLife/UI && npm install && npm run build"
fi

echo "已部署到 $MOD_DIR"
echo "下一步："
echo "  1. Steam → 右键游戏 → 属性 → 启动选项，加 --developerMode"
echo "  2. 启动游戏，进任意城市存档"
echo "  3. 看日志：tail -f \"$USER_DIR/Logs/CityLife.log\""
