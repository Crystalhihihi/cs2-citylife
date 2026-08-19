# CityLife 市民圈

用 LLM 让 Cities: Skylines II 的城市**活起来**——市民/商家/政府在虚拟社交平台上议论你的城市，而你（市长）说的话会被城市当真：宣布活动，市民真的聚集；景区被夸，游客真的来。

- 设计文档（含全部架构决策）：[docs/specs/2026-08-16-cs2-living-city-design.md](docs/specs/2026-08-16-cs2-living-city-design.md)
- 发布页文案草案：[docs/store-page-draft.md](docs/store-page-draft.md)
- 技术验证（spike）报告：[docs/spikes/](docs/spikes/)

## 当前状态

**M0 技术验证阶段**，尚无可玩版本。路线图见设计文档 §9。

## 构建

- 需要 .NET SDK（8.0+）
- 游戏路径默认 `D:\SteamLibrary\steamapps\common\Cities Skylines II`，用环境变量 `CITIES2_PATH` 覆盖
- `dotnet build` 即可编译；游戏程序集只引用不打包

## 部署（本地开发）

```bash
./scripts/deploy.sh           # 编译 + 复制到本地 Mods 目录
./scripts/deploy.sh disable   # 临时禁用（目录改名 .CityLife）
./scripts/deploy.sh enable    # 恢复
```

本地代码 mod = `Mods/CityLife/` 文件夹 + dll，游戏启动时自动加载，无需 playset 激活；文件夹名以点开头视为禁用。
首次使用：Steam 启动选项加 `--developerMode`（开发者菜单 + 详细日志）。
验证：进存档后看 `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CityLife.log`，出现 `[ReadProbe] 市民=N, 家庭=M` 即读侧链路打通。

## 贡献

项目按**社区优先原则**设计（设计文档 §1）：后期内容生态靠社区生长，代码注释与扩展点文档是一等交付物。三条主要贡献路径：

1. **CLI 适配**：实现 `ICliProvider` 接口即可接入新的 LLM 供给（其他 code CLI / API 计费接入），见设计文档 §4 M3
2. **事件包**：JSON/YAML 数据文件，不写代码就能新增"逆天发言→真实事件"的支持，格式随 M4 稳定后公布
3. **翻译**：发布即中英，欢迎更多语言

## 许可证

MIT，见 [LICENSE](LICENSE)。
