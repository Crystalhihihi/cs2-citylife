using Unity.Entities;

namespace CityLife.GameBridge
{
    /// <summary>
    /// 场景词层（§12 #71，2026-09-17 玩家拍板）：服务/公共建筑 → 场景词 + 话题核小组 + 推荐情绪子集。
    /// 玩家原话口径：每个建筑都有对应场景词（医院也分好几种，每种不一样）；建筑内发生的事 60% 贴场景、
    /// 40% 无关唠嗑；街上唠嗑自由发挥。60/40 闸在调用方（BubbleChatterSystem 组卡掷签），本类只出词。
    ///
    /// 读法口径（GameDllDump 2026-09-17 复核，勿凭印象改动）：实体侧 Game.Buildings 标记组件
    /// （School/Hospital/PoliceStation/FireStation/DeathcareFacility/GarbageFacility/PostFacility/Prison/
    /// ParkingFacility/Park/LeisureProvider/WelfareOffice/ResearchFacility/DisasterFacility/FirewatchTower/
    /// EmergencyShelter/ElectricityProducer/WaterPumpingStation/WaterTower/WastewaterTreatmentPlant/
    /// SewageOutlet/TelecomFacility/AdminBuilding/MaintenanceDepot/Battery/Transformer/EmergencyGenerator/
    /// ExtractorFacility/TransportStation/PublicTransportStation/CargoTransportStation/TransportDepot 等）
    /// + prefab 侧数据（PrefabRef → SchoolData.m_EducationLevel 0-3=小学/中学/高中/大学（按声明序假定，
    /// 值域待实机复核）、DeathcareFacilityData.m_LongTermStorage 墓园/火葬场、GarbageFacilityData
    /// .m_LongTermStorage 填埋/分拣、PoliceStationData.m_JailCapacity>0 警察总局、PostFacilityData
    /// .m_PostVanCapacity>0 邮局/分拣中心、TransportDepotData.m_DispatchCenter 调度中心）；焚烧由渲染名
    /// 含"焚烧"补强（无独立组件）。诊所=prefab/渲染名含 Clinic/诊所（MedicalClinic02 普查实锤存在）。
    /// 自长商业不在这里——继续走 #62 资源业态词（ShopOutput.BusinessWordOf）；住宅无场景词（#62 定案）。
    ///
    /// 如何扩展：加场景=SceneTable 加一行（组件判定+词+话核 3-5+情绪子集），组装/抽签/题词全自动；
    /// 判定全 HasComponent/prefab 数据级，别遍历嵌套。LLM 只消费词表结果，不参与分类（单向阀门）。
    /// </summary>
    public static class SceneWords
    {
        /// <summary>一行场景词：场景词 + 话题核小组（60/40 贴景卡的"｜话核："来源）+ 推荐情绪子集
        /// （贴景卡情绪签从该子集抽，不贴走 #70 日常套）。</summary>
        public sealed class Row
        {
            /// <summary>场景词（如"小学"）。</summary>
            public string Word = "";
            /// <summary>话题核小组（3-5 个词，贴景卡抽 1-2 个缀卡）。</summary>
            public string[] Cores = System.Array.Empty<string>();
            /// <summary>推荐情绪子集（可含日常套没有的词——唏嘘/沉默/疼等场景专属）。</summary>
            public string[] Moods = System.Array.Empty<string>();
        }

