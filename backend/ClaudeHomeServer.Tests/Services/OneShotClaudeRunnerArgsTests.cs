using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Состав аргументов one-shot запуска claude. Проверяем через BuildArgs, а не через реальный
// запуск: подменить процесс нечем (IProcessLauncher отдает живой Process). Флаги при этом
// критичны — CLI валидирует аргументы и падает с кодом 1 на незнакомом, роняя разом все
// фоновые задачи (так однажды лег весь one-shot из-за мертвого MultiEdit в deny-правилах).
public class OneShotClaudeRunnerArgsTests
{
    // Именованные аргументы обязательны: safeMode и persistSessions — соседние bool,
    // и позиционный вызов оставил бы тесты зелеными при перестановке параметров в проде
    private static List<string> Build(bool persistSessions = false, bool safeMode = true,
        string? model = null, string? effort = null) =>
        OneShotClaudeRunner.BuildArgs([], safeMode: safeMode,
            persistSessions: persistSessions, model: model, effort: effort);

    [Fact]
    public void ПоУмолчанию_ТранскриптНеПишется()
    {
        Build().Should().Contain("--no-session-persistence");
    }

    [Fact]
    public void Рубильник_ВозвращаетЗаписьТранскрипта()
    {
        Build(persistSessions: true).Should().NotContain("--no-session-persistence");
    }

    [Fact]
    public void ВПесочнице_ФлагСтоит_АSafeModeНет()
    {
        // safe-mode — только local (в образе песочницы CLI может быть старее 2.1.169),
        // а запись транскрипта отключаем в обеих средах
        var args = Build(safeMode: false);

        args.Should().Contain("--no-session-persistence");
        args.Should().NotContain("--safe-mode");
    }

    [Fact]
    public void PrintРежим_Обязателен()
    {
        // --no-session-persistence работает только вместе с --print
        Build().Should().Contain("--print");
    }

    [Fact]
    public void ФорматВывода_ВсегдаJson()
    {
        // Аналитике расхода нужен usage каждого вызова, а его отдает только json-ответ
        Build().Should().ContainInOrder("--output-format", "json");
        Build().Should().NotContain("text");
    }

    [Fact]
    public void МодельИУсилие_ПередаютсяКогдаЗаданы()
    {
        var args = Build(model: "deepseek-chat", effort: "high");

        args.Should().ContainInOrder("--model", "deepseek-chat");
        args.Should().ContainInOrder("--effort", "high");
    }

    [Fact]
    public void БезМоделиИУсилия_ФлаговНет()
    {
        var args = Build();

        args.Should().NotContain("--model");
        args.Should().NotContain("--effort");
    }

    [Theory]
    // Формулировки разных версий CLI и локалей — на любой из них нужна авто-деградация,
    // иначе на старом CLI в образе песочницы молча лягут ВСЕ фоновые задачи
    [InlineData("error: unknown option '--no-session-persistence'")]
    [InlineData("Unrecognized argument: --no-session-persistence")]
    [InlineData("Unexpected flag --no-session-persistence")]
    [InlineData("Аргумент --no-session-persistence не поддерживается")]
    public void ОтказИзЗаНашегоФлага_Распознается(string detail)
    {
        OneShotClaudeRunner.LooksLikeUnknownSessionFlag(detail).Should().BeTrue();
    }

    [Theory]
    // Чужие отказы деградацию запускать не должны: снятие флага их не вылечит,
    // а лишний повторный запуск удвоит и задержку, и расход токенов
    [InlineData("Failed to authenticate: OAuth session expired and could not be refreshed")]
    [InlineData("error: unknown option '--safe-mode'")]
    [InlineData("Error: Invalid tool name in disallowedTools: MultiEdit")]
    [InlineData("")]
    public void ЧужойОтказ_ДеградациюНеЗапускает(string detail)
    {
        OneShotClaudeRunner.LooksLikeUnknownSessionFlag(detail).Should().BeFalse();
    }

    [Fact]
    public void ИнструментыВыключены()
    {
        var args = Build();

        args.Should().Contain("--disallowedTools");
        args[args.IndexOf("--disallowedTools") + 1].Should().Contain("Bash").And.Contain("Write");
    }
}
