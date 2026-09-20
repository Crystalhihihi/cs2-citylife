# Spike 报告：车辆长对话闸可读性 + 医院事实档字段（2026-09-20，2B）

> 工具：tools/GameDllDump（MetadataLoadContext 只读元数据，「2B spike」节）+ bindings.d.ts（游戏 UI 绑定面）+ logs/census-spike-20260914（既有普查）。
> 结论先行：**①跟随/选中/乘员/司乘位全部可读，机制可做**；**②医院字段钉死但资产值域离线不可达，机制暂缓**（玩家外出，实机 dump 值回来后按阈值派生表落地）。

## ① 车辆长对话闸 —— 全部可读（双通道锁定+乘员+司乘位）

| 问题 | 读法 | 依据（dump.txt 行） |
|---|---|---|
| 玩家"锁定"实体 | **选中通道**：`Game.Tools.SelectionInfo{m_SelectionType, m_AreaType}`（实体侧选中标记组件；`SelectionElement` buffer{m_Entity} 多选列表）；**跟随通道**：`Game.Citizens.Followed{m_Priority, m_StartedFollowingAsChild}`（实体侧跟随标记——跟随的是市民，经 `Game.Creatures.CurrentVehicle` 落到所乘车辆） | L4522-4527 / L4515-4520；UI 面 `followedCitizens$/followCitizen/unfollowCitizen`（bindings.d.ts:1843-1848） |
| 乘员数 | `Game.Vehicles.Passenger : IBufferElementData{m_Passenger}`（骑手 buffer，长度=后座人数）+ `Game.Vehicles.Controller{m_Controller}`（驾驶者 creature→`Game.Creatures.Resident.m_Citizen` 回指市民）；PersonalCar 另有 `m_Keeper`（车主市民） | Passenger L3804；Controller dump 2B 节；PersonalCar dump 2B 节 |
| 出租车司乘位 | 司机=Controller.m_Controller（前排）；乘客=Passenger buffer（后座）——"乘客在后座才怼'师傅你是不是绕路了'，司机位不挨骂"可实现 | Taxi{m_TargetRequest, m_State, m_CurrentFee…}、Taxi(prefab).m_PassengerCapacity |
| 跟随兜底 | 跟随标记只挂市民不挂车（`Followed` 是 Citizen 组件）——"跟随车辆"语义=跟随车内市民→CurrentVehicle 落车；选中标记挂任意实体（车本体可选中）。双通道互补：选中车/跟随车内市民都触发 | 同上 |

降级预案（已写进实现注释）：若实机发现 SelectionInfo 不挂车（只挂区域/建筑），退选中通道为"跟随单通道"；再不行退"不做跟随闸、只做乘员数+打电话卡"——读不到就降级，别硬造。

## ② 医院事实档 —— 字段钉死，值域离线不可达，机制暂缓

- **类型层（钉死）**：`Game.Prefabs.HospitalData{m_AmbulanceCapacity, m_MedicalHelicopterCapacity, m_PatientCapacity, m_TreatmentBonus, m_HealthRange, m_TreatDiseases, m_TreatInjuries}`（2026-09-17 场景词 spike 节）；家族周边 `HealthcareParameterData`（全局参数）/`HealthcareDispatchSystem`/`Game.Simulation.HealthcareRequest{Type}`。
- **值域（断点）**：分档（诊所/社区医院/医疗中心）需要各 prefab 的**资产字段值**（容量/直升机有无/TreatX 布尔）。资产值在 CO 二进制资产里，GameDllDump/GameDecomp 只读 IL 元数据与方法体，**离线拿不到**；实机普查产物（logs/census-spike-20260914）只有 MedicalClinic02 一例（类别医院=5 但抽样未覆盖全部）。
- **结论**：阈值派生表不做（玩家警告别搞海量手动，没有值就派生不了）。实机补值路径：玩家回来后跑一次带 HospitalData 输出的普查（CensusSpikeSystem 扩一行 hospital 家族 dump：prefab 名+m_PatientCapacity+m_MedicalHelicopterCapacity+m_TreatDiseases/m_TreatInjuries——探针点已在本报告指明），拿到值域后再定分档表（与 SceneWords 并列新表，SceneWords 本体不动）与越档过滤词（诊所卡含 CT/核磁/住院部=丢）。
- 附带可复用点：救护车/直升机调度链（HealthcareDispatchSystem/HealthcareRequest）字段已钉——后续"急诊体验"类内容有抓手。

## ③ 急救号 locale —— 无 spike 阻塞

`EventSceneSystem` 受害重伤档首发固定"快打120！"（同现场 120 句只用一次，玩家"别出现多次"），en=911 预留注释。无技术阻塞。