        /// <summary>场景词表（判定顺序=表序，先赢先得；实体标记优先、prefab 数据/名称细分随后）。</summary>
        private static readonly Row[] k_Table =
        {
            Row3("小学", "作业|考试|课间|接送|春游", "乐|烦|馋|困"),       // 配小学生身份（§12 #66/#71 联动）
            Row3("中学", "考试|补习|晚自习|社团|放学", "烦|困|急|郁闷"),
            Row3("高中", "高考|晚自习|排名|补习", "烦|困|急|麻木"),
            Row3("大学", "论文|社团|食堂|考研|宿舍", "乐|闲|期待|郁闷"),
            Row3("学校", "作业|上课|考试|放学", "烦|乐|困|急"),           // 读不到 EducationLevel 的通用兜底行
            Row3("诊所", "排队|开药|打针|问诊", "烦|疼|急"),
            Row3("医院", "挂号|排队|打针|探病|体检", "烦|疼|急|麻木"),     // 小孩怕打针的玩家口径落情绪子集
            Row3("警察总局", "报案|审讯|拘留|值班", "麻木|急|怒|郁闷"),
            Row3("派出所", "报案|调解|巡逻|值班", "烦|麻木|急"),
            Row3("消防局", "值班|出警|警铃|训练", "急|累|麻木|期待"),
            Row3("消防瞭望塔", "值班|瞭望|演练", "闲|麻木|期待"),
            Row3("墓园", "扫墓|告别|花圈|安息", "唏嘘|沉默|麻木|郁闷"),
            Row3("火葬场", "告别|火化|骨灰|讣告", "唏嘘|沉默|麻木"),
            Row3("垃圾填埋场", "臭味|苍蝇|清运|填埋", "烦|麻木|郁闷"),
            Row3("垃圾焚烧厂", "烟气|焚烧|清运|炉温", "烦|麻木|郁闷"),
            Row3("垃圾分拣中心", "分拣|回收|清运|臭味", "烦|麻木|累"),
            Row3("邮局", "包裹|快递|排队|明信片", "烦|期待|急"),
            Row3("邮政分拣中心", "分拣|包裹|夜班|传送带", "累|麻木|急"),
            Row3("停车场", "车位|收费|找车|剐蹭", "烦|急|郁闷"),
            Row3("监狱", "探视|服刑|放风|出狱", "麻木|郁闷|怕|唏嘘"),
            Row3("福利局", "救济|申请|排队|审核", "烦|郁闷|麻木|期待"),
            Row3("灾害应急局", "值班|演练|预警|物资", "麻木|急|期待"),
            Row3("研究所", "课题|数据|论文|加班", "累|麻木|期待|郁闷"),
            Row3("避难所", "床位|物资|登记|安置", "怕|郁闷|麻木|累"),
            Row3("电厂", "值班|巡检|机组|停电", "累|麻木|烦"),
            Row3("自来水厂", "水压|水质|巡检|停水", "麻木|累|烦"),
            Row3("水塔", "水压|巡检", "闲|麻木"),
            Row3("污水处理厂", "臭味|巡检|排放|夜班", "烦|麻木|累"),
            Row3("排水口", "排水|涨水|臭味", "烦|急"),
            Row3("游乐园", "排队|项目|尖叫|门票", "乐|期待|急"),
            Row3("公园", "遛弯|跳舞|拍照|下棋|喂鸽子", "乐|闲|期待"),
            Row3("景点", "拍照|打卡|门票|导游", "乐|期待|闲"),
            Row3("电信机房", "值班|信号|巡检|故障", "麻木|累|急"),
            Row3("市政厅", "办事|排队|盖章|投诉", "烦|郁闷|麻木"),
            Row3("道路养护场", "出车|修路|值班|除雪", "累|麻木|烦"),
            Row3("变电站", "值班|巡检|跳闸", "麻木|累"),
            Row3("储能电站", "值班|巡检|充放", "麻木|闲"),
            Row3("应急发电站", "值班|停电|机组", "急|麻木"),
            Row3("采集场", "巡检|采集|值班", "累|麻木"),
            Row3("火车站", "候车|晚点|检票|站台|行李", "急|期待|累|烦"),
            Row3("客运站", "等车|晚点|换乘|末班车|安检", "急|烦|累|期待"),
            Row3("货运枢纽", "装卸|集装箱|班次|调度", "累|麻木|急"),
            Row3("调度中心", "调度|排班|夜班|电台", "麻木|急|累"),
            Row3("车库", "发车|检修|排班|夜班", "累|麻木"),
            Row3("车站", "等车|晚点|换乘|末班车", "急|烦|累"), // TransportStop 兜底（整合 #62 车站粗类臂）
        };

        private static Row Row3(string word, string cores, string moods)
            => new Row { Word = word, Cores = cores.Split('|'), Moods = moods.Split('|') };

