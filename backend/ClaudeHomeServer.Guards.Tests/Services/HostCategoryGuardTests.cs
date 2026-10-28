using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож метки Host (фича «Ускорение тестов», Э3). Набор Fast — ИСКЛЮЧЕНИЕ категорий
/// (<c>Category!=Host&amp;Category!=Slow&amp;…</c>), поэтому класс, который поднимает хост, но не
/// помечен <c>[Trait("Category", "Host")]</c>, молча утяжеляет быстрый гейт. Класс «поднимает
/// хост», если в его базовом типе, интерфейсах (фикстуры), полях, сигнатурах методов или IL тел
/// (вместе с async-машинами и замыканиями) встречается тип хоста: <c>TestWebApplicationFactory</c>,
/// его наследник или тип, держащий такой хост полем.
///
/// Скан — по МЕТАДАННЫМ собранных сборок всех тестовых проектов решения (список — из slnx), через
/// System.Reflection.Metadata: сборки в процесс не грузятся. Загрузка через AssemblyLoadContext
/// здесь недопустима: соседние сторожа перебирают AppDomain.GetAssemblies(), и чужие копии
/// сборок ломали им GetTypes(). Сборки нет — красный с просьбой собрать решение: молча
/// пропущенный проект был бы вакуумным проходом. Сканируется то, что СОБРАНО: после правки
/// тестов без сборки решения сторож видит старые сборки. Метка нужна на самом классе: xUnit v2
/// не наследует трейты класса.
/// </summary>
public class HostCategoryGuardTests
{
    private const string FactoryName = "ClaudeHomeServer.Tests.Helpers.TestWebApplicationFactory";

    private static readonly string Backend = FindBackend();

