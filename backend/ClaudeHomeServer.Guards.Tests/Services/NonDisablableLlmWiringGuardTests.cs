using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож проводки инварианта «<c>llm</c> не отключается конфигом» (ADR-014 п.5): в
/// <c>Program.cs</c> есть вызов <c>NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration)</c>,
/// и он стоит ДО <c>AddSubsystems</c> и до <c>AddCore</c> — отказ обязан случиться раньше сборки
/// контейнера, с явной причиной, а не как «No service for type LlmProviderRegistry» глубже в старте.
///
/// Текущий набор границ: (1) вызов есть вне комментариев; (2) он верхний оператор — ровно одна строка
/// с колонки 0, без вложенности в if/блок; (3) нижняя граница — ПОСЛЕ подключения источника тумблеров
/// подсистем (<c>AddJsonFile(subsystemsPath…)</c>), иначе <c>Subsystems:llm:Enabled=false</c> из
/// <c>subsystems.json</c> ещё не прочитан; (4) между этим источником и вызовом, а также после вызова нет других источников
/// конфига (Add*-провайдеры, <c>Configuration.Add</c>, <c>Sources</c>); строковые литералы источником не считаются;
/// (5) верхняя граница — до <c>AddSubsystems</c> и до <c>AddCore</c>.
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

    // «Есть ли вызов вообще» и «стоит ли он верхним оператором» разведены: переформатирование
    // (отступ, перенос) не должно читаться как «вызова нет».
    private static readonly Regex AnyEnsureCall = new(
        @"\bNonDisablableSubsystems\s*\.\s*EnsureLlmEnabled\s*\(", RegexOptions.Compiled);

    private static readonly Regex TopLevelEnsureCall = new(
        @"^NonDisablableSubsystems\s*\.\s*EnsureLlmEnabled\(\s*builder\s*\.\s*Configuration\s*\)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex AddSubsystemsCall = new(
        @"\bAddSubsystems\s*\(", RegexOptions.Compiled);

    private static readonly Regex AddCoreCall = new(
        @"\bbuilder\.Services\.AddCore\s*\(", RegexOptions.Compiled);

    // Подключение источника тумблеров подсистем (data/subsystems.json): вызов обязан стоять ПОСЛЕ него.
    private static readonly Regex SubsystemsSourceCall = new(
        @"\bAddJsonFile\s*\(\s*subsystemsPath\b", RegexOptions.Compiled);

    // Любой другой источник конфига: расширения Add* провайдеров, прямое builder.Configuration.Add<T>(…)
    // и правка Sources. Ни один не должен стоять между источником подсистем и вызовом, ни после вызова.
    private static readonly Regex OtherConfigSourceCall = new(
        @"\bAdd(JsonFile|JsonStream|XmlFile|XmlStream|IniFile|IniStream|InMemoryCollection|CommandLine|" +
        @"EnvironmentVariables|UserSecrets|KeyPerFile)\s*(<[^>]*>\s*)?\(" +
        @"|\bConfiguration\s*\.\s*Add\s*[<(]" +
        @"|\bSources\s*\.\s*(Add|Insert)\s*\(", RegexOptions.Compiled);

    // Строковые литералы разбираем вместе с комментариями (в них бывает «//»), чтобы токены не склеились.
    private static readonly Regex Tokens = new(
        @"@?""(?:\\.|""""|[^""\\])*""|/\*.*?\*/|//[^\r\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void ProgramCs_ВызываетEnsureLlmEnabled_ДоAddSubsystemsИAddCore()
    {
        AssertWiring(File.ReadAllText(Path.Combine(FindRepoRoot(), ProgramPath)));
    }

    // Самопроверки на синтетических нарушителях: сторож, который не краснеет, ничего не охраняет.
    // Каждая фиксирует и КАКОЕ правило нарушено (по тексту отказа), а не только факт исключения.
    private const string Source = "builder.Configuration.AddJsonFile(subsystemsPath, optional: true);\n";
    private const string Call = "NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration);\n";
    private const string Core = "builder.Services.AddCore(c);\n";
    private const string Subsystems = "builder.Services.AddSubsystems(c);\n";

    [Fact]
    public void Правило_КраснеетБезВызова()
    {
        var act = () => AssertWiring(Source + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*нет (вне комментариев)*");
    }

    [Fact]
    public void Правило_КраснеетЕслиВызовОставленВБлочномКомментарии()
    {
        var act = () => AssertWiring(Source + "/* " + Call + "*/\n" + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*нет (вне комментариев)*");
    }

    [Fact]
    public void Правило_КраснеетЕслиВызовОставленВСтрочномКомментарии()
    {
        var act = () => AssertWiring(Source + "// " + Call + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*нет (вне комментариев)*");
    }

    [Fact]
    public void Правило_КраснеетЕслиВызовНеВерхнимОператором()
    {
        var act = () => AssertWiring(Source + "if (x)\n    " + Call + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*не верхним оператором*");
    }

    [Fact]
    public void Правило_КраснеетЕслиВызовПослеAddSubsystems()
    {
        var act = () => AssertWiring(Source + Core + Subsystems + Call);
        act.Should().Throw<Exception>().WithMessage("*до AddSubsystems*");
    }

    [Fact]
    public void Правило_КраснеетЕслиВызовМеждуAddCoreИAddSubsystems()
    {
        var act = () => AssertWiring(Source + Core + Call + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*до сборки контейнера (AddCore)*");
    }

    [Fact]
    public void Правило_КраснеетЕслиВызовДоПодключенияИсточникаПодсистем()
    {
        var act = () => AssertWiring(Call + Source + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*после источника подсистем*");
    }

    [Fact]
    public void Правило_КраснеетЕслиМеждуИсточникомПодсистемИВызовомЕстьДругойИсточникКонфига()
    {
        var act = () => AssertWiring(Source + "builder.Configuration.AddInMemoryCollection(x);\n" + Call + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*после всех источников конфига*");
    }

    [Theory]
    [InlineData("builder.Configuration.AddCommandLine(args);")]
    [InlineData("builder.Configuration.AddEnvironmentVariables();")]
    [InlineData("builder.Configuration.AddXmlFile(\"a.xml\");")]
    [InlineData("builder.Configuration.AddIniFile(\"a.ini\");")]
    [InlineData("builder.Configuration.AddUserSecrets<Program>();")]
    [InlineData("builder.Configuration.AddJsonStream(s);")]
    [InlineData("builder.Configuration.Sources.Add(src);")]
    public void Правило_КраснеетНаДругихИсточникахКонфигаМеждуИсточникомИВызовом(string extra)
    {
        var act = () => AssertWiring(Source + extra + "\n" + Call + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*после всех источников конфига*");
    }

    [Fact]
    public void Правило_КраснеетЕслиИсточникКонфигаДобавленПослеВызова()
    {
        var act = () => AssertWiring(Source + Call + "builder.Configuration.AddJsonFile(\"appsettings.Extra.json\", optional: true);\n" + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*ни одного источника конфига после вызова*");
    }

    [Fact]
    public void Правило_ЗеленоеЕслиИмяИсточникаТолькоВСтроковомЛитерале()
    {
        AssertWiring(Source + "Console.Error.WriteLine(\"AddJsonFile не подключён\");\n" + Call + Core + Subsystems);
    }

    [Fact]
    public void Правило_КраснеетЕслиИсточникПодсистемНеНайден()
    {
        var act = () => AssertWiring(Call + Core + Subsystems);
        act.Should().Throw<Exception>().WithMessage("*источник тумблеров подсистем*");
    }

    [Fact]
    public void Правило_ЗеленоеПриВызовеДоСборкиКонтейнера()
    {
        AssertWiring(Source + Call + Core + Subsystems);
    }

    private static void AssertWiring(string raw)
    {
        var text = StripCommentsAndLiterals(raw);

        AnyEnsureCall.Matches(text).Count.Should().BeGreaterThan(0,
            "в Program.cs вызова NonDisablableSubsystems.EnsureLlmEnabled нет (вне комментариев): " +
            "без него Subsystems:llm:Enabled=false роняет старт чужим текстом DI");

        var calls = TopLevelEnsureCall.Matches(text);
        calls.Count.Should().Be(1,
            "вызов NonDisablableSubsystems.EnsureLlmEnabled(builder.Configuration) есть, но он не верхним оператором " +
            "(или не ровно один): ожидается одна строка с колонки 0, без вложенности в if/блок");

        var subsystems = AddSubsystemsCall.Match(text);
        var core = AddCoreCall.Match(text);
        var source = SubsystemsSourceCall.Match(text);
        subsystems.Success.Should().BeTrue("сторож обязан видеть AddSubsystems, иначе порядок не проверить");
        core.Success.Should().BeTrue("сторож обязан видеть builder.Services.AddCore");
        source.Success.Should().BeTrue(
            "сторож обязан видеть источник тумблеров подсистем (AddJsonFile(subsystemsPath…)), иначе нижнюю границу не проверить");

        var at = calls[0].Index;
        at.Should().BeGreaterThan(source.Index,
            "отказ — после источника подсистем: иначе Subsystems:llm:Enabled=false из subsystems.json не прочитан");
        var between = text.Substring(source.Index + source.Length, at - source.Index - source.Length);
        OtherConfigSourceCall.IsMatch(between).Should().BeFalse(
            "отказ — после всех источников конфига: между источником подсистем и вызовом появился " +
            "другой AddJsonFile/AddInMemoryCollection, и тумблер llm мог прийти из него уже после проверки");
        var after = text.Substring(at + calls[0].Length);
        OtherConfigSourceCall.IsMatch(after).Should().BeFalse(
            "ни одного источника конфига после вызова: Subsystems:llm:Enabled=false из него прочитается позже " +
            "(AddSubsystems читает тумблеры), а явного отказа на старте не будет");
        at.Should().BeLessThan(subsystems.Index, "отказ — до AddSubsystems");
        at.Should().BeLessThan(core.Index, "отказ — до сборки контейнера (AddCore)");
    }

    // Комментарии вырезаются, литералы опустошаются: имя вызова в тексте строки — не вызов.
    private static string StripCommentsAndLiterals(string text) =>
        Tokens.Replace(text, m => m.Value[0] == '"' || m.Value[0] == '@' ? "\"\"" : " ");

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