        /// <summary>建筑 → 场景词行（服务/公共建筑；商业/住宅/未分类 → null）。读不到细分口径时落同类粗词
        /// （学校读不到等级→"学校"通用行；表尾"车站"是 TransportStop 兜底）。不进热路径（组炉级低频）。</summary>
        public static Row? Of(EntityManager em, Entity building)
        {
            if (building == Entity.Null || !em.HasComponent<Game.Buildings.Building>(building))
                return null;

            // —— 教育（prefab SchoolData.m_EducationLevel 细分）——
            if (em.HasComponent<Game.Buildings.School>(building))
            {
                var level = ReadPrefabComponent<Game.Prefabs.SchoolData>(em, building)?.m_EducationLevel ?? -1;
                return level switch
                {
                    0 => k_Table[0], 1 => k_Table[1], 2 => k_Table[2], 3 => k_Table[3],
                    _ => RowOf("学校")!, // 读不到等级落通用校词行（值域按声明序假定，实机复核后校准）
                };
            }
            // —— 医疗（诊所=名称含 Clinic/诊所，普查实锤 MedicalClinic02；其余=综合医院）——
            if (em.HasComponent<Game.Buildings.Hospital>(building))
            {
                var name = PrefabNameOf(em, building);
                return RowOf(name != null && (name.Contains("Clinic") || name.Contains("诊所")) ? "诊所" : "医院");
            }
            if (em.HasComponent<Game.Buildings.PoliceStation>(building))
                return RowOf((ReadPrefabComponent<Game.Prefabs.PoliceStationData>(em, building)?.m_JailCapacity ?? 0) > 0 ? "警察总局" : "派出所");
            if (em.HasComponent<Game.Buildings.FireStation>(building)) return RowOf("消防局");
            if (em.HasComponent<Game.Buildings.FirewatchTower>(building)) return RowOf("消防瞭望塔");
            if (em.HasComponent<Game.Buildings.DeathcareFacility>(building))
                return RowOf((ReadPrefabComponent<Game.Prefabs.DeathcareFacilityData>(em, building)?.m_LongTermStorage ?? false) ? "墓园" : "火葬场");
            if (em.HasComponent<Game.Buildings.GarbageFacility>(building))
            {
                var name = PrefabNameOf(em, building);
                if (name != null && name.Contains("焚烧"))
                    return RowOf("垃圾焚烧厂");
                return RowOf((ReadPrefabComponent<Game.Prefabs.GarbageFacilityData>(em, building)?.m_LongTermStorage ?? false)
                    ? "垃圾填埋场" : "垃圾分拣中心");
            }
            if (em.HasComponent<Game.Buildings.PostFacility>(building))
                return RowOf((ReadPrefabComponent<Game.Prefabs.PostFacilityData>(em, building)?.m_PostVanCapacity ?? 0) > 0 ? "邮局" : "邮政分拣中心");
            if (em.HasComponent<Game.Buildings.ParkingFacility>(building)
                || em.HasComponent<Game.Buildings.CarParkingFacility>(building)
                || em.HasComponent<Game.Buildings.BicycleParkingFacility>(building))
                return RowOf("停车场");
            if (em.HasComponent<Game.Buildings.Prison>(building)) return RowOf("监狱");
            if (em.HasComponent<Game.Buildings.WelfareOffice>(building)) return RowOf("福利局");
            if (em.HasComponent<Game.Buildings.DisasterFacility>(building)) return RowOf("灾害应急局");
            if (em.HasComponent<Game.Buildings.ResearchFacility>(building)) return RowOf("研究所");
            if (em.HasComponent<Game.Buildings.EmergencyShelter>(building)) return RowOf("避难所");
            // —— 水电污 ——
            if (em.HasComponent<Game.Buildings.ElectricityProducer>(building)) return RowOf("电厂");
            if (em.HasComponent<Game.Buildings.WaterPumpingStation>(building)) return RowOf("自来水厂");
            if (em.HasComponent<Game.Buildings.WaterTower>(building)) return RowOf("水塔");
            if (em.HasComponent<Game.Buildings.WastewaterTreatmentPlant>(building)) return RowOf("污水处理厂");
            if (em.HasComponent<Game.Buildings.SewageOutlet>(building)) return RowOf("排水口");
            // —— 休闲 ——
            if (em.HasComponent<Game.Buildings.LeisureProvider>(building)) return RowOf("游乐园");
            if (em.HasComponent<Game.Buildings.Park>(building)) return RowOf("公园");
            if (em.HasComponent<Game.Buildings.AttractivenessProvider>(building)) return RowOf("景点");
            // —— 市政/公用 ——
            if (em.HasComponent<Game.Buildings.TelecomFacility>(building)) return RowOf("电信机房");
            if (em.HasComponent<Game.Buildings.AdminBuilding>(building)) return RowOf("市政厅");
            if (em.HasComponent<Game.Buildings.MaintenanceDepot>(building)) return RowOf("道路养护场");
            if (em.HasComponent<Game.Buildings.Battery>(building)) return RowOf("储能电站");
            if (em.HasComponent<Game.Buildings.Transformer>(building)) return RowOf("变电站");
            if (em.HasComponent<Game.Buildings.EmergencyGenerator>(building)) return RowOf("应急发电站");
            if (em.HasComponent<Game.Buildings.ExtractorFacility>(building)) return RowOf("采集场");
            // —— 交通（站场整合既有"车站"粗类臂：TransportStop 兜底；机场/港口无独立组件实锤，
            //    落客运/货运枢纽——口径见类头注释，细分待 prefab 名普查） ——
            if (em.HasComponent<Game.Buildings.PublicTransportStation>(building)) return RowOf("客运站");
            if (em.HasComponent<Game.Buildings.TransportStation>(building)) return RowOf("火车站");
            if (em.HasComponent<Game.Buildings.CargoTransportStation>(building)) return RowOf("货运枢纽");
            if (em.HasComponent<Game.Buildings.TransportDepot>(building))
                return RowOf((ReadPrefabComponent<Game.Prefabs.TransportDepotData>(em, building)?.m_DispatchCenter ?? false) ? "调度中心" : "车库");
            if (em.HasComponent<Game.Routes.TransportStop>(building)) return RowOf("车站");
            return null;
        }

