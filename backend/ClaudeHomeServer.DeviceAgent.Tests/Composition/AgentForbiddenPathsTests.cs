using ClaudeHomeServer.DeviceAgent.Composition;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Запретный список автовыдачи папок (решение владельца 2026-09-27): агент отказывает сам,
/// сервер список не видит. Строковые проверки — на форме пути и Linux, и Windows.
/// </summary>
public sealed class AgentForbiddenPathsTests
{
    private static readonly AgentForbiddenContext Unix = new(
        Homes: ["/home/u"],
        AgentDirectories: ["/home/u/.local/share/ai-home-agent"],
        MountPoints: ["/", "/mnt/data"]);

    private static readonly AgentForbiddenContext Windows = new(
        Homes: [@"C:\Users\u"],
        AgentDirectories: [@"C:\Users\u\AppData\Local\AiHomeAgent"],
        MountPoints: [@"C:\", @"D:\"]);

    [Theory]
    [InlineData("/")]
    [InlineData("/mnt/data")]
    [InlineData("/home")]
    [InlineData("/home/u")]
    [InlineData("/home/u/")]
    [InlineData("/home/u/.ssh")]
    [InlineData("/home/u/.ssh/keys")]
    [InlineData("/home/u/work/.gnupg/x")]
    [InlineData("/home/u/.aws")]
    [InlineData("/home/u/.kube/cache")]
    [InlineData("/home/u/.claude/projects")]
    [InlineData("/home/u/.config/app")]
    [InlineData("/home/u/.local")]
    [InlineData("/home/u/.local/share/ai-home-agent/turns")]
    [InlineData("/etc/nginx")]
    [InlineData("/usr/local/src/app")]
    [InlineData("/var/www")]
    [InlineData("/opt")]
    [InlineData("/proc/1")]
    // «..»-обход: лексически путь начинается в разрешённом месте, а ведёт в запретное
    [InlineData("/home/u/projects/../.ssh/id")]
    [InlineData("/home/u/projects/app/../../../u")]
    [InlineData("/srv/../etc/passwd-dir")]
    // Регистр: без учёта на любой ОС
    [InlineData("/home/u/.SSH")]
    [InlineData("/HOME/U")]
    [InlineData("/ETC/x")]
    public void Unix_ЗапретныеПути_Отказ(string path) =>
        AgentForbiddenPaths.RefusalOf(path, Unix).Should().NotBeNull($"«{path}» в запретном списке");

    [Theory]
    [InlineData("/home/u/projects/app")]
    [InlineData("/home/u/projects/../work/app")]
    [InlineData("/mnt/data/projects")]
    [InlineData("/srv/app")]
    [InlineData("/home/u/.sshkeys-backup-not-really")]
    public void Unix_ПодпапкиПрофиляИОбычныеКаталоги_Разрешены(string path) =>
        AgentForbiddenPaths.RefusalOf(path, Unix).Should().BeNull();

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\u")]
    [InlineData(@"c:\users\U")]
    [InlineData(@"C:\Users\u\AppData\Roaming\x")]
    [InlineData(@"C:\Users\u\appdata")]
    [InlineData(@"C:\Users\u\.ssh\keys")]
    [InlineData(@"C:\Users\u\.ssh.\keys")]
    [InlineData(@"C:\WINDOWS\System32")]
    [InlineData(@"C:\Program Files\App")]
    [InlineData(@"C:\program files (x86)\App")]
    [InlineData(@"C:\ProgramData\x")]
    [InlineData(@"D:\Windows")]
    [InlineData(@"C:\Users\u\Projects\..\..\u")]
    public void Windows_ЗапретныеПути_Отказ(string path) =>
        AgentForbiddenPaths.RefusalOf(path, Windows).Should().NotBeNull($"«{path}» в запретном списке");

    [Theory]
    [InlineData(@"C:\Users\u\Projects\app")]
    [InlineData(@"D:\work\app")]
    [InlineData(@"C:\src\app")]
    public void Windows_ОбычныеКаталоги_Разрешены(string path) =>
        AgentForbiddenPaths.RefusalOf(path, Windows).Should().BeNull();

    [Fact]
    public void ОтносительныйПуть_Отказ()
    {
        AgentForbiddenPaths.RefusalOf("projects/app", Unix).Should().NotBeNull();
        AgentForbiddenPaths.Check("projects/app", Unix).Should().NotBeNull();
    }

    [SkippableFact]
    public void СсылкаНаЗапретныйКаталог_Отказ_ПоРеальномуПути()
    {
        using var box = new LinkBox();
        var home = Path.Combine(box.Base, "home");
        var ssh = Path.Combine(home, ".ssh");
        Directory.CreateDirectory(ssh);
        var link = Path.Combine(box.Base, "work", "innocent");
        box.Link(link, ssh);
        var context = new AgentForbiddenContext([home], [], []);

        AgentForbiddenPaths.RefusalOf(link, context).Should().BeNull("лексически путь безобиден");
        AgentForbiddenPaths.Check(link, context).Should().NotBeNull("по реальному пути это .ssh");
        AgentForbiddenPaths.Check(Path.Combine(link, "sub"), context).Should().NotBeNull();
    }

    [SkippableFact]
    public void СсылкаНаПрофиль_Отказ()
    {
        using var box = new LinkBox();
        var home = Path.Combine(box.Base, "home");
        Directory.CreateDirectory(home);
        var link = Path.Combine(box.Base, "work", "me");
        box.Link(link, home);

        AgentForbiddenPaths.Check(link, new AgentForbiddenContext([home], [], [])).Should().NotBeNull();
    }

    /// <summary>
    /// Каталог для ссылок — рядом с тестами, а не во временной папке: на Windows она лежит в
    /// AppData и запрещена сама по себе, проверка ссылки стала бы вакуумной.
    /// </summary>
    internal sealed class LinkBox : IDisposable
    {
        public string Base { get; } = Path.Combine(AppContext.BaseDirectory, "forbidden-" + Guid.NewGuid().ToString("N"));

        public LinkBox() => Directory.CreateDirectory(Path.Combine(Base, "work"));

        public void Link(string linkPath, string target)
        {
            try { Directory.CreateSymbolicLink(linkPath, target); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                Skip.If(true, "Нет прав создавать символические ссылки: " + e.Message);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Base, recursive: true); } catch { /* временная папка */ }
        }
    }
}
