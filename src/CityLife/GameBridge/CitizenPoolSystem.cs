using System.Collections.Generic;
using Game;
using Game.Citizens;
using Unity.Entities;
using Unity.Mathematics;

namespace CityLife.GameBridge
{
    /// <summary>一条真实市民语境：显示名 + 处境卡（"手头紧的上班族，坐公交下班回家路上（去住宅区）"）+ 采样时的市民实体。
    /// 内容导演按席位分配给模型当写作处境。Entity 是 S6 环境圈摘要的定位锚（采样时实体就在手上顺带存下；
    /// 市民会死/搬走，消费前必须 EntityManager.Exists 兜底——版本代际自动防复用，见 spike §7）。
    /// Occasion=气泡场合（§12 #60 刀① Plan B：乘车/在建筑/走路是采样时已确定事实，执行层随卡盖章，
    /// LLM 只报 card 归属不再判场合——错位率归零，Any 只剩卡号缺失/越界的 salvage 兜底）。
    /// Age=年龄段（§12 #66 儿童进气泡：池子放开 Child——气泡=说话不是发帖；"儿童不发帖"口径收窄为
    /// 信息流主炉等消费处按本字段自滤，闲聊炉/对话配对放行）。</summary>
    public readonly struct CitizenContext
    {
        public readonly string Name;
        public readonly string Context;
        public readonly Entity Entity;
        public readonly Content.BubbleOccasion Occasion;
        public readonly CitizenAge Age;

        public CitizenContext(string name, string context, Entity entity, Content.BubbleOccasion occasion, CitizenAge age)
        {
            Name = name;
            Context = context;
            Entity = entity;
            Occasion = occasion;
            Age = age;
        }
    }

