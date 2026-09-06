using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// РАЗВЕДОЧНЫЙ PROBE (не сторож!). Задача cd6dd658: измерить масштаб красного прогона
/// перед переводом сторожей границ с рефлексии на IL-скан (задача 8beee75e).
///
/// Probe читает ТЕЛА методов (IL-опкоды + <c>Module.Resolve*</c> по токенам операндов) и
/// печатает рёбра «вертикаль → чужой namespace», которых текущие
/// <see cref="SubsystemBoundaryTests"/> / <see cref="RootSubsystemBoundaryTests"/> НЕ видят.
/// Ничего не ассертит по существу (кроме факта непустой выборки), allow-list не правит,
/// в постоянные сторожа не встраивается — итог уезжает в
/// <c>docs/research/il-boundary-scan-2026-09.md</c> и probe можно удалить.
///
/// Зависимостей нет: <c>MethodBody.GetILAsByteArray()</c> + <c>Module.ResolveMember/ResolveType</c>
/// (BCL), якоря файл:строка — из portable PDB через <c>System.Reflection.Metadata</c> (тоже BCL).
/// </summary>
public class IlBoundaryScanProbe
{
    private readonly ITestOutputHelper _out;

    public IlBoundaryScanProbe(ITestOutputHelper output) => _out = output;

    static IlBoundaryScanProbe()
    {
        _ = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;
    }

    // ───────────────────────── опкод-таблица ─────────────────────────

