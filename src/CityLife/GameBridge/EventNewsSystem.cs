using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Events;
using Game.Prefabs;
using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 突发新闻系统（实体级话题源第一刀）：轮询 EventJournal（城市事件日志），
    /// 新事件入刊即发一条"突发"帖进信息流（FeedStore 带实体坐标，面板可"点击聚焦"，§4 M6 感知率）。
    ///
    /// 纪律（事件分类学未验证，先摸后扩）：
    /// - 一切入刊事件先打日志（prefab 内部名+追踪数据），跑一段真机把真名摸全；
    /// - 只有名字命中映射表的事件才发帖，未命中只记日志——不拿猜的名字胡说；
    /// - 首次启动只建基线不翻旧账（防读档时把历史事件当新闻刷）。
    /// </summary>
    public partial class EventNewsSystem : GameSystemBase
    {
        private EventJournalSystem m_Journal = default!;
        private PrefabSystem m_PrefabSystem = default!;
        private EntityQuery m_CitizenQuery = default!;
        private int m_LastCount;
        private bool m_Initialized;

        // 事件内部名 → 突发帖文案（初版为猜测名，真名以日志为准后扩表）
        private static readonly Dictionary<string, string> k_EventTexts = new()
        {
            { "FireEvent", "有地方着火了！消防车都过去了，希望人没事" },
            { "TrafficAccidentEvent", "路口出车祸了，围了一堆人，路过绕一下" },
            { "DeathEvent", "听说有人去世了……一路走好" },
        };

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Journal = World.GetOrCreateSystemManaged<EventJournalSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CitizenQuery = GetEntityQuery(ComponentType.ReadOnly<Citizen>());
            RequireForUpdate(m_CitizenQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128;

        protected override void OnUpdate()
        {
            var journal = m_Journal.eventJournal;
            if (!m_Initialized)
            {
                // 基线：已有事件不算新闻（防读档刷历史）
                m_LastCount = journal.Length;
                m_Initialized = true;
                return;
            }

            if (journal.Length <= m_LastCount)
            {
                m_LastCount = journal.Length; // 列表收缩（新档/重置）时跟齐
                return;
            }

            for (int i = m_LastCount; i < journal.Length; i++)
                ProcessEntry(journal[i]);
            m_LastCount = journal.Length;
        }

        private void ProcessEntry(Entity journalEntity)
        {
            var info = m_Journal.GetInfo(journalEntity);          // { m_Event, m_StartFrame }
            var prefabEntity = m_Journal.GetPrefab(journalEntity);

            string prefabName = "?";
            if (m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefab))
                prefabName = prefab.name;

            // 追踪数据全记（分类学摸底）：类型+数值对
            string tracking = "";
            if (m_Journal.TryGetData(journalEntity, out DynamicBuffer<EventJournalData> data))
            {
                for (int i = 0; i < data.Length && i < 6; i++)
                    tracking += $" {data[i].m_Type}={data[i].m_Value}";
            }
            Mod.Log.Info($"[News] 事件入刊：prefab={prefabName} entity={info.m_Event.Index}:{info.m_Event.Version}{tracking}");

            // 未知名只记不发——等日志把真名喂出来再扩表
            if (!k_EventTexts.TryGetValue(prefabName, out var text))
                return;

            // 突发帖直写信息流（锚定事件实体，面板点击可聚焦现场）
            var post = new Content.Post("现场直击", text, Content.Topic.Breaking, "live");
            Mod.Feed.Record(post, info.m_Event.Index, info.m_Event.Version);
        }
    }
}