    /// <summary>
    /// 市民语境池（读侧）：每 2048 帧从城市真实市民采样一批"名字+处境卡"，
    /// 供内容导演按席位分配——"创造条件，不做限制"（2026-08-20 玩家定案，狼人杀经验：
    /// 写死 10 项处境轮换是罐头，每个作者是一个真实市民的当下才是活水）。
    ///
    /// 处境卡（§12 #48：处境矩阵是内容管道 v2 的轴心，人为主、场景为调料，执行层组装零 LLM）=
    /// 是谁（身份+家境）+ 在哪/在干嘛 + 乘什么 + 去哪，仍是一句自然语言短句（≤25 字为佳）。
    /// 时间/天气/季节是全局量，归信息流炉【城市此刻】，不进处境卡；起点建筑 v1 不反查。
    ///
    /// 信号全部字段级实锤（GameDllDump + docs/spikes/2026-09-09-situation-matrix-fields.md，勿复探）：
    /// 年龄=Citizen.m_State 的 AgeBit1/2（→CitizenAge）；性别=Male 位；游客/无家可归=对应位；
    /// 工作=Worker.m_Workplace（Null=失业）；家境=HouseholdMember→Household.m_Resources（仅极端值才提，阈值待 [Pool·校准] 日志校准）；
    /// 在干嘛=TravelPurpose.m_Purpose（Purpose 枚举全量实锤；m_Data 是 int 不是目标实体，spike §2 证伪）。
    /// 处境矩阵 v1 新增信号（全部直读模拟数据，读不到就省略该维度——绝不编造）：
    /// 乘什么=CurrentTransport.m_CurrentTransport → 必须先判 Game.Vehicles.Vehicle（步行时指向行人 agent、
    ///   无 Vehicle 组件，spike §3 必须前置），再按 PersonalCar/Taxi/PublicTransport/DeliveryTruck 分类；分类失败=省略；
    /// 在室内=CurrentBuilding.m_CurrentBuilding（在建筑内才挂、离开即移除，与"在路上"互斥，spike §1）——
    ///   值可能是外部连接等非建筑实体，消费前须 HasComponent&lt;Game.Buildings.Building&gt; 兜底；
    /// 去哪=Game.Common.Target.m_Target（行程终点，行程结束即移除，spike §2）——目标是租户实体（公司/住户）时
    ///   经 Game.Buildings.PropertyRenter.m_Property 映射到房产建筑（spike §2，TripNeededSystem decomp 行 145-150）；
    /// 建筑类型=Game.Buildings 的 Residential/Commercial/Industrial/OfficeProperty 四组件（互斥挂其一）
    ///   + Game.Prefabs.SignatureBuildingData（景点/地标，实体侧空标记）+ School/Hospital 服务组件
    ///   + AttractivenessProvider（公园，CityChangeSystem 同款实锤）；全不中=省略，不编"某建筑"（spike §5）。
    /// 纪律：跨步抽样+整体轮换（同锚点系统）；跳过 MovingAway；儿童自 §12 #66 起入池
    /// （气泡=说话不是发帖，"儿童不发帖"口径收窄到信息流主炉等消费处按 Age 自滤）；只读不写。
    /// </summary>
    public partial class CitizenPoolSystem : GameSystemBase
    {
        private const int k_MaxEntries = 48;
        // 分层配额（§12 #60 刀②，治"夜晚住宅区塌缩"实机实锤——配额只超采不重排，少数派拉满即停）
        private const int k_OccasionCap = 29;  // 单场合 ≤60%
        private const int k_IdentityCap = 12;  // 同身份 ≤25%
        private const int k_SameTextCap = 4;   // 同文本卡（重复卡对内容零增量）
        private const int k_MaxScan = 384;     // 扫描预算（配额拒绝变多后防全表扫，读侧成本有界）

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
            var occCount = new int[4]; // BubbleOccasion 计数（配额+日志用）
            var idCount = new Dictionary<string, int>();
            var textCount = new Dictionary<string, int>();
            var scanned = 0;
            for (int i = m_Offset % stride; i < arr.Length && m_Entries.Count < k_MaxEntries && scanned < k_MaxScan; i += stride, scanned++)
            {
                var e = arr[i];
                var citizen = EntityManager.GetComponentData<Citizen>(e);

                // 年龄=状态位低 2 位（AgeBit1=1/AgeBit2=2 → 0-3 直映 CitizenAge）；
                // 儿童不跳过（§12 #66：气泡=说话不是发帖，池子放开 Child；信息流主炉在消费处按 Age 滤）
                var age = (CitizenAge)(int)(citizen.m_State & (CitizenFlags.AgeBit1 | CitizenFlags.AgeBit2));
                var purpose = EntityManager.HasComponent<TravelPurpose>(e)
                    ? EntityManager.GetComponentData<TravelPurpose>(e).m_Purpose
                    : Purpose.None;
                if (purpose == Purpose.MovingAway)
                    continue;

                var name = m_NameSystem.GetRenderedLabelName(e);
                if (string.IsNullOrEmpty(name))
                    continue;
                var card = Describe(EntityManager, e, citizen, age, purpose, m_NameSystem, out var occasion);
                // 配额闸：超额的多数派让位，少数派拉满预算尽量补齐（配额只超采不重排）
                if (occCount[(int)occasion] >= k_OccasionCap)
                    continue;
                var comma = card.IndexOf('，');
                var identity = comma > 0 ? card[..comma] : card;
                if (idCount.TryGetValue(identity, out var ic) && ic >= k_IdentityCap)
                    continue;
                if (textCount.TryGetValue(card, out var tc) && tc >= k_SameTextCap)
                    continue;
                m_Entries.Add(new CitizenContext(name, card, e, occasion, age));
                occCount[(int)occasion]++;
                idCount[identity] = ic + 1;
                textCount[card] = tc + 1;
            }
            m_Offset++;
            m_Cycle++;
            arr.Dispose();

