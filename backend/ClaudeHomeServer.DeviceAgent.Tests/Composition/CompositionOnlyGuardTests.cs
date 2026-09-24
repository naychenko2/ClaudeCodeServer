using System.Reflection;
using System.Reflection.Emit;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Terminal;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Сторож «только композиция» (ADR-016, задача 4.2): у агента нет второй версии файловых
/// сервисов. Код <c>DeviceAgent.Composition</c> файлы не читает, не пишет, не перечисляет и
/// не удаляет сам — всё это делает вертикаль Files (<see cref="FileService"/>) и Git. Себе
/// композиция оставляет только метаданные для политики путей: существование, длину, цель
/// ссылки. IL-скан по вызовам: новый <c>File.ReadAllText</c> в композиции — красный.
/// </summary>
public sealed class CompositionOnlyGuardTests
{
    private const string CompositionNamespace = "ClaudeHomeServer.DeviceAgent.Composition";

    private static readonly HashSet<Type> IoTypes =
    [
        typeof(File), typeof(Directory), typeof(FileStream), typeof(FileInfo), typeof(DirectoryInfo),
        typeof(FileSystemInfo), typeof(StreamReader), typeof(StreamWriter), typeof(FileSystemWatcher),
        typeof(System.IO.Enumeration.FileSystemEnumerable<>),
    ];

    // Метаданные для проверки пути — не файловая операция
    private static readonly HashSet<string> AllowedMembers =
    [
        "File.Exists", "Directory.Exists",
        "FileInfo..ctor", "DirectoryInfo..ctor",
        "FileInfo.get_Exists", "FileInfo.get_Length", "FileSystemInfo.get_LinkTarget", "FileSystemInfo.get_Exists",
    ];

    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static CompositionOnlyGuardTests()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = unchecked((ushort)op.Value);
            (op.Size == 1 ? OneByte : TwoByte)[v & 0xFF] = op;
        }
    }

    [Fact]
    public void Композиция_НеДелаетФайловыхОперацийСама()
    {
        var violations = CompositionMethods()
            .SelectMany(m => CalledMembers(m).Select(c => (Method: m, Called: c)))
            .Where(x => x.Called.DeclaringType is { } t && IsIoType(t)
                        && !AllowedMembers.Contains($"{t.Name}.{x.Called.Name}"))
            .Select(x => $"{x.Method.DeclaringType!.FullName}.{x.Method.Name} → {x.Called.DeclaringType!.Name}.{x.Called.Name}")
            .Distinct()
            .ToList();

        violations.Should().BeEmpty("файловые операции агента — только через вертикаль Files (FileService)");
    }

    [Fact]
    public void Сторож_НеВакуумный_ВидитВызовыКомпозиции()
    {
        // Без этого скан мог бы молча ничего не находить (не тот namespace, не те опкоды)
        var called = CompositionMethods().SelectMany(CalledMembers).ToList();

        called.Should().Contain(m => m.DeclaringType == typeof(FileService) && m.Name == nameof(FileService.ReadFile));
        called.Should().Contain(m => m.DeclaringType == typeof(FileSystemInfo) && m.Name == "get_LinkTarget");
    }

    [Fact]
    public void Сервисы_ИзВертикалей_АНеКопииВАгенте()
    {
        typeof(FileService).Assembly.GetName().Name.Should().Be("ClaudeHomeServer.Files");
        typeof(GitService).Assembly.GetName().Name.Should().Be("ClaudeHomeServer.Git");
        typeof(RecursiveDirectoryWatcher).Assembly.GetName().Name.Should().Be("ClaudeHomeServer.Core");

        var agent = typeof(AgentProjectFiles).Assembly;
        agent.GetTypes().Where(t => typeof(IProjectFiles).IsAssignableFrom(t) && !t.IsInterface)
            .Should().Equal(typeof(AgentProjectFiles));
        agent.GetTypes().Should().NotContain(t => t.Name.Contains("FileService") || t.Name.Contains("GitService"));
    }

    [Fact]
    public void РабочиеПодсистемы_ИзВертикалей_АНеКопииВАгенте()
    {
        // Задача 4.3: терминал, дев-серверы и превью, навыки — те же сборки, что на сервере
        typeof(TerminalService).Assembly.GetName().Name.Should().Be("ClaudeHomeServer.Terminal");
        typeof(DevServerService).Assembly.GetName().Name.Should().Be("ClaudeHomeServer.ProjectServices");
        typeof(ProjectServicesApi).Assembly.Should().BeSameAs(typeof(DevServerService).Assembly);
        typeof(DevServerPreviewForwarder).Assembly.Should().BeSameAs(typeof(DevServerService).Assembly);
        typeof(SkillsService).Assembly.GetName().Name.Should().Be("ClaudeHomeServer.Skills");

        typeof(AgentProjectFiles).Assembly.GetTypes().Should().NotContain(t =>
            t.Name.Contains("TerminalService") || t.Name.Contains("DevServer") || t.Name.Contains("Discovery")
            || t.Name.Contains("LaunchConfig") || t.Name.Contains("SkillsService") || t.Name.Contains("Forwarder"));
    }

    private static bool IsIoType(Type t) =>
        IoTypes.Contains(t) || (t.IsGenericType && IoTypes.Contains(t.GetGenericTypeDefinition()));

    private static IEnumerable<MethodBase> CompositionMethods() =>
        typeof(AgentProjectFiles).Assembly.GetTypes()
            .Where(t => t.Namespace == CompositionNamespace)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)));

    private static IEnumerable<MethodBase> CalledMembers(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        var pos = 0;
        while (pos < il.Length)
        {
            var b = il[pos++];
            var op = b == 0xFE ? TwoByte[il[pos++]] : OneByte[b];
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar: pos += 1; break;
                case OperandType.InlineVar: pos += 2; break;
                case OperandType.InlineI8 or OperandType.InlineR: pos += 8; break;
                case OperandType.InlineSwitch: pos += 4 + 4 * BitConverter.ToInt32(il, pos); break;
                case OperandType.InlineMethod:
                    {
                        var token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        MethodBase? called = null;
                        try
                        {
                            called = method.Module.ResolveMethod(token,
                                method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                                method.IsGenericMethod ? method.GetGenericArguments() : null);
                        }
                        catch (ArgumentException) { }
                        if (called is not null) yield return called;
                        break;
                    }
                default: pos += 4; break;
            }
        }
    }
}
