using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Резолв команды по PATH+PATHEXT: без него на Windows не запускался ни один npm-сервис
// (npm — это npm.cmd, а Process.Start расширения не подставляет).
//
// Проверяем FindInPath, а не ResolveExecutable: второй завязан на Windows и системное
// окружение, а правило поиска одинаково и проверяемо на любой ОС — CI гоняет linux.
public class LocalProcessRunnerPathTests : IDisposable
{
    private readonly string _dirA;
    private readonly string _dirB;

    public LocalProcessRunnerPathTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "lpr_" + Guid.NewGuid().ToString("N")[..8]);
        _dirA = Path.Combine(root, "a");
        _dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dirA)!, recursive: true); } catch { /* best-effort */ }
    }

    private string Touch(string dir, string name)
    {
        var full = Path.Combine(dir, name);
        File.WriteAllText(full, "");
        return full;
    }

    private string Path2 => _dirA + ";" + _dirB;

    // Расширения в тестах в нижнем регистре — как имена создаваемых файлов. На Windows
    // регистр не важен (ФС нечувствительна), на linux-раннере CI — важен, и «.CMD» из
    // настоящего PATHEXT там просто не нашёл бы «npm.cmd». Разбор расширений регистра
    // не меняет, так что правило проверяется то же самое.

    [Fact]
    public void FindInPath_FindsCommandByExtension()
    {
        var expected = Touch(_dirA, "npm.cmd");

        LocalProcessRunner.FindInPath("npm", Path2, ".com;.exe;.bat;.cmd").Should().Be(expected);
    }

    [Fact]
    public void FindInPath_PrefersEarlierDirectory()
    {
        var first = Touch(_dirA, "tool.cmd");
        Touch(_dirB, "tool.cmd");

        LocalProcessRunner.FindInPath("tool", Path2, ".cmd").Should().Be(first);
    }

    [Fact]
    public void FindInPath_TriesAllExtensionsInDirectoryBeforeNext()
    {
        // Как в cmd: каталог перебирается по всем расширениям целиком, и лишь потом
        // берётся следующий — иначе .exe из дальнего каталога обошёл бы .cmd из ближнего
        var nearCmd = Touch(_dirA, "tool.cmd");
        Touch(_dirB, "tool.exe");

        LocalProcessRunner.FindInPath("tool", Path2, ".exe;.cmd").Should().Be(nearCmd);
    }

    [Fact]
    public void FindInPath_UnknownCommand_ReturnsNull()
    {
        LocalProcessRunner.FindInPath("нет-такой-команды", Path2, ".cmd").Should().BeNull();
    }

    [Fact]
    public void FindInPath_IgnoresQuotedAndBrokenEntries()
    {
        var expected = Touch(_dirB, "tool.cmd");
        // Кавычки в записях PATH — обычное дело, мусорные символы тоже встречаются
        var path = "\"" + _dirA + "\";<битая|запись>;" + _dirB;

        LocalProcessRunner.FindInPath("tool", path, ".cmd").Should().Be(expected);
    }

    [Fact]
    public void FindInPath_EmptyPath_ReturnsNull()
    {
        LocalProcessRunner.FindInPath("npm", null, null).Should().BeNull();
    }

    [Fact]
    public void ResolveExecutable_PathLikeName_ReturnedAsIs()
    {
        // Путь и имя с расширением ОС разбирает сама — в PATH лезть незачем
        LocalProcessRunner.ResolveExecutable("./scripts/run.sh").Should().Be("./scripts/run.sh");
        LocalProcessRunner.ResolveExecutable("node.exe").Should().Be("node.exe");
    }

    [Fact]
    public void ResolveExecutable_UnknownCommand_ReturnedAsIs()
    {
        // Не нашли — отдаём как было, чтобы ошибка запуска осталась прежней
        LocalProcessRunner.ResolveExecutable("нет-такой-команды-xyz").Should().Be("нет-такой-команды-xyz");
    }
}