            // 实机肉眼验收用：每轮采样结束打一条样例卡+场合分布（配额成效看这里）
            if (m_Entries.Count > 0)
            {
                Mod.Log.Info($"[Pool] 本轮 {m_Entries.Count} 条（走{occCount[1]}/车{occCount[2]}/室{occCount[3]}/通{occCount[0]}，扫描{scanned}），样例：\"{m_Entries[0].Context}\"（{m_Entries[0].Name}）");
                // 身份分布一行（§12 #66 儿童验收观测：小学生进没进池看这里；idCount 采样时已累计，只排个序）
                var ids = new List<KeyValuePair<string, int>>(idCount);
                ids.Sort((a, b) => b.Value.CompareTo(a.Value)); // 多的在前
                var sb = new System.Text.StringBuilder();
                foreach (var kv in ids)
                {
                    if (sb.Length > 0)
                        sb.Append('、');
                    sb.Append(kv.Key).Append('×').Append(kv.Value);
                }
                Mod.Log.Info($"[Pool] 身份分布：{sb}");
            }
            else
            {
                Mod.Log.Info("[Pool] 本轮 0 条（市民皆被跳过或城市无人）");
            }
        }

        /// <summary>
        /// 对任意市民实体出处境卡（与池采样同一条产线同一口径，一处定义别复制粘贴）。
        /// 按实体出卡的公共口：S7 剧场按人开炉曾用，§12 #52 改库存剧本后当前无调用方，
        /// 保留给 backlog 的定班底剧场等后续形态。无 Citizen 组件/MovingAway → null（调用方跳过）；
        /// 儿童自 §12 #66 起放行（气泡=说话不是发帖，闲聊炉对话配对可配到孩子）。
        /// 名字不在此处取——NameSystem 归调用方（EnvironmentDigestSystem 同款惰性解析先例）；
        /// nameSystem 只用于场所真名层（§12 #62），传 null = 场所全落类别词。
        /// 场合随卡盖章（§12 #60 刀①），失败路径落 Any。
        /// </summary>
        internal static string? DescribeCitizen(EntityManager em, Entity e, out Content.BubbleOccasion occasion, Game.UI.NameSystem? nameSystem = null)
        {
            occasion = Content.BubbleOccasion.Any;
            if (e == Entity.Null || !em.Exists(e) || !em.HasComponent<Citizen>(e))
                return null;
            var citizen = em.GetComponentData<Citizen>(e);
            // 年龄=状态位低 2 位（AgeBit1=1/AgeBit2=2 → 0-3 直映 CitizenAge）；儿童放行（§12 #66）
            var age = (CitizenAge)(int)(citizen.m_State & (CitizenFlags.AgeBit1 | CitizenFlags.AgeBit2));
            var purpose = em.HasComponent<TravelPurpose>(e)
                ? em.GetComponentData<TravelPurpose>(e).m_Purpose
                : Purpose.None;
            if (purpose == Purpose.MovingAway)
                return null;
            return Describe(em, e, citizen, age, purpose, nameSystem, out occasion);
        }

