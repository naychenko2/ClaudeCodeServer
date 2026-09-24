using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Формула запуска строки в системном шелле — общая у сторожей чатов и пульта удалённых
// команд. Ключевой случай — вложенные кавычки: именно на них разваливался запуск на проде
// (ArgumentList экранировал внутренние " как \", cmd этих правил не знает, команда
// исполнялась как строковый литерал и давала ложный exit 0).
public class ShellCommandLineTests
{
    [Fact]
    public void WindowsCmdArguments_ВложенныеКавычкиДоезжаютКакЕсть()
    {
        ShellCommandLine.WindowsCmdArguments(@"tasklist /fi ""imagename eq Proxifier.exe"" /nh")
            .Should().Be(@"/s /c ""tasklist /fi ""imagename eq Proxifier.exe"" /nh""",
                "ключ /s заставляет cmd снять ТОЛЬКО внешние кавычки");
    }

    [Fact]
    public void ShellFileName_ПоПлатформеЦели()
    {
        ShellCommandLine.ShellFileName(windows: true).Should().Be("cmd.exe");
        ShellCommandLine.ShellFileName(windows: false).Should().Be("bash");
    }

    [Fact]
    public void UnixShellArgs_ЛогинШеллИКомандаОтдельнымАргументом()
    {
        // Массив, а не строка: аргументы уходят в execve без экранирования, кавычки
        // внутри команды разбирает сам bash. -l — ради PATH профильного окружения.
        ShellCommandLine.UnixShellArgs("docker compose up -d")
            .Should().Equal("-lc", "docker compose up -d");
    }
}
