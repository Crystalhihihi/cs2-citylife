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
