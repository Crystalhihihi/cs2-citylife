using System.Collections.Generic;
using Game;
using Game.Citizens;
using Unity.Entities;
using Unity.Mathematics;

namespace CityLife.GameBridge
{
    /// <summary>一条真实市民语境：显示名 + 此刻状态（"失业中，在家呆着"）。内容导演按席位分配给模型当写作处境。</summary>
    public readonly struct CitizenContext
    {
        public readonly string Name;
        public readonly string Context;

        public CitizenContext(string name, string context)
        {
            Name = name;
            Context = context;
        }
    }

    /// <summary>
    /// 市民语境池（读侧）：每 2048 帧从城市真实市民采样一批"名字+此刻状态"，
    /// 供内容导演按席位分配——"创造条件，不做限制"（2026-08-20 玩家定案，狼人杀经验：
    /// 写死 10 项处境轮换是罐头，每个作者是一个真实市民的当下才是活水）。
    ///
    /// 信号全部字段级实锤（GameDllDump）：
    /// 年龄=Citizen.m_State 的 AgeBit1/2（→CitizenAge）；性别=Male 位；游客/无家可归=对应位；
    /// 在干嘛=TravelPurpose.m_Purpose（Purpose 枚举全量实锤）；工作=Worker.m_Workplace（Null=失业）；
    /// 家境=HouseholdMember→Household.m_Resources（仅极端值才提，阈值待 [Pool·校准] 日志校准）。
    /// 纪律：跨步抽样+整体轮换（同锚点系统）；跳过儿童（不发帖）与 MovingAway；只读不写。
    /// </summary>
    public partial class CitizenPoolSystem : GameSystemBase
    {
        private const int k_MaxEntries = 48;

        private EntityQuery m_CitizenQuery = default!;
        private Game.UI.NameSystem? m_NameSystem;  // 惰性：游戏自建系统，GetExisting 拿不到就等下轮
        private readonly List<CitizenContext> m_Entries = new();
        private int m_Offset;
        private uint m_Cycle;

        /// <summary>当前可用语境（主线程只读）。空 = 还没采到（开局/无名系统），消费方回退罐头处境池。</summary>
        public IReadOnlyList<CitizenContext> Entries => m_Entries;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.ReadOnly<HouseholdMember>(),
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
            m_Entries.Clear();
            var stride = math.max(1, arr.Length / k_MaxEntries);
            for (int i = m_Offset % stride; i < arr.Length && m_Entries.Count < k_MaxEntries; i += stride)
            {
                var e = arr[i];
                var citizen = EntityManager.GetComponentData<Citizen>(e);

                // 年龄=状态位低 2 位（AgeBit1=1/AgeBit2=2 → 0-3 直映 CitizenAge）；儿童不发帖跳过
                var age = (CitizenAge)(int)(citizen.m_State & (CitizenFlags.AgeBit1 | CitizenFlags.AgeBit2));
                if (age == CitizenAge.Child)
                    continue;
                var purpose = EntityManager.HasComponent<TravelPurpose>(e)
                    ? EntityManager.GetComponentData<TravelPurpose>(e).m_Purpose
                    : Purpose.None;
                if (purpose == Purpose.MovingAway)
                    continue;

                var name = m_NameSystem.GetRenderedLabelName(e);
                if (string.IsNullOrEmpty(name))
                    continue;
                m_Entries.Add(new CitizenContext(name, Describe(e, citizen, age, purpose)));
            }
            m_Offset++;
            m_Cycle++;
            arr.Dispose();
        }

        /// <summary>此刻状态描述："退休大爷，在家呆着" / "手头紧的上班族，下班回家路上" / "游客，正在逛街"。</summary>
        private string Describe(Entity e, Citizen citizen, CitizenAge age, Purpose purpose)
        {
            // —— 身份（谁）——
            var tourist = (citizen.m_State & CitizenFlags.Tourist) != 0;
            var homeless = (citizen.m_State & CitizenFlags.Homeless) != 0;
            var male = (citizen.m_State & CitizenFlags.Male) != 0;
            string identity;
            if (tourist) identity = "游客";
            else if (homeless) identity = "无家可归者";
            else if (age == CitizenAge.Elderly) identity = male ? "退休大爷" : "退休大妈";
            else if (age == CitizenAge.Teen || EntityManager.HasComponent<Student>(e)) identity = "学生";
            else if (EntityManager.HasComponent<Worker>(e))
                identity = EntityManager.GetComponentData<Worker>(e).m_Workplace != Entity.Null ? "上班族" : "失业中";
            else identity = "无业";

            // 家境（仅极端值才提——中不溜的不贴标签；阈值是猜的，[Pool·校准] 日志攒分布后校准）
            if (!tourist && !homeless && EntityManager.HasComponent<HouseholdMember>(e))
            {
                var hh = EntityManager.GetComponentData<HouseholdMember>(e).m_Household;
                if (EntityManager.HasComponent<Household>(hh))
                {
                    var res = EntityManager.GetComponentData<Household>(hh).m_Resources;
                    if (res < 1000) identity = "手头紧的" + identity;
                    else if (res > 20000) identity = "手头宽裕的" + identity;
                }
            }

            // —— 在干嘛（TravelPurpose 直译）——
            var doing = purpose switch
            {
                Purpose.Working => "正在上班",
                Purpose.GoingToWork => "去上班路上",
                Purpose.GoingHome => "下班回家路上",
                Purpose.Shopping => "逛街",
                Purpose.Leisure => "闲逛",
                Purpose.Sleeping => "在被窝刷手机",
                Purpose.Studying => "在学校",
                Purpose.GoingToSchool => "上学路上",
                Purpose.Hospital => "在医院",
                Purpose.None => "在家呆着",
                _ => "",
            };
            return doing.Length > 0 ? $"{identity}，{doing}" : identity;
        }
    }
}
