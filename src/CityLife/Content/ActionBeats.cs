using System.Collections.Generic;

namespace CityLife.Content
{
    /// <summary>
    /// 神态拍/环境声词表（§12 #72 活人感专项第一批，0 token 模板底层）：
    /// 沉默市民发"动作拍"（刷手机/看窗外…），车辆/环境发"环境声"（喇叭/引擎/风声）——
    /// <b>声音不是话语</b>：不进片段池、不耗 LLM、环境声允许重复；
    /// 动作拍按玩家原话限流"同屏同拍至多 2 个"（多次出现就有问题了）。
    /// 纯数据模块不碰游戏 API；显示侧（更小更淡的 kind=3 样式）在 BubbleWorldSpikeSystem。
    /// 如何扩展：加拍=往对应数组加行（括号由调用方统一加，词表只写裸词）；
    /// 加场景子集=加数组+Pick 里加一行 switch 臂；EventSceneSystem 联动的现场子集（录像/报警中/踮脚）挂第二批。
    /// </summary>
    public static class ActionBeats
    {
        /// <summary>场景子集键：街上（步行市民默认）/医院楼/通用。</summary>
        public enum SceneKind : byte { Street, Hospital, Generic }

        /// <summary>街上动作拍（步行为主的沉默市民）。</summary>
        private static readonly string[] k_Street =
            { "刷手机", "看窗外", "赶路", "打电话", "东张西望", "抽烟", "看表" };

        /// <summary>医院楼动作拍（候诊语境；当前无现场系统联动，医院锚点判到即用）。</summary>
        private static readonly string[] k_Hospital =
            { "刷手机", "揉膝盖", "发呆", "看叫号屏", "排队等号" };

        /// <summary>通用动作拍（场景判不出时的兜底）。</summary>
        private static readonly string[] k_Generic =
            { "刷手机", "看窗外", "发呆", "抠脑袋" };

        /// <summary>车辆环境声（#55 环境声层：快车喊话归此层不再做剧场；允许重复）。</summary>
        public static readonly string[] CarSounds = { "喇叭", "引擎" };

        /// <summary>环境环境声（楼/空旷处，少量；允许重复）。</summary>
        public static readonly string[] EnvSounds = { "风声" };

        /// <summary>动作拍取用（确定性轮换+同屏限流）：sceneKind 子集内按 salt 顺探，
        /// 同屏已 ≥maxSame 的拍跳过；子集全被限流占满 → null（调用方降级嘟囔，别硬拍）。
        /// countOnScreen=调用方给的"该拍文本当前同屏计数"查询（执行层帧级小表）。</summary>
        public static string? Pick(SceneKind sceneKind, uint salt, System.Func<string, int> countOnScreen, int maxSame = 2)
        {
            var pool = sceneKind == SceneKind.Hospital ? k_Hospital
                     : sceneKind == SceneKind.Street ? k_Street
                     : k_Generic;
            for (var i = 0; i < pool.Length; i++)
            {
                var beat = pool[(int)(salt % (uint)pool.Length + i) % pool.Length];
                if (countOnScreen(beat) < maxSame)
                    return beat;
            }
            return null;
        }
    }
}
