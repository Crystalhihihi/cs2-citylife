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
