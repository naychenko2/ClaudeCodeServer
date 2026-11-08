using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож проводки инварианта «<c>llm</c> не отключается конфигом» (ADR-014 п.5): в
/// <c>Program.cs</c> есть вызов <c>NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration)</c>,
/// и он стоит ДО <c>AddSubsystems</c> и до <c>AddCore</c> — отказ обязан случиться раньше сборки
/// контейнера, с явной причиной, а не как «No service for type LlmProviderRegistry» глубже в старте.
///
/// Зачем текстовый сторож: тело проверки охраняет <c>NonDisablableSubsystemsTests</c>, а саму
/// проводку тестом не достать — top-level statements выполняются раньше, чем
/// <c>TestWebApplicationFactory</c> подмешивает ExtraConfig (проба: снять вызов — 4 из 4 зелёные).
/// Вариант «перенести проверку в AddCore» отвергнут: до AddCore в Program.cs уже идут побочные
/// эффекты старта (источники конфига, миграции, загрузка модулей), и отказ перестал бы быть первым.
/// Сторож читает исходник с того же дерева, что собирается; комментарии вырезаются, иначе
/// упоминание в тексте пояснения засчитывалось бы за вызов.
/// </summary>
public class NonDisablableLlmWiringGuardTests
{
    private const string ProgramPath = "backend/ClaudeHomeServer/Program.cs";

    private static readonly Regex EnsureCall = new(
        @"^NonDisablableSubsystems\.EnsureLlmEnabled\(\s*builder\.Configuration\s*\)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex AddSubsystemsCall = new(
        @"\bAddSubsystems\s*\(", RegexOptions.Compiled);

    private static readonly Regex AddCoreCall = new(
        @"\bbuilder\.Services\.AddCore\s*\(", RegexOptions.Compiled);

    [Fact]
    public void ProgramCs_ВызываетEnsureLlmEnabled_ДоAddSubsystemsИAddCore()
    {
        var text = StripComments(File.ReadAllText(Path.Combine(FindRepoRoot(), ProgramPath)));
        AssertWiring(text);
    }

    // Самопроверки на синтетических нарушителях: сторож, который не краснеет, ничего не охраняет.
    [Fact]
    public void Правило_КраснеетБезВызова()
    {
        var act = () => AssertWiring("builder.Services.AddCore(c);\nbuilder.Services.AddSubsystems(c);");
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Правило_КраснеетЕслиВызовПослеAddSubsystems()
    {
        var act = () => AssertWiring(
            "builder.Services.AddCore(c);\nbuilder.Services.AddSubsystems(c);\n" +
            "NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration);");
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Правило_ЗеленоеПриВызовеДоСборкиКонтейнера()
    {
        AssertWiring(
            "NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration);\n" +
            "builder.Services.AddCore(c);\nbuilder.Services.AddSubsystems(c);");
    }

    private static void AssertWiring(string text)
    {
        var calls = EnsureCall.Matches(text);
        calls.Count.Should().Be(1,
            "Program.cs обязан ровно раз вызвать NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration) " +
            "верхним оператором: без вызова Subsystems:llm:Enabled=false роняет старт чужим текстом DI");

        var subsystems = AddSubsystemsCall.Match(text);
        var core = AddCoreCall.Match(text);
        subsystems.Success.Should().BeTrue("сторож обязан видеть AddSubsystems, иначе порядок не проверить");
        core.Success.Should().BeTrue("сторож обязан видеть builder.Services.AddCore");

        var at = calls[0].Index;
        at.Should().BeLessThan(subsystems.Index, "отказ — до AddSubsystems");
        at.Should().BeLessThan(core.Index, "отказ — до сборки контейнера (AddCore)");
    }

    private static string StripComments(string text) =>
        Regex.Replace(text, @"//[^\r\n]*", "");

    // .git бывает файлом (worktree), поэтому проверяем и каталог, и файл
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) &&
                Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory + " не встретил .git и backend/.");
    }
}