        /// <summary>按词查行（组卡侧掷签命中后取话核/情绪子集用；表小线性查）。</summary>
        public static Row? RowOf(string word)
        {
            for (int i = 0; i < k_Table.Length; i++)
                if (k_Table[i].Word == word)
                    return k_Table[i];
            return null;
        }

        /// <summary>话核抽签（确定性：salt 锚定；小组里抽 1-2 个，"/" 分隔缀卡）。</summary>
        public static string PickCores(Row row, uint salt, int cardIdx)
        {
            var cores = row.Cores;
            if (cores.Length == 0)
                return "";
            var a = cores[(int)((salt + (uint)cardIdx) % (uint)cores.Length)];
            if (cores.Length > 1 && (salt + (uint)cardIdx) % 2 == 0)
            {
                var b = cores[(int)((salt + (uint)cardIdx + 1) % (uint)cores.Length)];
                if (b != a)
                    return a + "/" + b;
            }
            return a;
        }

        /// <summary>prefab 侧组件直读（PrefabRef.m_Prefab → prefab 实体 → 数据组件；读不到 → null）。</summary>
        private static T? ReadPrefabComponent<T>(EntityManager em, Entity building) where T : unmanaged, IComponentData
        {
            if (!em.HasComponent<Game.Prefabs.PrefabRef>(building))
                return null;
            var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(building).m_Prefab;
            if (prefab == Entity.Null || !em.HasComponent<T>(prefab))
                return null;
            return em.GetComponentData<T>(prefab);
        }

        /// <summary>prefab 内部名（名称细分补强用，如焚烧/Clinic；读不到 → null）。</summary>
        private static string? PrefabNameOf(EntityManager em, Entity building)
        {
            if (!em.HasComponent<Game.Prefabs.PrefabRef>(building))
                return null;
            var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(building).m_Prefab;
            if (prefab == Entity.Null || !em.HasComponent<Game.Prefabs.PrefabData>(prefab))
                return null;
            var ps = em.World.GetExistingSystemManaged<Game.Prefabs.PrefabSystem>();
            return ps != null ? ps.GetPrefabName(prefab) : null;
        }
    }
}