        /// <summary>处境卡组装："退休大爷，在公园里溜达" / "手头紧的上班族，坐公交下班回家路上（去「胖东来」）" / "学生，打车上学路上（去学校）"。读不到的维度整段省略。
        /// 场所显示词走 §12 #62 分级：真名（NameSystem，「」括注）>类别词；zone 通用名在 ShopNameOf 已斩断，到不了这里。
        /// 场合随组装一并盖章（§12 #60 刀① Plan B——是事实不是判断，不再劳烦 LLM 推）。
        /// static + 显式 EntityManager：池采样（OnUpdate）与按实体出卡口（DescribeCitizen）共用。</summary>
        private static string Describe(EntityManager em, Entity e, Citizen citizen, CitizenAge age, Purpose purpose, Game.UI.NameSystem? nameSystem, out Content.BubbleOccasion occasion)
        {
            // —— 身份（谁）——
            var tourist = (citizen.m_State & CitizenFlags.Tourist) != 0;
            var homeless = (citizen.m_State & CitizenFlags.Homeless) != 0;
            var male = (citizen.m_State & CitizenFlags.Male) != 0;
            string identity;
            if (tourist) identity = "游客";
            else if (homeless) identity = "无家可归者";
            else if (age == CitizenAge.Child) identity = "小学生"; // §12 #66：儿童身份档（处境措辞交给 LLM，卡上只落身份+处境事实）
            else if (age == CitizenAge.Elderly) identity = male ? "退休大爷" : "退休大妈";
            else if (age == CitizenAge.Teen || em.HasComponent<Student>(e)) identity = "学生";
            else if (em.HasComponent<Worker>(e))
                identity = em.GetComponentData<Worker>(e).m_Workplace != Entity.Null ? "上班族" : "失业中";
            else identity = "无业";

            // 家境（仅极端值才提——中不溜的不贴标签；阈值是猜的，[Pool·校准] 日志攒分布后校准）
            if (!tourist && !homeless && em.HasComponent<HouseholdMember>(e))
            {
                var hh = em.GetComponentData<HouseholdMember>(e).m_Household;
                if (em.HasComponent<Household>(hh))
                {
                    var res = em.GetComponentData<Household>(hh).m_Resources;
                    if (res < 1000) identity = "手头紧的" + identity;
                    else if (res > 20000) identity = "手头宽裕的" + identity;
                }
            }

            // —— 处境（在哪/在干嘛/乘什么/去哪）——
            var situation = DescribeSituation(em, e, purpose, nameSystem, out occasion);
            return situation.Length > 0 ? $"{identity}，{situation}" : identity;
        }

        /// <summary>处境半句：在室内→"在 XX（里）+动作"；在途中→"乘什么+路程短语+（去 XX）"。
        /// 场所显示词走 §12 #62 分级：真名（NameSystem，「」括注）>业态词（商/工/办按租户产出分，
        /// 2026-09-16 落地）>粗类词——真名经 ShopNameOf 取
        /// （zone 通用名已在那层斩断）；住宅是自己家不落名（住宅无业态），恒落类别词"住宅区"。
        /// 场合随路盖章（§12 #60 刀①）：室内=Indoor（公园/景点是开放空间=Walk，#57 公园归人）；
        /// 途中乘真载具=Vehicle，否则走路=Walk——采样时已确定的事实，LLM 不再推。</summary>
        private static string DescribeSituation(EntityManager em, Entity e, Purpose purpose, Game.UI.NameSystem? nameSystem, out Content.BubbleOccasion occasion)
        {
            // 室内：CurrentBuilding 在挂=在建筑内（行程分发时移除，spike §1）
            if (em.HasComponent<CurrentBuilding>(e))
            {
                var curBuilding = em.GetComponentData<CurrentBuilding>(e).m_CurrentBuilding;
                var place = ClassifyBuilding(em, curBuilding);
                occasion = place is "公园" or "景点" ? Content.BubbleOccasion.Walk : Content.BubbleOccasion.Indoor;
                if (place != null)
                {
                    var real = place == "住宅区" ? null : EnvironmentDigestSystem.ShopNameOf(em, nameSystem, curBuilding);
                    // §12 #62 业态细分（2026-09-16）：商/工/办的类别词升级为业态词（餐饮店/软件公司/服装厂…，
                    // 读不出业态回落粗类）；真名仍优先（分级链：真名>业态类别，住宅无业态）
                    var category = real == null && place is "商店" or "工厂" or "办公楼"
                        ? ShopOutput.BusinessWordOf(em, curBuilding) ?? place
                        : place;
                    var where = real != null ? $"「{real}」" : category;
                    // "在商店里上班" vs "在住宅区呆着"：片区/开放场所不加"里"（带真名时按类别同判）
                    var at = place is "住宅区" or "景点" ? $"在{where}" : $"在{where}里";
                    return at + IndoorActivity(purpose);
                }
                // 建筑读不出类型（外部连接/未分类）：只按目的直译，不编场所
                return PurposePhrase(purpose);
            }

            // 在途中：乘什么（步行/未分类=省略）+ 路程短语 + 目的地括注
            occasion = RealVehicleOrNull(em, e) != Entity.Null ? Content.BubbleOccasion.Vehicle : Content.BubbleOccasion.Walk;
            var s = TransportPhrase(em, e) + JourneyPhrase(purpose);
            var dest = DestinationPlace(em, e, nameSystem);
            if (dest != null)
                s += s.Length > 0 ? $"（去{dest}）" : $"在去{dest}的路上";
            return s;
        }

