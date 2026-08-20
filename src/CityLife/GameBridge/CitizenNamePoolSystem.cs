using System.Collections.Generic;
using Game;
using Game.Citizens;
using Unity.Entities;
using Unity.Mathematics;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 市民真名池（读侧）：每 2048 帧从城市真实市民采样一批显示名（NameSystem.GetRenderedLabelName，
    /// dump 实锤），供内容导演给帖子/评论作者署名——"发帖用原版名，除非特殊注入"（2026-08-20 玩家定案）。
    /// 此前作者名用人格卡自带名（"热心大妈""种草小能手"），满屏都是那几张脸，一眼假。
    /// 纪律：跨步抽样+整体轮换（同锚点系统），只读不写；池空时消费方自行回退。
    /// </summary>
    public partial class CitizenNamePoolSystem : GameSystemBase
    {
        private const int k_MaxNames = 64;

        private EntityQuery m_CitizenQuery = default!;
        private Game.UI.NameSystem? m_NameSystem;  // 惰性：游戏自建系统，GetExisting 拿不到就等下轮
        private readonly List<string> m_Names = new();
        private int m_Offset;

        /// <summary>当前可用真名（主线程只读）。空 = 还没采到（开局/无名系统），消费方回退人格卡名。</summary>
        public IReadOnlyList<string> Names => m_Names;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 2048;

        protected override void OnUpdate()
        {
            m_NameSystem ??= World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (m_NameSystem == null)
                return;

            var arr = m_CitizenQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            m_Names.Clear();
            var stride = math.max(1, arr.Length / k_MaxNames);
            for (int i = m_Offset % stride; i < arr.Length && m_Names.Count < k_MaxNames; i += stride)
            {
                var name = m_NameSystem.GetRenderedLabelName(arr[i]);
                if (!string.IsNullOrEmpty(name))
                    m_Names.Add(name);
            }
            m_Offset++;
            arr.Dispose();
        }
    }
}