    [Fact]
    public void ХостовыйКласс_ПомеченHost()
    {
        // Сборки независимы — параллельно: сторож входит в набор Fast и обязан укладываться в 2 с
        var scans = TestAssemblies().ToList().AsParallel().Select(dll => HostScan.Hosted(dll, FactoryName)).ToList();
        var hostedTotal = scans.Sum(h => h.Count);
        var missing = scans.SelectMany(h => h).Where(c => !c.HasHostLabel).Select(c => c.Name).ToList();
        hostedTotal.Should().BeGreaterThan(100, "иначе сторож прошёл вакуумно: хостовых классов в решении сотни");
        // Весь список, а не первые элементы: метки ставят пачкой
        Assert.True(missing.Count == 0, $"Классы поднимают TestWebApplicationFactory без [Trait(\"Category\", \"Host\")] "
            + $"({missing.Count}) — без метки они попадут в набор Fast (Category!=Host&…) и утяжелят быстрый гейт:\n"
            + string.Join("\n", missing.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Сканер_ВидитПолеNewВТелеКонструкторДержателяИФикстуру()
    {
        var hosted = HostScan.Hosted(typeof(HostCategoryGuardTests).Assembly.Location, typeof(Probe.ProbeFactory).FullName!,
            name => !name.Contains('<') && name.Contains("+Probe+By", StringComparison.Ordinal) || name.EndsWith("+Probe+Plain", StringComparison.Ordinal)
                || name.EndsWith("+Probe+Labeled", StringComparison.Ordinal));
        hosted.Select(c => c.Name.Split('+')[^1]).Should().BeEquivalentTo(
            [nameof(Probe.ByField), nameof(Probe.ByNewInBody), nameof(Probe.ByCtor), nameof(Probe.ByHolder),
                nameof(Probe.ByFixture), nameof(Probe.Labeled)]);
        hosted.Single(c => c.Name.EndsWith("Labeled", StringComparison.Ordinal)).HasHostLabel.Should().BeTrue();
        hosted.Single(c => c.Name.EndsWith("ByField", StringComparison.Ordinal)).HasHostLabel.Should().BeFalse();
    }

    internal static IEnumerable<string> TestAssemblies()
    {
        // Свои bin/<Config>/<tfm>/ — те же у тестовых проектов
        var own = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var tfm = own.Name;
        var config = own.Parent!.Name;
        var slnx = File.ReadAllText(Path.Combine(Backend, "ClaudeHomeServer.slnx"));
        var projects = Regex.Matches(slnx, "Path=\"([^\"]+\\.Tests)/[^\"]+\\.csproj\"").Select(m => m.Groups[1].Value)
            .Where(p => !p.EndsWith(".Guards.Tests", StringComparison.Ordinal)).ToList();
        projects.Should().NotBeEmpty();
        foreach (var project in projects)
        {
            var name = Path.GetFileName(project);
            var dll = Path.Combine(Backend, project, "bin", config, tfm, name + ".dll");
            File.Exists(dll).Should().BeTrue($"нет сборки {dll}: собери решение (dotnet build backend/ClaudeHomeServer.slnx) — "
                + "без неё сторож прошёл бы проект вакуумно");
            yield return dll;
        }
    }

    private static string FindBackend()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeHomeServer.slnx"))) return dir.FullName;
        throw new InvalidOperationException("не найден backend/ClaudeHomeServer.slnx выше " + AppContext.BaseDirectory);
    }

    // Образцы для проверки самого сканера: «By*» и Labeled он обязан увидеть, Plain — нет
    internal static class Probe
    {
        public class ProbeFactory;

        private sealed class DerivedFactory : ProbeFactory;

        private sealed class Holder
        {
            public ProbeFactory? Factory = null;
        }

        public interface IFixture<T>;

        public sealed class ByField { private readonly ProbeFactory? _f = null; public object? F => _f; }

        public sealed class ByNewInBody
        {
            public static async Task<object> Run() { await Task.Yield(); return new DerivedFactory(); }
        }

        public sealed class ByCtor(ProbeFactory f) { public object F => f; }

        public sealed class ByHolder { private readonly Holder _h = new(); public object H => _h; }

        public sealed class ByFixture : IFixture<DerivedFactory>;

        public sealed class Plain { public int X => 1; }

        [Trait("Category", "Host")]
        public sealed class Labeled { public static object Make() => new DerivedFactory(); }
    }
}

/// <summary>Скан хостовых классов по метаданным сборки (без загрузки в процесс).</summary>
internal static class HostScan
{
    public sealed record HostedClass(string Name, bool HasHostLabel);

    private static readonly Dictionary<short, OperandType> Operands = BuildOperandTable();

    // candidate == null — тестовые классы (есть метод с атрибутом *FactAttribute/*TheoryAttribute)
    public static List<HostedClass> Hosted(string dll, string factoryName, Func<string, bool>? candidate = null)
    {
        var dir = Path.GetDirectoryName(dll)!;
        using var main = new Asm(dll);
        // Сборки, где ищутся базовые типы и держатели хоста: сама тестовая и общие наборы TestKit*
        var others = Directory.EnumerateFiles(dir, "ClaudeHomeServer.TestKit*.dll")
            .Where(f => !string.Equals(f, dll, StringComparison.Ordinal)).Select(f => new Asm(f)).ToList();
        try
        {
            var types = new List<TypeInfoMd>(main.Types());
            foreach (var o in others) types.AddRange(o.Types());
            var byName = types.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            bool IsCandidate(TypeInfoMd t) => candidate is null ? t.IsTestClass : candidate(t.Name);

            // Хост: фабрика и наследники (по цепочке баз в известных сборках), затем — до неподвижной
            // точки — не тестовые типы, держащие хост полем
            var host = new HashSet<string>(StringComparer.Ordinal) { factoryName };
            foreach (var t in types)
                for (var b = t.BaseName; b is not null; b = byName.TryGetValue(b, out var bt) ? bt.BaseName : null)
                    if (b == factoryName) { host.Add(t.Name); break; }
            bool grew;
            do
            {
                grew = false;
                foreach (var t in types)
                    if (!host.Contains(t.Name) && !IsCandidate(t) && t.FieldTypes().Any(host.Contains))
                        grew |= host.Add(t.Name);
            } while (grew);

            var result = new List<HostedClass>();
            foreach (var t in main.Types())
                if (IsCandidate(t) && UsesHost(t, byName, host))
                    result.Add(new HostedClass(t.Name, t.HasCategory("Host")));
            return result;
        }
        finally
        {
            foreach (var o in others) o.Dispose();
        }
    }

    // Члены, к которым обращается класс вместе со ВСЕМИ вложенными типами (фикстура-наследник
    // тоже правит хост): «Тип::Имя»
    public static HashSet<string> MembersUsedBy(string dll, string className)
    {
        using var asm = new Asm(dll);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in asm.Types().Where(t => t.Name == className || t.Name.StartsWith(className + "+", StringComparison.Ordinal)))
            members.UnionWith(t.MemberNames());
        return members;
    }

    private static bool UsesHost(TypeInfoMd t, Dictionary<string, TypeInfoMd> byName, HashSet<string> host)
    {
        for (var c = t; c is not null; c = c.BaseName is { } b && byName.TryGetValue(b, out var bt) ? bt : null)
        {
            if (c.BaseName is { } baseName && host.Contains(baseName)) return true;
            if (c.MentionedTypes().Any(host.Contains)) return true;
        }
        return false;
    }

    private static Dictionary<short, OperandType> BuildOperandTable()
    {
        var table = new Dictionary<short, OperandType>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is OpCode op) table[op.Value] = op.OperandType;
        return table;
    }