        /// <summary>乘什么：开私家车/打车/坐公交/开货车；步行（行人 agent，无 Vehicle 组件）与未分类载具一律省略。</summary>
        private static string TransportPhrase(EntityManager em, Entity e)
        {
            var vehicle = RealVehicleOrNull(em, e);
            if (vehicle == Entity.Null)
                return "";
            if (em.HasComponent<Game.Vehicles.PersonalCar>(vehicle)) return "开私家车";
            if (em.HasComponent<Game.Vehicles.Taxi>(vehicle)) return "打车";
            if (em.HasComponent<Game.Vehicles.PublicTransport>(vehicle)) return "坐公交";
            if (em.HasComponent<Game.Vehicles.DeliveryTruck>(vehicle)) return "开货车";
            return ""; // 服务车等其他载具：v1 不分类，省略
        }

        /// <summary>真载具或 Null：CurrentTransport 指向行人 agent（步行）时无 Vehicle 组件（spike §3 必须前置判）。</summary>
        private static Entity RealVehicleOrNull(EntityManager em, Entity e)
        {
            if (!em.HasComponent<CurrentTransport>(e))
                return Entity.Null;
            var vehicle = em.GetComponentData<CurrentTransport>(e).m_CurrentTransport;
            return vehicle != Entity.Null && em.HasComponent<Game.Vehicles.Vehicle>(vehicle) ? vehicle : Entity.Null;
        }

        /// <summary>目的地建筑显示词（§12 #62 分级）：真名（NameSystem，「」括注）优先、业态词
        /// （餐饮店/软件公司…，商/工/办按租户产出资源分，2026-09-16 落地）居中、粗类词（"商店"）兜底；
        /// 住宅是自己家不落名=类别词"住宅区"。无 Target/非建筑/未分类 → null（该维度省略）。
        /// nameSystem 传 null = 真名层整体关闭（业态层不受影响，粗类→业态照常升级）。
        /// internal static 共享：BubbleChatterSystem 车卡组卡（载具目的地）同用（一处定义别复制粘贴）。</summary>
        internal static string? DestinationPlace(EntityManager em, Entity e, Game.UI.NameSystem? nameSystem = null)
        {
            if (!em.HasComponent<Game.Common.Target>(e))
                return null;
            var target = em.GetComponentData<Game.Common.Target>(e).m_Target;
            if (target == Entity.Null)
                return null;
            // 目标是租户实体（公司/住户）时映射到其房产建筑（spike §2，TripNeededSystem decomp 行 145-150）
            if (em.HasComponent<Game.Buildings.PropertyRenter>(target))
                target = em.GetComponentData<Game.Buildings.PropertyRenter>(target).m_Property;
            var place = ClassifyBuilding(em, target);
            if (place == null || place == "住宅区")
                return place;
            // §12 #62 业态细分（2026-09-16）：商/工/办目的地类别词升级为业态词（"（去餐饮店）"）；
            // 业态层不依赖 nameSystem（真名层关闭时业态照样生效），真名仍优先（分级链：真名>业态类别）
            var category = place is "商店" or "工厂" or "办公楼"
                ? ShopOutput.BusinessWordOf(em, target) ?? place
                : place;
            if (nameSystem == null)
                return category;
            var real = EnvironmentDigestSystem.ShopNameOf(em, nameSystem, target);
            return real != null ? $"「{real}」" : category;
        }

