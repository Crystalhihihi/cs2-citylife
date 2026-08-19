import { bindValue, trigger } from "cs2/api";

// 与 C# 侧 CityLife.GameBridge.CityLifeUISystem 的数据契约：
// group = "CityLife"。
// posts（JSON v2）：FeedStore.ToJson() 的产物，形如
//   [{"a":"作者","t":"正文","k":"话题","p":"人格","n":1,
//     "e":[123,4],                       // 可选：锚点实体
//     "c":[["评论者名","评论正文"],...]   // 可选：评论（无评论的帖无此键）
//   }]（最新在后）。
export const postsBinding = bindValue<string>("CityLife", "posts", "[]");

// UI → C# 单向日志通道：UI 侧 console.log 玩家看不到，
// 关键事件经此转发进 Mod.Log（在游戏日志里能看到 [UI] 前缀）。
export const uiLog = (msg: string) => trigger("CityLife", "uiLog", msg);

// 市长发帖通道（M2-C）：面板底部输入框的正文经此进 C#，
// C# 侧追加为普通帖（a === "市长"、p === "mayor"），回流到 posts JSON。
// 入参约定：UI 侧已 trim 且保证非空；C# 侧仍应做一次防御性校验。
export const mayorPost = (text: string) => trigger("CityLife", "mayorPost", text);

// 市长回复通道（M2-C）：回复挂到帖 n 的评论串（c）上，随 posts JSON 回流。
// 线格式 "{n}|{text}"：C# 侧按第一个 | 切分（正文里允许再出现 |），并做防御性校验。
// 入参约定：text 已在 UI 侧 trim 且非空。
export const replyPost = (n: number, text: string) =>
    trigger("CityLife", "replyPost", `${n}|${text}`);

// 面板开合状态上报（feedMode 联动：openOnly/throttled 模式下 C# 据此调整生成节拍）
export const panelState = (open: boolean) => trigger("CityLife", "panelState", open ? 1 : 0);

// 活动确认弹窗（M4）：空串 "" = 不显示；非空 = JSON：
//   {"id":7,"title":"活动确认","lines":["活动：周末市集",...],"danger":false}
// lines 逐行渲染；danger=true 时标题警示色 + 首行警告样式（危险操作用，如透支财政）。
// 同一时刻最多一张，新 id 替换旧的；C# 在收到应答后清空回 ""。
export const eventConfirmBinding = bindValue<string>("CityLife", "eventConfirm", "");

// 弹窗应答通道：线格式 "{id}:{1|0}"，1=确认执行，0=取消（Esc/点压暗背景都算取消）。
// UI 侧应答后立即本地隐藏，不等 C# 清空 binding。
export const eventConfirmResult = (id: number, accept: boolean) =>
    trigger("CityLife", "eventConfirmResult", `${id}:${accept ? 1 : 0}`);

// 帖子结构（短键名与 FeedStore.ToJson 对应；e/c 为 v2 新增可选键，向后兼容 M2-A 数据）
export interface FeedPost {
    a: string;               // 作者
    t: string;               // 正文
    k: string;               // 话题
    p: string;               // 人格 id
    n: number;               // 序号
    e?: [number, number];    // 锚点实体 [index, version]，可能缺省（预留：点击聚焦）
    c?: [string, string][];  // 评论 [评论者名, 正文] 二元组，无评论的帖无此键
}