    internal sealed class Asm : IDisposable
    {
        private readonly PEReader _pe;
        public MetadataReader Md { get; }

        public Asm(string path)
        {
            _pe = new PEReader(File.OpenRead(path));
            Md = _pe.GetMetadataReader();
        }

        public IEnumerable<TypeInfoMd> Types() =>
            Md.TypeDefinitions.Select(h => new TypeInfoMd(this, h)).ToList();

        public MethodBodyBlock? Body(MethodDefinition m) =>
            m.RelativeVirtualAddress == 0 ? null : _pe.GetMethodBody(m.RelativeVirtualAddress);

        public void Dispose() => _pe.Dispose();
    }

    internal sealed class TypeInfoMd(Asm asm, TypeDefinitionHandle handle)
    {
        private readonly TypeDefinition _def = asm.Md.GetTypeDefinition(handle);
        private MetadataReader Md => asm.Md;

        public string Name { get; } = Names.Of(asm.Md, handle);

        public string? BaseName => _def.BaseType.IsNil ? null : Names.Root(Md, _def.BaseType);

        public bool IsTestClass =>
            (_def.Attributes & TypeAttributes.Abstract) == 0 && (_def.Attributes & TypeAttributes.Interface) == 0
            && !Name.Contains('<') && _def.GetMethods().Any(m =>
                Md.GetMethodDefinition(m).GetCustomAttributes().Any(a =>
                    Names.AttributeType(Md, Md.GetCustomAttribute(a)) is { } n
                    && (n.EndsWith("FactAttribute", StringComparison.Ordinal) || n.EndsWith("TheoryAttribute", StringComparison.Ordinal))));

        public bool HasCategory(string category) => _def.GetCustomAttributes().Any(h =>
        {
            var a = Md.GetCustomAttribute(h);
            if (Names.AttributeType(Md, a) != "Xunit.TraitAttribute") return false;
            var value = a.DecodeValue(StringOnlyProvider.Instance);
            return value.FixedArguments.Length == 2 && Equals(value.FixedArguments[0].Value, "Category")
                && Equals(value.FixedArguments[1].Value, category);
        });

        public IEnumerable<string> FieldTypes() => _def.GetFields().SelectMany(f =>
            Md.GetFieldDefinition(f).DecodeSignature(NamesProvider.Instance, null));

        // Члены из IL тел методов самого типа: «ОбъявляющийТип::Имя»
        public IEnumerable<string> MemberNames()
        {
            foreach (var mh in _def.GetMethods())
            {
                if (asm.Body(Md.GetMethodDefinition(mh)) is not { } body) continue;
                var il = body.GetILReader();
                foreach (var token in IlTokenHandles(il))
                    if (Names.Member(Md, token) is { } name) yield return name;
            }
        }

        // Всё, что упомянуто типом и его сгенерированными вложенными (async-машины, замыкания)
        public IEnumerable<string> MentionedTypes()
        {
            foreach (var n in Own()) yield return n;
            foreach (var nested in Generated(_def))
                foreach (var n in new TypeInfoMd(asm, nested).Own()) yield return n;
        }

        private IEnumerable<TypeDefinitionHandle> Generated(TypeDefinition def)
        {
            foreach (var h in def.GetNestedTypes())
            {
                var nested = Md.GetTypeDefinition(h);
                if (!Md.GetString(nested.Name).StartsWith('<')) continue;
                yield return h;
                foreach (var deeper in Generated(nested)) yield return deeper;
            }
        }