        /// <summary>建筑类型词：景点/学校/医院/车站/住宅区/商店/工厂/办公楼/公园；非建筑（外部连接等）与未分类 → null。
        /// internal static 共享：EnvironmentDigestSystem 聚类计数直接用（一处定义，别复制粘贴）。</summary>
        internal static string? ClassifyBuilding(EntityManager em, Entity building)
        {
            if (building == Entity.Null || !em.HasComponent<Game.Buildings.Building>(building))
                return null; // 外部连接等非建筑实体兜底（spike §1）
            if (em.HasComponent<Game.Prefabs.SignatureBuildingData>(building)) return "景点";
            if (em.HasComponent<Game.Buildings.School>(building)) return "学校";
            if (em.HasComponent<Game.Buildings.Hospital>(building)) return "医院";
            // 车站（§12 #62 粗类名补齐 2026-09-16）：TransportStop 与 WaitingPassengers 同实体共存
            // （2026-09-09 spike §4 实锤）； Building 门已把 bus 站牌等非建筑物件挡在外面，只剩真车站建筑
            if (em.HasComponent<Game.Routes.TransportStop>(building)) return "车站";
            if (em.HasComponent<Game.Buildings.ResidentialProperty>(building)) return "住宅区";
            if (em.HasComponent<Game.Buildings.CommercialProperty>(building)) return "商店";
            if (em.HasComponent<Game.Buildings.IndustrialProperty>(building)) return "工厂";
            if (em.HasComponent<Game.Buildings.OfficeProperty>(building)) return "办公楼";
            if (em.HasComponent<Game.Buildings.AttractivenessProvider>(building)) return "公园";
            return null;
        }

        /// <summary>室内动作（目的直译）：缀在"在 XX（里）"后；无对应动作 → 空串（只说地点）。</summary>
        private static string IndoorActivity(Purpose purpose) => purpose switch
        {
            Purpose.Working => "上班",
            Purpose.Studying => "上课",
            Purpose.Sleeping => "睡觉",
            Purpose.Shopping => "逛街",
            Purpose.Leisure or Purpose.Relaxing => "溜达",
            Purpose.Hospital or Purpose.InHospital => "看病",
            Purpose.Sightseeing or Purpose.VisitAttractions => "看风景",
            Purpose.InEmergencyShelter => "避难",
            Purpose.None => "呆着",
            _ => "",
        };

        /// <summary>路程短语（目的直译）：只覆盖"在路上"语义的目的，其余空串。</summary>
        private static string JourneyPhrase(Purpose purpose) => purpose switch
        {
            Purpose.GoingToWork => "去上班路上",
            Purpose.GoingHome => "下班回家路上",
            Purpose.GoingToSchool => "上学路上",
            Purpose.Shopping => "去逛街路上",
            Purpose.Leisure => "出去玩路上",
            Purpose.Hospital => "去医院路上",
            Purpose.Sightseeing => "去看风景路上",
            Purpose.VisitAttractions => "去逛景点路上",
            Purpose.Traveling => "赶路",
            Purpose.EmergencyShelter => "去避难所路上",
            _ => "",
        };

        /// <summary>目的直译兜底（在建筑内但建筑类型读不出时用）：沿用升级前措辞。</summary>
        private static string PurposePhrase(Purpose purpose) => purpose switch
        {
            Purpose.Working => "正在上班",
            Purpose.GoingToWork => "去上班路上",
            Purpose.GoingHome => "下班回家路上",
            Purpose.Shopping => "逛街",
            Purpose.Leisure => "闲逛",
            Purpose.Sleeping => "在被窝刷手机",
            Purpose.Studying => "在学校",
            Purpose.GoingToSchool => "上学路上",
            Purpose.Hospital or Purpose.InHospital => "在医院",
            Purpose.None => "在家呆着",
            _ => "",
        };
    }
}
