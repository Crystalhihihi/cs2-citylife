using Game.Common;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 原版 chirp 发布侧过滤器（M2-C 收尾）：ChirperUISystem 每次要发布"新 chirp"前，
    /// 把它们标记 Deleted 并剥掉 Created/Updated——面板与弹窗一起绝，实体级根治。
    /// 模式学 CustomChirps 的 PublishAddedChirps_FilterPatch（致谢），代码自写，且我们
    /// 自 M2-C 起不往 chirp 流发帖，所以全删不误伤自己人（key 检查都省了）。
    ///
    /// 为什么用补丁而不是关系统（2026-08-19 CRITICAL 实锤）：
    /// CreateChirpSystem.GetQueue() 有运行断言，LifePathEventSystem 等生产侧每帧调用——
    /// 关生成系统会让游戏自己抛 AssertionException。关停显示系统则弹窗路径压不住。
    /// 只有发布侧过滤是既不惹游戏、又一击致命的位置。
    /// 版本敏感点：m_CreatedChirpQuery 私有字段名随版本可能变动——AccessTools.Field 返回 null 即静默跳过。
    /// </summary>
    [HarmonyPatch(typeof(Game.UI.InGame.ChirperUISystem), "PublishAddedChirps")]
    internal static class VanillaChirpFilterPatch
    {
        private static void Prefix(Game.UI.InGame.ChirperUISystem __instance)
        {
            // 拿 UI 系统自己的"新 chirp"查询（私有字段，缺席则放弃本次过滤）
            var field = AccessTools.Field(typeof(Game.UI.InGame.ChirperUISystem), "m_CreatedChirpQuery");
            if (field == null)
                return;
            if (field.GetValue(__instance) is not EntityQuery query || query.IsEmptyIgnoreFilter)
                return;

            var em = __instance.EntityManager;
            var list = query.ToEntityArray(Allocator.Temp); // 先快照再改组件
            try
            {
                foreach (var e in list)
                {
                    if (!em.HasComponent<Deleted>(e))
                        em.AddComponent<Deleted>(e);
                    if (em.HasComponent<Created>(e))
                        em.RemoveComponent<Created>(e);
                    if (em.HasComponent<Updated>(e))
                        em.RemoveComponent<Updated>(e);
                }
            }
            finally
            {
                list.Dispose();
            }
        }
    }
}