        private IEnumerable<string> Own()
        {
            foreach (var i in _def.GetInterfaceImplementations())
                foreach (var n in Names.All(Md, Md.GetInterfaceImplementation(i).Interface)) yield return n;
            foreach (var n in FieldTypes()) yield return n;
            foreach (var mh in _def.GetMethods())
            {
                var m = Md.GetMethodDefinition(mh);
                var sig = m.DecodeSignature(NamesProvider.Instance, null);
                foreach (var n in sig.ParameterTypes.SelectMany(p => p)) yield return n;
                foreach (var n in sig.ReturnType) yield return n;
                if (asm.Body(m) is not { } body) continue;
                if (!body.LocalSignature.IsNil)
                    foreach (var n in Md.GetStandaloneSignature(body.LocalSignature)
                                 .DecodeLocalSignature(NamesProvider.Instance, null).SelectMany(l => l))
                        yield return n;
                List<string> tokens;
                try { tokens = IlTokens(body.GetILReader()); }
                catch (BadImageFormatException e)
                {
                    throw new InvalidOperationException($"IL {Name}.{Md.GetString(m.Name)} не разобран: {e.Message}", e);
                }
                foreach (var n in tokens) yield return n;
            }
        }

        private List<string> IlTokens(BlobReader il) =>
            [.. IlTokenHandles(il).SelectMany(h => Names.All(Md, h))];

        private static List<EntityHandle> IlTokenHandles(BlobReader il)
        {
            var found = new List<EntityHandle>();
            while (il.RemainingBytes > 0)
            {
                short code = il.ReadByte();
                if (code == 0xFE) code = unchecked((short)(0xFE00 | il.ReadByte()));
                if (!Operands.TryGetValue(code, out var operand)) break;
                switch (operand)
                {
                    case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok:
                        found.Add(MetadataTokens.EntityHandle(il.ReadInt32()));
                        break;
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                        il.Offset += 1; break;
                    case OperandType.InlineVar: il.Offset += 2; break;
                    case OperandType.InlineI8 or OperandType.InlineR: il.Offset += 8; break;
                    case OperandType.InlineSwitch:
                        // Счётчик читается ДО сдвига: «Offset += 4 * ReadInt32()» взял бы Offset до чтения
                        var targets = il.ReadInt32();
                        il.Offset += 4 * targets;
                        break;
                    default: il.Offset += 4; break;
                }
            }
            return found;
        }
    }

    // Имена типов из дескрипторов метаданных: полное имя с «+» для вложенных
    internal static class Names
    {
        public static string Of(MetadataReader md, TypeDefinitionHandle h)
        {
            var d = md.GetTypeDefinition(h);
            var name = md.GetString(d.Name);
            var declaring = d.GetDeclaringType();
            return declaring.IsNil ? Join(md.GetString(d.Namespace), name) : Of(md, declaring) + "+" + name;
        }

        public static string Of(MetadataReader md, TypeReferenceHandle h)
        {
            var r = md.GetTypeReference(h);
            var name = md.GetString(r.Name);
            return r.ResolutionScope.Kind == HandleKind.TypeReference
                ? Of(md, (TypeReferenceHandle)r.ResolutionScope) + "+" + name
                : Join(md.GetString(r.Namespace), name);
        }

        private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

