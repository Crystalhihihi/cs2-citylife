// GameDllDump —— 用 MetadataLoadContext 只读检查 CS2 的 Game.dll 元数据（不加载/执行游戏代码，不反编译方法体）。
//
// 运行方式：
//   dotnet run -c Release
//   默认游戏目录写死在下方 DEFAULT_GAME_DIR；可用环境变量 CITIES2_PATH 覆盖
//   （CITIES2_PATH 可指向游戏根目录，或直接指向 Cities2_Data\Managed 目录）。
//   输出打印到 stdout，例如： dotnet run -c Release > dump.txt

using System.Reflection;
using System.Text;

const string DEFAULT_GAME_DIR = @"D:\SteamLibrary\steamapps\common\Cities Skylines II";

string managedDir = ResolveManagedDir(DEFAULT_GAME_DIR);
if (!Directory.Exists(managedDir))
{
    Console.Error.WriteLine($"Managed 目录不存在: {managedDir}");
    return 2;
}
Console.WriteLine($"# Managed dir: {managedDir}");

var dlls = Directory.GetFiles(managedDir, "*.dll");
Console.WriteLine($"# 依赖 dll 数: {dlls.Length}");

MetadataLoadContext mlc = CreateMlc(dlls);
Assembly game;
try
{
    game = mlc.LoadFromAssemblyPath(Path.Combine(managedDir, "Game.dll"));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"加载 Game.dll 元数据失败: {ex.Message}");
    return 3;
}

// ---------- 程序集总体信息 ----------
Console.WriteLine();
Console.WriteLine("## 程序集信息");
var tfm = game.GetCustomAttributesData()
    .FirstOrDefault(a => a.AttributeType.Name == "TargetFrameworkAttribute");
Console.WriteLine($"TargetFramework: {(tfm != null ? tfm.ConstructorArguments.FirstOrDefault().Value : "(未标注)")}");
Console.WriteLine($"AssemblyName: {game.GetName().Name}, Version: {game.GetName().Version}");

Type[] allTypes;
try { allTypes = game.GetTypes(); }
catch (ReflectionTypeLoadException e) { allTypes = e.Types.Where(t => t != null).Cast<Type>().ToArray(); }
Console.WriteLine($"类型总数: {allTypes.Length}");

var byName = allTypes.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToList());

// ---------- 探测 ----------
Section("市民 Game.Citizens", () =>
{
    Dump("Game.Citizens.Citizen", fields: true);
    Dump("Game.Citizens.Household", fields: true);
    Dump("Game.Citizens.HouseholdMember", fields: true);
    Dump("Game.Citizens.Worker", fields: true);
    Dump("Game.Citizens.Student", fields: true);
    Dump("Game.Citizens.TravelPurpose", fields: true);
    Dump("Game.Citizens.TripNeeded", fields: true);
});

Section("公司 Game.Companies", () =>
{
    foreach (var t in allTypes.Where(t => t.Name.StartsWith("Profitability")).OrderBy(t => t.FullName))
        DumpType(t, fields: true);
    Dump("Game.Companies.CommercialCompany", fields: true);
    Dump("Game.Companies.WorkProvider", fields: true);
    Dump("Game.Companies.Employee", fields: true);
});

Section("事件/事故", () =>
{
    Find("AccidentSite", fields: true);
    Find("AccidentSiteSystem", methods: true);
    Find("CrimeVictim", fields: true);
    Console.WriteLine();
    Console.WriteLine("### Game.Events 命名空间类型一览（仅类型名）");
    foreach (var t in allTypes.Where(t => t.Namespace == "Game.Events").OrderBy(t => t.Name))
        Console.WriteLine($"- {t.Name} ({KindOf(t)})");
    Console.WriteLine();
    Find("EventJournalSystem", fields: true, methods: true);
    Find("EventJournalEntry", fields: true);
    Find("EventJournalData", fields: true);
    Console.WriteLine();
    Console.WriteLine("### 事件组件字段补探（2026-08-20 突发直采用：OnFire/AccidentSite 挂在什么实体上）");
    Dump("Game.Events.Fire", fields: true);
    Dump("Game.Events.OnFire", fields: true);
    Dump("Game.Events.TrafficAccident", fields: true);
    Dump("Game.Events.Event", fields: true);
    Dump("Game.Events.Destruction", fields: true);
    Dump("Game.Events.HealthEvent", fields: true);
    Dump("Game.Buildings.Building", fields: true);
    Console.WriteLine();
    Console.WriteLine("### 标志性建筑补探（2026-08-20 民意层 v2：请愿聚集点识别）");
    Find("SignatureBuilding", fields: true);
    Find("SignatureBuildingType", fields: true);
    Find("SignatureBuildingData", fields: true);
});