    private static readonly OpCode[] SingleByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static void BuildOpcodeTables()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = unchecked((ushort)op.Value);
            if (op.Size == 1) SingleByte[v & 0xFF] = op;
            else TwoByte[v & 0xFF] = op;
        }
    }

    /// <summary>Все типы, «упомянутые» в теле метода: declaring-типы вызванных методов и
    /// полей, операнды ldtoken/newobj/castclass/isinst/box/newarr/initobj, generic-аргументы
    /// (в т.ч. <c>GetRequiredService&lt;T&gt;</c>), типы локальных переменных.</summary>
    private static IEnumerable<Type> TypesFromBody(MethodBase method)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch { yield break; }
        if (body is null) yield break;

        foreach (var local in body.LocalVariables)
            foreach (var t in Expand(local.LocalType)) yield return t;

        byte[]? il;
        try { il = body.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;

        var module = method.Module;
        Type[]? typeArgs = null, methodArgs = null;
        try { typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null; } catch { }
        try { methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null; } catch { }

        var pos = 0;
        while (pos < il.Length)
        {
            OpCode op;
            var b = il[pos++];
            if (b == 0xFE)
            {
                if (pos >= il.Length) break;
                op = TwoByte[il[pos++]];
            }
            else op = SingleByte[b];

            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: pos += 1; break;
                case OperandType.InlineVar: pos += 2; break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.ShortInlineR: pos += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: pos += 8; break;
                case OperandType.InlineSwitch:
                    {
                        if (pos + 4 > il.Length) { pos = il.Length; break; }
                        var n = BitConverter.ToInt32(il, pos);
                        pos += 4 + 4 * n;
                        break;
                    }
                case OperandType.InlineString:
                case OperandType.InlineSig: pos += 4; break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    {
                        if (pos + 4 > il.Length) { pos = il.Length; break; }
                        var token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        foreach (var t in ResolveToken(module, token, typeArgs, methodArgs))
                            yield return t;
                        break;
                    }
                default: pos = il.Length; break;
            }
        }
    }

    private static IEnumerable<Type> ResolveToken(Module module, int token, Type[]? typeArgs, Type[]? methodArgs)
    {
        MemberInfo? member = null;
        try { member = module.ResolveMember(token, typeArgs, methodArgs); }
        catch
        {
            try { member = module.ResolveType(token, typeArgs, methodArgs); } catch { }
        }
        if (member is null) yield break;

        if (member is Type type)
        {
            foreach (var t in Expand(type)) yield return t;
            yield break;
        }

        if (member.DeclaringType is { } owner)
            foreach (var t in Expand(owner)) yield return t;

        if (member is MethodInfo mi)
        {
            // Ключевой случай: sp.GetRequiredService<T>() — T живёт в generic-аргументах
            // инстанцированного метода, declaring-тип при этом чужой (Microsoft.*).
            if (mi.IsGenericMethod)
            {
                Type[] args;
                try { args = mi.GetGenericArguments(); } catch { yield break; }
                foreach (var a in args)
                    foreach (var t in Expand(a)) yield return t;
            }
        }
        else if (member is FieldInfo fi)
        {
            foreach (var t in Expand(fi.FieldType)) yield return t;
        }
    }

    private static IEnumerable<Type> Expand(Type? t)
    {
        if (t is null) yield break;
        if (t.IsByRef || t.HasElementType)
        {
            foreach (var x in Expand(t.GetElementType())) yield return x;
            if (!t.IsByRef) yield return t;
            yield break;
        }
        var nn = Nullable.GetUnderlyingType(t);
        if (nn is not null) { yield return nn; yield break; }
        yield return t;
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                foreach (var x in Expand(a)) yield return x;
    }

    // ───────────────────────── PDB: якоря файл:строка ─────────────────────────

    private sealed class PdbIndex
    {
        private readonly Dictionary<int, (string File, int Line)> _byToken = new();

        public static PdbIndex? TryLoad(Assembly asm)
        {
            try
            {
                var dll = asm.Location;
                if (string.IsNullOrEmpty(dll)) return null;
                var pdb = Path.ChangeExtension(dll, ".pdb");
                if (!File.Exists(pdb)) return null;
                using var fs = File.OpenRead(pdb);
                using var provider = MetadataReaderProvider.FromPortablePdbStream(fs, MetadataStreamOptions.PrefetchMetadata);
                var md = provider.GetMetadataReader();
                var idx = new PdbIndex();
                foreach (var h in md.MethodDebugInformation)
                {
                    var dbg = md.GetMethodDebugInformation(h);
                    if (dbg.SequencePointsBlob.IsNil) continue;
                    foreach (var sp in dbg.GetSequencePoints())
                    {
                        if (sp.IsHidden) continue;
                        var doc = md.GetDocument(sp.Document);
                        var name = md.GetString(doc.Name);
                        var mdefToken = MetadataTokens.GetToken(h.ToDefinitionHandle());
                        idx._byToken[mdefToken] = (name, sp.StartLine);
                        break;
                    }
                }
                return idx;
            }
            catch { return null; }
        }

        public string Anchor(MethodBase m)
        {
            if (_byToken.TryGetValue(m.MetadataToken, out var v))
            {
                var file = v.File.Replace('\\', '/');
                var cut = file.IndexOf("/backend/", StringComparison.OrdinalIgnoreCase);
                if (cut >= 0) file = file[(cut + 1)..];
                return $"{file}:{v.Line}";
            }
            return "(нет PDB-точки)";
        }
    }

    // ───────────────────────── выборка сборок и границ ─────────────────────────

    private static List<Assembly> ProductAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var n = a.GetName().Name;
                return n is not null
                    && (n == "ClaudeHomeServer" || n.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && n != "ClaudeHomeServer.Tests"
                    && !n.StartsWith("ClaudeHomeServer.Tests.", StringComparison.Ordinal);
            })
            .ToList();

    private static bool IsAllowed(Type type, string[] prefixes, ICollection<string> exact)
    {
        var ns = type.Namespace;
        if (ns is null) return true;
        if (type.FullName is { } fn && exact.Contains(fn)) return true;
        // Нормализация для массивов/generic-инстанцирований: FullName несёт `[]`/`[[...]]`.
        var bare = type.IsConstructedGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;
        if (bare is not null && exact.Contains(bare)) return true;
        foreach (var p in prefixes)
            if (ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal)) return true;
        return false;
    }

    private static IEnumerable<Type> TypesUnder(IEnumerable<Assembly> asms, string root, bool exactNamespaceOnly)
    {
        foreach (var a in asms)
        {
            Type[] types;
            try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            foreach (var t in types)
            {
                var ns = t.Namespace;
                if (ns is null) continue;
                if (exactNamespaceOnly ? ns == root : (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal)))
                    yield return t;
            }
        }
    }

    private static IEnumerable<MethodBase> AllMethods(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodBase[] a, b;
        try { a = t.GetMethods(F).Cast<MethodBase>().ToArray(); } catch { a = Array.Empty<MethodBase>(); }
        try { b = t.GetConstructors(F).Cast<MethodBase>().ToArray(); } catch { b = Array.Empty<MethodBase>(); }
        return a.Concat(b);
    }

    /// <summary>Что видит СЕЙЧАС рефлексионный сторож (копия CollectReferencedTypes).</summary>
    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags M = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        const BindingFlags P = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        foreach (var f in Safe(() => type.GetFields(M))) foreach (var t in Expand(f.FieldType)) yield return t;
        foreach (var c in Safe(() => type.GetConstructors(M)))
            foreach (var p in c.GetParameters()) foreach (var t in Expand(p.ParameterType)) yield return t;
        foreach (var pr in Safe(() => type.GetProperties(P))) foreach (var t in Expand(pr.PropertyType)) yield return t;
        foreach (var m in Safe(() => type.GetMethods(P)))
        {
            foreach (var t in Expand(m.ReturnType)) yield return t;
            foreach (var p in m.GetParameters()) foreach (var t in Expand(p.ParameterType)) yield return t;
        }
    }

    private static T[] Safe<T>(Func<T[]> f) { try { return f(); } catch { return Array.Empty<T>(); } }

    // ───────────────────────── сам probe ─────────────────────────

    [Fact]
    public void Probe_ПечатаетРёбраИзТелМетодов()
    {
        BuildOpcodeTables();
        var asms = ProductAssemblies();
        Assert.NotEmpty(asms);

        var pdbs = asms.ToDictionary(a => a, PdbIndex.TryLoad);

        var swTotal = Stopwatch.StartNew();
        var report = new StringBuilder();
        int totalEdges = 0, methodsScanned = 0, typesScanned = 0;
        // ребро: (вертикаль, чужой FullName) → примеры якорей
        var edges = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        void ScanVertical(string name, string root, bool exactNs, string[] prefixes, ICollection<string> exact,
                          Func<Type, bool>? skipType = null)
        {
            var visible = new HashSet<string>(StringComparer.Ordinal);
            var types = TypesUnder(asms, root, exactNs).ToList();
            foreach (var t in types)
            {
                if (skipType?.Invoke(t) == true) continue;
                typesScanned++;
                foreach (var r in SignatureTypes(t))
                    if (!IsAllowed(r, prefixes, exact) && r.FullName is { } fn) visible.Add(fn);
            }

            foreach (var t in types)
            {
                if (skipType?.Invoke(t) == true) continue;
                var pdb = pdbs.TryGetValue(t.Assembly, out var p) ? p : null;
                foreach (var m in AllMethods(t))
                {
                    methodsScanned++;
                    foreach (var r in TypesFromBody(m))
                    {
                        if (r.FullName is not { } fn) continue;
                        if (IsAllowed(r, prefixes, exact)) continue;
                        // Самоссылки внутри своего дерева уже отфильтрованы префиксом вертикали.
                        if (visible.Contains(fn)) continue; // это ребро сторож УЖЕ видит
                        var key = $"{name} → {fn}";
                        if (!edges.TryGetValue(key, out var set))
                            edges[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                        var owner = (t.FullName ?? t.Name) + "." + m.Name;
                        if (set.Count < 4) set.Add($"{owner} @ {pdb?.Anchor(m) ?? "(нет PDB)"}");
                        totalEdges++;
                    }
                }
            }
        }

        // 1) Таблица подсистемных вертикалей — источник правды публичный.
        foreach (var row in SubsystemBoundaryTests.Boundaries)
        {
            var b = (SubsystemBoundaryTests.VerticalBoundary)row[0];
            ScanVertical(b.VerticalName, b.NamespaceRoot, exactNs: false,
                b.AllowedNamespacePrefixes, new HashSet<string>(b.AllowedExactNamespaces, StringComparer.Ordinal));
        }

        // 2) Root-сторож: конфиг лежит в private static полях — читаем рефлексией,
        //    чтобы probe не расходился с источником правды.
        var rootType = typeof(RootSubsystemBoundaryTests);
        string[] Field(string n) => (string[])rootType
            .GetField(n, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        HashSet<string> Set(string n) => (HashSet<string>)rootType
            .GetField(n, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var rootPrefixes = Field("SharedAllowedPrefixes")
            .Concat(Field("RootAllowedSubVerticalPrefixes"))
            .Concat(new[] { "ClaudeHomeServer.Services" }) // peer-root разрешён сторожем
            .ToArray();
        var rootExact = Set("RootAllowedExactTypes");
        var excluded = Set("ExcludedRootTypes");

        ScanVertical("root:Services", "ClaudeHomeServer.Services", exactNs: true,
            rootPrefixes, rootExact,
            skipType: t => t.IsNested || (t.FullName is { } fn && excluded.Contains(fn)));

        swTotal.Stop();

        report.AppendLine($"# IL-boundary probe — сырой прогон");
        report.AppendLine($"сборок: {asms.Count} ({string.Join(", ", asms.Select(a => a.GetName().Name))})");
        report.AppendLine($"PDB найдено: {pdbs.Count(k => k.Value is not null)}/{pdbs.Count}");
        report.AppendLine($"типов просмотрено: {typesScanned}, методов: {methodsScanned}");
        report.AppendLine($"попаданий (не-уникальных): {totalEdges}");
        report.AppendLine($"УНИКАЛЬНЫХ РЁБЕР (невидимых рефлексии): {edges.Count}");
        report.AppendLine($"время скана: {swTotal.ElapsedMilliseconds} мс");
        report.AppendLine();
        foreach (var (k, v) in edges)
        {
            report.AppendLine($"## {k}");
            foreach (var a in v) report.AppendLine($"   - {a}");
        }

        var outPath = Path.Combine(Path.GetTempPath(), "il-boundary-probe.txt");
        File.WriteAllText(outPath, report.ToString());
        _out.WriteLine($"отчёт: {outPath}");
        _out.WriteLine(report.ToString());
    }

    /// <summary>Классификация рёбер + поиск ЦИКЛОВ «вертикаль ⇄ вертикаль» на графе
    /// «объявленные allow-list рёбра + новые IL-рёбра».</summary>
    [Fact]
    public void Probe_КлассификацияИЦиклы()
    {
        BuildOpcodeTables();
        var asms = ProductAssemblies();
        var rows = SubsystemBoundaryTests.Boundaries
            .Select(r => (SubsystemBoundaryTests.VerticalBoundary)r[0]).ToList();
        var verticalByNs = rows.ToDictionary(r => r.NamespaceRoot, r => r.VerticalName, StringComparer.Ordinal);

        string? VerticalOf(string fullName)
        {
            foreach (var (ns, name) in verticalByNs)
                if (fullName.StartsWith(ns + ".", StringComparison.Ordinal)) return name;
            return null;
        }

        // 1) Объявленные рёбра вертикаль→вертикаль (из allow-list).
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void AddEdge(string from, string to)
        {
            if (from == to) return;
            if (!graph.TryGetValue(from, out var s)) graph[from] = s = new(StringComparer.Ordinal);
            s.Add(to);
        }
        var declared = 0;
        foreach (var r in rows)
        {
            foreach (var p in r.AllowedNamespacePrefixes.Concat(r.AllowedExactNamespaces))
            {
                var target = VerticalOf(p.EndsWith(".", StringComparison.Ordinal) ? p : p + ".");
                if (target is null || target == r.VerticalName) continue;
                if (graph.TryGetValue(r.VerticalName, out var ex) && ex.Contains(target)) continue;
                AddEdge(r.VerticalName, target);
                declared++;
            }
        }

        // 2) Новые IL-рёбра.
        var buckets = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        void Bucket(string b, string edge)
        {
            if (!buckets.TryGetValue(b, out var l)) buckets[b] = l = new();
            l.Add(edge);
        }

        var newCrossVertical = new List<string>();
        foreach (var r in rows)
        {
            var prefixes = r.AllowedNamespacePrefixes;
            var exact = new HashSet<string>(r.AllowedExactNamespaces, StringComparer.Ordinal);
            var types = TypesUnder(asms, r.NamespaceRoot, false).ToList();
            var visible = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in types)
                foreach (var x in SignatureTypes(t))
                    if (!IsAllowed(x, prefixes, exact) && x.FullName is { } f) visible.Add(f);

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in types)
                foreach (var m in AllMethods(t))
                    foreach (var x in TypesFromBody(m))
                        if (x.FullName is { } f && !IsAllowed(x, prefixes, exact) && !visible.Contains(f))
                            found.Add(f);

            foreach (var f in found)
            {
                var edge = $"{r.VerticalName} → {f}";
                var target = VerticalOf(f);
                if (target is not null && target != r.VerticalName)
                {
                    Bucket("cross-vertical", edge);
                    if (!(graph.TryGetValue(r.VerticalName, out var ex) && ex.Contains(target)))
                        newCrossVertical.Add($"{r.VerticalName} → {target} ({f})");
                    AddEdge(r.VerticalName, target);
                }
                else if (f.StartsWith("ClaudeHomeServer.Protocol.", StringComparison.Ordinal)) Bucket("protocol", edge);
                else if (f.StartsWith("ClaudeHomeServer.Telemetry.", StringComparison.Ordinal)) Bucket("telemetry", edge);
                else if (f.StartsWith("ClaudeHomeServer.Controllers.", StringComparison.Ordinal)) Bucket("controllers", edge);
                else if (f.StartsWith("ClaudeHomeServer.Services.", StringComparison.Ordinal)) Bucket("root-services", edge);
                else if (f.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal)) Bucket("other-claudehome", edge);
                else Bucket("third-party", edge);
            }
        }

        // 3) Циклы: все простые цепочки длины 2 (пары) + сильно связные компоненты.
        var pairCycles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (from, tos) in graph)
            foreach (var to in tos)
                if (graph.TryGetValue(to, out var back) && back.Contains(from))
                    pairCycles.Add(string.CompareOrdinal(from, to) < 0 ? $"{from} ⇄ {to}" : $"{to} ⇄ {from}");

        _out.WriteLine($"объявленных рёбер вертикаль→вертикаль (allow-list): {declared}");
        foreach (var (b, l) in buckets)
            _out.WriteLine($"корзина {b}: {l.Count}");
        _out.WriteLine($"НОВЫХ пар вертикаль→вертикаль (которых не было в allow-list): {newCrossVertical.Count}");
        foreach (var e in newCrossVertical.Distinct().OrderBy(x => x, StringComparer.Ordinal))
            _out.WriteLine($"   NEW  {e}");
        _out.WriteLine($"ПАР-ЦИКЛОВ на итоговом графе: {pairCycles.Count}");
        foreach (var c in pairCycles) _out.WriteLine($"   ⇄  {c}");

        _out.WriteLine("");
        foreach (var (b, l) in buckets)
        {
            _out.WriteLine($"=== {b} ({l.Count}) ===");
            foreach (var e in l.OrderBy(x => x, StringComparer.Ordinal)) _out.WriteLine($"   {e}");
        }
    }

    /// <summary>Замер: сколько стоит IL-скан против текущей рефлексии.</summary>
    [Fact]
    public void Probe_Производительность()
    {
        BuildOpcodeTables();
        var asms = ProductAssemblies();
        var rows = SubsystemBoundaryTests.Boundaries
            .Select(r => (SubsystemBoundaryTests.VerticalBoundary)r[0]).ToList();

        // прогрев (JIT + метадата)
        foreach (var r in rows.Take(2))
            foreach (var t in TypesUnder(asms, r.NamespaceRoot, false))
                foreach (var m in AllMethods(t)) _ = TypesFromBody(m).Count();

        var swRefl = Stopwatch.StartNew();
        var reflCount = 0;
        foreach (var r in rows)
            foreach (var t in TypesUnder(asms, r.NamespaceRoot, false))
                reflCount += SignatureTypes(t).Count();
        swRefl.Stop();

        var swIl = Stopwatch.StartNew();
        int ilCount = 0, methods = 0;
        foreach (var r in rows)
            foreach (var t in TypesUnder(asms, r.NamespaceRoot, false))
                foreach (var m in AllMethods(t)) { methods++; ilCount += TypesFromBody(m).Count(); }
        swIl.Stop();

        var swPdb = Stopwatch.StartNew();
        var pdbs = asms.Select(PdbIndex.TryLoad).Count(x => x is not null);
        swPdb.Stop();

        _out.WriteLine($"рефлексия по сигнатурам: {swRefl.ElapsedMilliseconds} мс ({reflCount} ссылок)");
        _out.WriteLine($"IL-скан тел методов:     {swIl.ElapsedMilliseconds} мс ({methods} методов, {ilCount} ссылок)");
        _out.WriteLine($"загрузка PDB ({pdbs} шт):  {swPdb.ElapsedMilliseconds} мс");
    }

    /// <summary>Проверка известных слепых пятен из CLAUDE.md: видит ли IL-скан
    /// конкретные статические вызовы из тел методов + <c>sp.GetRequiredService&lt;T&gt;()</c>.</summary>
    [Fact]
    public void Probe_СлепыеПятна()
    {
        BuildOpcodeTables();
        var asms = ProductAssemblies();

        Type? Find(string full) => asms.Select(a => a.GetType(full, false)).FirstOrDefault(t => t is not null);

        var checks = new (string Label, string TypeName, string Needle)[]
        {
            ("DeployHost → GitService", "ClaudeHomeServer.Services.Deploy.DeployHost", "ClaudeHomeServer.Services.Git.GitService"),
            ("DeployHost → Backup.InstanceLock", "ClaudeHomeServer.Services.Deploy.DeployHost", "ClaudeHomeServer.Services.Backup.InstanceLock"),
            ("ReaderService → SsrfGuard", "ClaudeHomeServer.Services.Reader.ReaderService", "ClaudeHomeServer.Services.SsrfGuard"),
            ("Memory → SessionSummaryService", "ClaudeHomeServer.Services.Memory.PersonaMemoryAutolearnService", "ClaudeHomeServer.Services.SessionSummaryService"),
            ("Execution → TranscriptRoots", "ClaudeHomeServer.Services.Execution.DockerProcessRunner", "ClaudeHomeServer.Services.TranscriptRoots"),
            ("Llm → SpecialtyCatalog", "ClaudeHomeServer.Services.Llm.SpecialtySettingsStore", "ClaudeHomeServer.Services.SpecialtyCatalog"),
            ("Tasks → TaskHubExtensions", "ClaudeHomeServer.Services.Tasks.TaskSchedulerService", "ClaudeHomeServer.Controllers.TaskHubExtensions"),
        };

        foreach (var (label, typeName, needle) in checks)
        {
            var t = Find(typeName);
            if (t is null) { _out.WriteLine($"{label}: ТИП НЕ НАЙДЕН ({typeName})"); continue; }
            // Важно: замыкания и async-state-машины компилятор кладёт в ВЛОЖЕННЫЕ типы
            // (`Foo+<BarAsync>d__12`, `Foo+<>c__DisplayClass3_0`), поэтому статический вызов
            // из тела async-метода живёт в IL вложенного типа, а не самого Foo.
            var family = new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)).ToList();
            var hits = family
                .SelectMany(AllMethods)
                .SelectMany(m => TypesFromBody(m).Select(r => (m, r)))
                .Where(x => x.r.FullName == needle || (x.r.FullName?.StartsWith(needle + "+", StringComparison.Ordinal) ?? false))
                .Select(x => $"{x.m.DeclaringType?.Name}.{x.m.Name}")
                .Distinct().ToList();
            _out.WriteLine($"{label}: {(hits.Count > 0 ? "ВИДИТ" : "НЕ ВИДИТ")} — методы: {string.Join(", ", hits.Take(6))}");
        }

        // GetRequiredService<T>: ищем в Program-подобных типах и вертикалях любые
        // инстанцирования generic-резолвов DI и печатаем, видны ли T.
        var probes = new[]
        {
            "ClaudeHomeServer.Services.Spend.SpendSubsystem",
            "ClaudeHomeServer.Services.Knowledge.KnowledgeSubsystem",
            "ClaudeHomeServer.Services.Video.VideoSubsystem",
            "ClaudeHomeServer.Services.Deploy.DeploySubsystem",
        };
        foreach (var name in probes)
        {
            var t = Find(name);
            if (t is null) { _out.WriteLine($"DI-резолв {name}: тип не найден"); continue; }
            var seen = AllMethods(t)
                .SelectMany(TypesFromBody)
                .Select(x => x.FullName)
                .Where(f => f is not null && f.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            _out.WriteLine($"DI-резолв {name}: типов из ClaudeHomeServer.* в телах = {seen.Count}");
            foreach (var s in seen.Take(40)) _out.WriteLine($"    {s}");
        }
    }
}