        // Корневое (без generic-аргументов) имя типа дескриптора
        public static string? Root(MetadataReader md, EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => Of(md, (TypeDefinitionHandle)h),
            HandleKind.TypeReference => Of(md, (TypeReferenceHandle)h),
            HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)h)
                .DecodeSignature(NamesProvider.Instance, null).FirstOrDefault(),
            _ => null,
        };

        // Все имена, которые несёт дескриптор: тип, его generic-аргументы, объявляющий тип члена
        public static IEnumerable<string> All(MetadataReader md, EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.TypeDefinition: return [Of(md, (TypeDefinitionHandle)h)];
                case HandleKind.TypeReference: return [Of(md, (TypeReferenceHandle)h)];
                case HandleKind.TypeSpecification:
                    return md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(NamesProvider.Instance, null);
                case HandleKind.MethodDefinition:
                    return [Of(md, md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType())];
                case HandleKind.FieldDefinition:
                    var fd = md.GetFieldDefinition((FieldDefinitionHandle)h);
                    return [Of(md, fd.GetDeclaringType()), .. fd.DecodeSignature(NamesProvider.Instance, null)];
                case HandleKind.MemberReference:
                    var mr = md.GetMemberReference((MemberReferenceHandle)h);
                    IEnumerable<string> parent = mr.Parent.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference
                        or HandleKind.TypeSpecification ? All(md, (EntityHandle)mr.Parent) : [];
                    return mr.GetKind() == MemberReferenceKind.Field
                        ? [.. parent, .. mr.DecodeFieldSignature(NamesProvider.Instance, null)]
                        : parent;
                case HandleKind.MethodSpecification:
                    var ms = md.GetMethodSpecification((MethodSpecificationHandle)h);
                    return [.. All(md, ms.Method), .. ms.DecodeSignature(NamesProvider.Instance, null).SelectMany(a => a)];
                default: return [];
            }
        }

        // «ОбъявляющийТип::Имя» члена из токена IL; не член — null
        public static string? Member(MetadataReader md, EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.MethodDefinition:
                    var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                    return Of(md, m.GetDeclaringType()) + "::" + md.GetString(m.Name);
                case HandleKind.MemberReference:
                    var r = md.GetMemberReference((MemberReferenceHandle)h);
                    var parent = r.Parent.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference
                        or HandleKind.TypeSpecification ? Root(md, (EntityHandle)r.Parent) : null;
                    return parent is null ? null : parent + "::" + md.GetString(r.Name);
                case HandleKind.MethodSpecification:
                    return Member(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
                default: return null;
            }
        }

        public static string? AttributeType(MetadataReader md, CustomAttribute a)
        {
            if (a.Constructor.Kind == HandleKind.MethodDefinition)
                return Of(md, md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType());
            if (a.Constructor.Kind != HandleKind.MemberReference) return null;
            var parent = md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent;
            return parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition ? Root(md, (EntityHandle)parent) : null;
        }
    }

    // Тип в сигнатуре — набор имён: сам тип и все generic-аргументы вглубь
    private sealed class NamesProvider : ISignatureTypeProvider<ImmutableArray<string>, object?>
    {
        public static readonly NamesProvider Instance = new();
        private static readonly ImmutableArray<string> None = [];

        public ImmutableArray<string> GetPrimitiveType(PrimitiveTypeCode typeCode) => None;
        public ImmutableArray<string> GetTypeFromDefinition(MetadataReader md, TypeDefinitionHandle h, byte kind) => [Names.Of(md, h)];
        public ImmutableArray<string> GetTypeFromReference(MetadataReader md, TypeReferenceHandle h, byte kind) => [Names.Of(md, h)];
        public ImmutableArray<string> GetTypeFromSpecification(MetadataReader md, object? ctx, TypeSpecificationHandle h, byte kind) =>
            md.GetTypeSpecification(h).DecodeSignature(this, ctx);
        public ImmutableArray<string> GetSZArrayType(ImmutableArray<string> e) => e;
        public ImmutableArray<string> GetArrayType(ImmutableArray<string> e, ArrayShape shape) => e;
        public ImmutableArray<string> GetByReferenceType(ImmutableArray<string> e) => e;
        public ImmutableArray<string> GetPointerType(ImmutableArray<string> e) => e;
        public ImmutableArray<string> GetPinnedType(ImmutableArray<string> e) => e;
        public ImmutableArray<string> GetGenericInstantiation(ImmutableArray<string> g, ImmutableArray<ImmutableArray<string>> args) =>
            [.. g, .. args.SelectMany(a => a)];
        public ImmutableArray<string> GetGenericMethodParameter(object? ctx, int index) => None;
        public ImmutableArray<string> GetGenericTypeParameter(object? ctx, int index) => None;
        public ImmutableArray<string> GetFunctionPointerType(MethodSignature<ImmutableArray<string>> s) => None;
        public ImmutableArray<string> GetModifiedType(ImmutableArray<string> m, ImmutableArray<string> u, bool req) => u;
    }

    // Разбор аргументов атрибута: нужны только строки (Trait("Category", "Host"))
    private sealed class StringOnlyProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly StringOnlyProvider Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader md, TypeDefinitionHandle h, byte kind) => Names.Of(md, h);
        public string GetTypeFromReference(MetadataReader md, TypeReferenceHandle h, byte kind) => Names.Of(md, h);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