Section("购物链路（2026-08-20 实质性 spike：购买行为在哪个系统发生）", () =>
{
    Console.WriteLine("### 名称含 Shop/Purchase/Transaction/Trade 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("Shop") || t.Name.Contains("Purchase")
             || t.Name.Contains("Transaction") || t.Name.Contains("Trade")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    Console.WriteLine();
    Console.WriteLine("### Game.Economy 命名空间类型一览（仅类型名）");
    foreach (var t in allTypes.Where(t => t.Namespace == "Game.Economy").OrderBy(t => t.Name))
        Console.WriteLine($"- {t.Name} ({KindOf(t)})");
    Console.WriteLine();
    Console.WriteLine("### 名称含 CitizenAI/CitizenAction/Behavior 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("CitizenAI") || t.Name.Contains("CitizenAction")
             || t.Name.Contains("Behavior")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    Console.WriteLine();
    Console.WriteLine("### 购买结算核心深挖（ResourceBuyerSystem 的查询=成交触发条件）");
    Find("ResourceBuyerSystem", fields: true, methods: true);
    Find("FailedShoppingOrigin", fields: true);
    Find("EconomyUtils", methods: true);
    Console.WriteLine();
    Console.WriteLine("### 城市变化感知补探（2026-08-20：学校/医院/警察/消防/道路组件名核实）");
    Find("School", fields: false);
    Find("Hospital", fields: false);
    Find("PoliceStation", fields: false);
    Find("FireStation", fields: false);
    Find("Road", fields: false);
    Find("Abandoned", fields: true);
    Console.WriteLine();
    Console.WriteLine("### 商业产出声明补探（2026-08-20 R3.1：\"店卖什么\"不能从存货猜——投入品混入，要读 prefab 产出声明）");
    Find("IndustrialProcessData", fields: true);
    Find("ResourceStack", fields: true);
    Find("CommercialCompany", fields: true);
    Find("CompanyData", fields: true);
    Find("ServiceCompanyData", fields: true);
    Find("Storefront", fields: true);
    Console.WriteLine();
    Console.WriteLine("### 市民世界位置补探（2026-08-20 M3-spike：Citizen 实体不带 Transform，采样恒 0）");
    Find("CurrentTransport", fields: true);
    Find("CurrentVehicle", fields: true);
    Console.WriteLine("### Game.Creatures 命名空间类型一览（仅类型名）");
    foreach (var t in allTypes.Where(t => t.Namespace == "Game.Creatures").OrderBy(t => t.Name))
        Console.WriteLine($"- {t.Name} ({KindOf(t)})");
    Find("Creature", fields: true);
    Find("Resident", fields: true);
    Console.WriteLine();
    Console.WriteLine("### 车辆组件补探（2026-08-20 M3-spike：车顶气泡锚点）");
    Console.WriteLine("### Game.Vehicles 命名空间类型一览（仅类型名）");
    foreach (var t in allTypes.Where(t => t.Namespace == "Game.Vehicles").OrderBy(t => t.Name))
        Console.WriteLine($"- {t.Name} ({KindOf(t)})");
});

Section("Colossal Gizmos 世界渲染通道（2026-08-21 M3-W：着色器枚举 Hidden/Colossal/Gizmos 实锤后的 API 摸底）", () =>
{
    foreach (var dll in dlls)
    {
        Assembly asm2;
        try { asm2 = mlc.LoadFromAssemblyPath(dll); }
        catch { continue; }
        Type[] types2;
        try { types2 = asm2.GetTypes(); }
        catch (ReflectionTypeLoadException e2) { types2 = e2.Types.Where(t => t != null).Cast<Type>().ToArray(); }
        catch { continue; }
        var hits = types2.Where(t => (t.Namespace ?? "").Contains("Gizmo")).OrderBy(t => t.FullName).ToList();
        if (hits.Count == 0) continue;
        Console.WriteLine($"### [{asm2.GetName().Name}] Gizmo 命名空间类型 {hits.Count} 个");
        foreach (var t in hits.Take(40))
            DumpType(t, fields: true, methods: true);
    }
});

Section("旅游", () =>
{
    Find("TouristSpawnSystem", fields: true, methods: true);
    Find("TouristHousehold", fields: true);
    Find("TourismSystem", fields: true, methods: true);
    foreach (var t in allTypes.Where(t => t.Name.Contains("Lodging")).OrderBy(t => t.FullName))
        DumpType(t, fields: true);
    Find("Attractiveness", fields: true);
});

Section("外部连接/交通", () =>
{
    Find("OutsideConnection", fields: true);
    Find("TrafficSpawner", fields: true, methods: true);
    Find("RandomTrafficRequest", fields: true);
});

Section("聚合", () =>
{
    Find("CountHouseholdDataSystem", methods: true);
});

Section("气候", () =>
{
    Find("ClimateSystem", fields: true, methods: true);
});

Section("补充探测（第一轮结果后的追问）", () =>
{
    // Game.dll 的程序集引用（用于推断目标运行时）
    Console.WriteLine("### Game.dll 引用的程序集");
    foreach (var r in game.GetReferencedAssemblies().OrderBy(r => r.Name))
        Console.WriteLine($"  {r.Name}, Version={r.Version}");
    Console.WriteLine();

    // Citizen 没有 m_Age 字段，找年龄相关类型
    foreach (var t in allTypes.Where(t => t.Name.Contains("Age") && (t.Namespace ?? "").Contains("Citizens")).OrderBy(t => t.FullName))
        DumpType(t, fields: true);
    // 行程目的枚举（TripNeeded.m_Purpose 的取值）
    Dump("Game.Citizens.Purpose", fields: true);
    Dump("Game.Citizens.CitizenFlags", fields: true);
    // Attractiveness 全文搜索
    Console.WriteLine();
    Console.WriteLine("### 名称含 Attractiveness 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("Attractiveness")).OrderBy(t => t.FullName))
        DumpType(t, fields: t.Name is "AttractivenessParameterData" or "AttractivenessProvider" or "TerrainAttractiveness");
    Find("AttractionSystem", methods: true);
    Find("AttractivenessSystem", methods: true);
    Dump("Game.Simulation.RandomTrafficRequestFlags", fields: true);
    Dump("Game.Events.AccidentSiteFlags", fields: true);
    // Crime 相关
    Find("Crime", fields: true);
    Find("Criminal", fields: true);
});

Section("姓名系统（2026-08-20 追问：市民真实姓名怎么读）", () =>
{
    foreach (var t in allTypes.Where(t =>
                 t.Name.Contains("NameSystem") || t.Name.Contains("CitizenName")
                 || t.Name == "Name" || t.Name == "CustomName").OrderBy(t => t.FullName))
        DumpType(t, fields: true, methods: true);
});

Section("UI binding（2026-08-19 M2-A 追问：ValueBinding/TriggerBinding/UISystemBase 在哪）", () =>
{
    // 全程序集扫描：binding 基类所在 dll 不确定，逐 dll 找名字含 Binding 的类型
    foreach (var dll in dlls)
    {
        Assembly asm;
        try { asm = mlc.LoadFromAssemblyPath(dll); }
        catch { continue; }
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).Cast<Type>().ToArray(); }
        catch { continue; }
        var hits = types.Where(t =>
                t.Name.Contains("ValueBinding") || t.Name.Contains("TriggerBinding")
                || t.Name.Contains("UISystemBase")).ToList();
        foreach (var t in hits)
            Console.WriteLine($"  [{asm.GetName().Name}] {t.FullName}  ({KindOf(t)})");
    }
});

Section("经济资源2（分节防一崩全崩）", () =>
{
    Dump("Game.Economy.Resources", fields: true);         // 公司身上的资源 buffer 元素
});
Section("经济资源3", () =>
{
    foreach (var t in allTypes.Where(t => t.Name.Contains("CompanyData") || t.Name.Contains("Company")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");  // 只列名，先看有什么
});

Section("Chirp 生成链（2026-08-19 M2-C 收尾：关停生成侧用）", () =>
{
    foreach (var t in allTypes.Where(t => t.Name.Contains("Chirp")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
});

Section("财政与弹窗（2026-08-20 M4 spike：预算真扣+原生确认弹窗）", () =>
{
    // 城市钱袋子：CitySystem / 含 Money / Budget 的类型
    Console.WriteLine("### 名称含 CitySystem/Money/Budget 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("CitySystem") || t.Name.Contains("Money") || t.Name.Contains("Budget")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    Console.WriteLine();
    Dump("Game.City.CitySystem", fields: true, methods: true);
    Dump("Game.Simulation.CitySystem", fields: true, methods: true);
    // 弹窗：含 Dialog/Modal 的类型
    Console.WriteLine("### 名称含 Dialog/Modal 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("Dialog") || t.Name.Contains("Modal")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    Console.WriteLine();
    Find("MessageDialog", fields: true);
    Find("DialogSystem", fields: true, methods: true);
    Find("MessageDialogSystem", fields: true, methods: true);
    // M4 spike 追问：钱袋子组件 + 弹窗确切签名
    Dump("Game.City.PlayerMoney", fields: true);
    Console.WriteLine("### AppBindings 里含 Dialog 的方法");
    var appBindings = allTypes.FirstOrDefault(t => t.FullName == "Game.UI.AppBindings");
    if (appBindings != null)
        foreach (var m in appBindings.GetMethods().Where(m => m.Name.Contains("Dialog")))
            Console.WriteLine($"  {m}");
    Console.WriteLine("### ConfirmationDialog 构造与成员");
    Find("ConfirmationDialog", fields: true, methods: true);
    Find("DialogAction", fields: true, methods: true);
    // M4 spike 追问 2：游戏时间读取（活动时长/冷却都按游戏时间）
    Find("TimeSystem", fields: true, methods: true);
    Find("TimeSettings", fields: true);
    // M4 spike 追问 3：当前时刻（活动按游戏时钟排期：下一个 19:00 开场）
    Dump("Game.Common.TimeData", fields: true);
});

Section("通知图标渲染·类型普查（2026-08-20 调查：建筑上方悬浮图标如何画进 3D 世界）", () =>
{
    Console.WriteLine("### Game.dll 名称含 Notification 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("Notification")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    Console.WriteLine();
    Console.WriteLine("### Game.dll 名称含 Icon 的类型（不含 ICommand）");
    foreach (var t in allTypes.Where(t => t.Name.Contains("Icon")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    Console.WriteLine();
    Console.WriteLine("### Game.dll 名称含 Overlay / Billboard / WorldSpace / Sprite 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("Overlay") || t.Name.Contains("Billboard")
             || t.Name.Contains("WorldSpace") || t.Name.Contains("Sprite")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
});

Section("通知图标渲染·系统深挖", () =>
{
    Find("NotificationIconRenderSystem", fields: true, methods: true);
    Find("NotificationIconDisplaySystem", fields: true, methods: true);
    Find("OverlayRenderSystem", fields: true, methods: true);
    Find("IconFlags", fields: true);
    Find("IconElement", fields: true);
    Find("Icon", fields: true);
    Find("IconCluster", fields: true);
    Find("NotificationIconData", fields: true);
    Find("NotificationIconPrefab", fields: true);
});

Section("通知图标渲染·嵌套类型（RenderJob/Buffer/ShaderIDs 线索）", () =>
{
    foreach (var outerName in new[] { "NotificationIconRenderSystem", "NotificationIconDisplaySystem", "OverlayRenderSystem" })
    {
        if (!byName.TryGetValue(outerName, out var outers)) continue;
        foreach (var outer in outers)
        {
            Type[] nested;
            try { nested = outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
            catch { continue; }
            foreach (var nt in nested) DumpType(nt, fields: true, methods: true);
        }
    }
});

Section("跨程序集扫描·Icon/Billboard/WorldSpace/Overlay（Colossal.* 等）", () =>
{
    foreach (var dll in dlls)
    {
        var asmName = Path.GetFileNameWithoutExtension(dll);
        if (asmName == "Game") continue; // Game.dll 已单独普查
        Assembly asm2;
        try { asm2 = mlc.LoadFromAssemblyPath(dll); }
        catch { continue; }
        Type[] types2;
        try { types2 = asm2.GetTypes(); }
        catch (ReflectionTypeLoadException e2) { types2 = e2.Types.Where(t => t != null).Cast<Type>().ToArray(); }
        catch { continue; }
        var hits = types2.Where(t =>
                t.Name.Contains("Billboard") || t.Name.Contains("WorldSpace")
                || (t.Name.Contains("Icon") && !t.Name.Contains("ICommand") && !t.Name.Contains("IconDirectory"))
                || t.Name.Contains("Overlay")).OrderBy(t => t.FullName).ToList();
        if (hits.Count == 0) continue;
        Console.WriteLine($"### [{asmName}] 命中 {hits.Count} 个（仅列名）");
        foreach (var t in hits.Take(60))
            Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    }
});

Section("通知图标渲染·第三轮：图标生成 API 面（mod 可复用性）", () =>
{
    Find("NotificationIconPrefabSystem", fields: true, methods: true);
    Find("IconCommandSystem", fields: true, methods: true);
    Find("IconCommandBuffer", fields: true, methods: true);
    Find("IconAnimationSystem", fields: true, methods: true);
    Find("IconClusterSystem", fields: true, methods: true);
    Find("IconClusterLayer", fields: true);
    Find("IconPriority", fields: true);
    Find("IconLayerMask", fields: true);
    Find("DisallowCluster", fields: true);
    Find("NotificationsUtils", methods: true);
});

Section("Game.dll 嵌入资源清单（shader/贴图名线索）", () =>
{
    foreach (var n in game.GetManifestResourceNames().OrderBy(n => n))
        Console.WriteLine($"  {n}");
});

Section("通知图标渲染·第二轮：数据链路与注入点", () =>
{
    // Buffer 系统：实例数据怎么组织
    Find("NotificationIconBufferSystem", fields: true, methods: true);
    // Location 系统：世界坐标怎么定
    Find("NotificationIconLocationSystem", fields: true, methods: true);
    // 配置 prefab：Material/Shader 来源候选
    Find("IconConfigurationPrefab", fields: true, methods: true);
    Find("IconConfigurationData", fields: true);
    Find("NotificationIconDisplayData", fields: true);
    Find("IconAnimationElement", fields: true);
    Find("IconCategory", fields: true);
    // 渲染注入点：RenderingSystem 暴露什么
    Find("RenderingSystem", fields: true, methods: true);
    // 相似渲染路径参照：MarkerIconSystem
    Find("MarkerIconSystem", fields: true, methods: true);
});

Section("通知图标渲染·第二轮嵌套（BufferSystem 的 IconData 等）", () =>
{
    foreach (var outerName in new[] { "NotificationIconBufferSystem", "NotificationIconLocationSystem", "MarkerIconSystem" })
    {
        if (!byName.TryGetValue(outerName, out var outers)) continue;
        foreach (var outer in outers)
        {
            Type[] nested;
            try { nested = outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
            catch { continue; }
            foreach (var nt in nested) DumpType(nt, fields: true, methods: false);
        }
    }
});

Section("OverlayRenderSystem 文字/图元 API（2026-08-21 M3-W 调查后：世界渲染正路，社区 20+ mod 验证）", () =>
{
    Find("OverlayRenderSystem", fields: true, methods: true);
    Find("OverlayConfigurationPrefab", fields: true, methods: true);
    Find("StyleFlags", fields: true);
    Find("CustomMeshType", fields: true);
    // Buffer 嵌套类型（DrawText/DrawCircle/DrawCustomMesh 等写入 API）
    foreach (var t in allTypes.Where(t => t.Name.Contains("Buffer") && (t.Namespace ?? "").Contains("Rendering")).OrderBy(t => t.FullName))
        DumpType(t, fields: true, methods: true);
    // hideOverlay 闸调查（2026-08-21 实机：hideOverlay=True 时计数全 0）
    Find("RenderingSystem", fields: true, methods: true);
    Console.WriteLine("### 名称含 hideOverlay/SetShaderEnabled 的类型");
    foreach (var t in allTypes.Where(t => t.Name.Contains("RenderingSystem") || t.Name.Contains("Overlay")).OrderBy(t => t.FullName))
        Console.WriteLine($"  {t.FullName} ({KindOf(t)})");
    Console.WriteLine();
    Console.WriteLine("### OverlayRenderSystem 嵌套数据类型（2026-08-21 计数全 0 调查：写入被收下的条件）");
    foreach (var outer in allTypes.Where(t => t.Name == "OverlayRenderSystem"))
    {
        Type[] nested;
        try { nested = outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
        catch { continue; }
        foreach (var nt in nested) DumpType(nt, fields: true, methods: false);
    }
});

return 0;

// ================== helpers ==================

string ResolveManagedDir(string defaultDir)
{
    var root = Environment.GetEnvironmentVariable("CITIES2_PATH");
    if (string.IsNullOrWhiteSpace(root)) root = defaultDir;
    // 允许直接指向 Managed 目录
    if (File.Exists(Path.Combine(root, "Game.dll"))) return root;
    return Path.Combine(root, "Cities2_Data", "Managed");
}

MetadataLoadContext CreateMlc(string[] assemblyPaths)
{
    var resolver = new PathAssemblyResolver(assemblyPaths);
    // Unity/CS2 的 Managed 目录里核心库名字不固定，逐个试
    foreach (var core in new[] { "mscorlib", "netstandard", "System.Private.CoreLib", "System.Runtime" })
    {
        try { return new MetadataLoadContext(resolver, core); }
        catch { /* try next */ }
    }
    return new MetadataLoadContext(resolver);
}

void Section(string title, Action body)
{
    Console.WriteLine();
    Console.WriteLine($"## {title}");
    try { body(); }
    catch (Exception ex) { Console.WriteLine($"(本节 dump 出错: {ex.GetType().Name}: {ex.Message})"); }
}

void Dump(string fullName, bool fields = false, bool methods = false)
{
    var t = SafeGetType(fullName);
    if (t == null) { Console.WriteLine($"### {fullName} —— 未找到"); Console.WriteLine(); return; }
    DumpType(t, fields, methods);
}

void Find(string name, bool fields = false, bool methods = false)
{
    if (!byName.TryGetValue(name, out var list))
    {
        Console.WriteLine($"### {name} —— 未找到"); Console.WriteLine(); return;
    }
    foreach (var t in list) DumpType(t, fields, methods);
}

Type? SafeGetType(string fullName)
{
    try { return game.GetType(fullName, throwOnError: false); }
    catch { return null; }
}

void DumpType(Type t, bool fields = false, bool methods = false)
{
    Console.WriteLine();
    Console.WriteLine($"### {t.FullName}");
    Console.WriteLine($"种类: {KindOf(t)}; 基类: {SafePretty(t.BaseType)}");
    var ifaces = SafeGet(() => t.GetInterfaces().Select(i => i.Name).Distinct().ToArray()) ?? Array.Empty<string>();
    var interesting = ifaces.Where(i => i.Contains("ComponentData") || i.Contains("BufferElementData")).ToArray();
    if (interesting.Length > 0) Console.WriteLine($"接口: {string.Join(", ", interesting)}");
    if (t.IsEnum)
    {
        Console.WriteLine("枚举值:");
        foreach (var n in Enum.GetNames(t))
        {
            object? raw = null;
            try { raw = t.GetField(n)?.GetRawConstantValue(); } catch { }
            Console.WriteLine($"  {n} = {(raw != null ? Convert.ToInt64(raw) : "?")}");
        }
    }
    if (fields)
    {
        Console.WriteLine("字段:");
        FieldInfo[] fs;
        try { fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
        catch (Exception ex) { Console.WriteLine($"  (字段枚举失败: {ex.Message})"); fs = Array.Empty<FieldInfo>(); }
        if (fs.Length == 0) Console.WriteLine("  (无实例/静态字段)");
        foreach (var f in fs)
            Console.WriteLine($"  {(f.IsPublic ? "public " : "private/protected ")}{(f.IsStatic ? "static " : "")}{Pretty(f.FieldType)} {f.Name}");
    }
    if (methods)
    {
        Console.WriteLine("公开方法:");
        MethodInfo[] ms;
        try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
        catch (Exception ex) { Console.WriteLine($"  (方法枚举失败: {ex.Message})"); ms = Array.Empty<MethodInfo>(); }
        foreach (var m in ms.Where(m => !m.IsSpecialName))
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));
            Console.WriteLine($"  {Pretty(m.ReturnType)} {m.Name}({ps})");
        }
        var props = SafeGet(() => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
        if (props != null && props.Length > 0)
        {
            Console.WriteLine("公开属性:");
            foreach (var p in props)
                Console.WriteLine($"  {Pretty(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}");
        }
    }
}

string KindOf(Type t)
{
    if (t.IsEnum) return "enum";
    if (t.IsInterface) return "interface";
    if (t.IsValueType) return "struct";
    if (t.IsClass) return "class";
    return "other";
}

T? SafeGet<T>(Func<T> f) { try { return f(); } catch { return default; } }

string SafePretty(Type? t) { try { return t == null ? "(none)" : Pretty(t); } catch { return "?"; } }

string Pretty(Type t)
{
    try
    {
        if (t.IsByRef) return "ref " + Pretty(t.GetElementType()!);
        if (t.IsArray) return Pretty(t.GetElementType()!) + "[]";
        if (t.IsPointer) return Pretty(t.GetElementType()!) + "*";
        if (t.IsGenericParameter) return t.Name;
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var name = def.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            var args = string.Join(", ", t.GetGenericArguments().Select(Pretty));
            var ns = t.Namespace;
            return $"{(ns != null ? ns + "." : "")}{name}<{args}>";
        }
        return t.FullName ?? t.Name;
    }
    catch { return t.Name; }
}
