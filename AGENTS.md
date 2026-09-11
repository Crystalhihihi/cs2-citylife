# AGENTS.md —— 给 coding AI 与贡献者的项目指引

开始任何改动前，**必读** [docs/specs/2026-08-16-cs2-living-city-design.md](docs/specs/2026-08-16-cs2-living-city-design.md)（唯一设计权威，含 §12 决策记录）。

## 架构铁律（违反即返工）

1. **执行层/LLM 分工**：算得准的全给执行层；LLM 只做两件事——结构化上下文 → 文本，自然语言 → 结构化意图
2. **单向阀门**：LLM 输出绝不直接改模拟数值，必须经 schema 校验 + 分档有上限的确定性映射
3. **加 System 不改游戏**：禁止反编译复制游戏系统；写回优先 prefab 参数，避免 Harmony patch 私有字段
4. **版本敏感面收敛**：所有直接触碰游戏 API 的代码只允许出现在 `GameBridge` 命名空间下，其余模块只依赖其稳定接口
5. **不替换游戏原生个体 AI**（市民/游客的行程与行为本体）；mod 改的是流量与叙事
6. **绝不阻塞模拟线程**：所有 LLM 调用走 `ICliProvider` 后台异步 one-shot

## 构建与验证

- `dotnet build`（游戏路径用环境变量 `CITIES2_PATH` 覆盖默认 Steam 路径）
- 补丁日 smoke test 清单见设计文档 §5；spike 报告归档在 `docs/spikes/`

## 代码规范

- 注释用**中文**；公共类型/扩展点必须有 XML doc 注释
- 每个扩展点（`ICliProvider`、事件包 schema、原语执行器）必须配"如何扩展"的注释或文档链接——社区贡献是本项目的主要生长方式
- 新系统（ECS System）必须遵守读侧纪律：查询 OnCreate 缓存、降频错峰、`RequireForUpdate`、排除 Temp/Deleted
- ECS 系统更新间隔（`GetUpdateInterval`）必须是 **2 的幂**——非 2 幂在 mod 初始化时抛 `System update interval not power of 2`，系统整个注册不上（2026-08-19 实机踩坑）
- **禁用 `SystemAPI.*`**（Query/GetSingleton 等）：它依赖 Unity 源码生成器在编译期生成实现，我们的纯 `dotnet build` 不跑生成器，运行时抛 `No suitable code replacement generated`（2026-08-20 CRITICAL 实锤）。单例读取用 `GetEntityQuery(...).GetSingleton<T>()`；同理别用依赖生成器的特性（IJobEntity 的自动调度参数等），手写 IJobChunk/IJob
- mod 间互操作（如 CustomChirps）一律**惰性解析**：加载顺序不定，禁止在 OnCreate 做一次性反射绑定
- 改动若涉及设计决策，先更新设计文档（含 §12 决策记录），再写码

## 目录结构

- `src/CityLife/` —— mod 本体（C#）
- `tools/GameDllDump/` —— Game.dll 元数据只读 dump 工具（版本复核用，禁止用它反编译复制游戏逻辑）
- `tools/PromptEval/` —— prompt 离线评测 harness（§12 #60：固定卡具离线发炉，猫密度/句长方差/分区熵/占位符合规等指标，改动前后对比）
- `scripts/` —— spike/实测脚本
- `docs/` —— 设计文档、发布页文案、spike 报告
