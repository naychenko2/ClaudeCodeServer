using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Hosting;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hosting;

/// <summary>
/// «roots add» не принимает каталог, куда пишут другие пользователи машины (ревью 4.5,
/// MAJOR #2): там подложенная жёсткая ссылка неотличима от файла проекта.
/// </summary>
public sealed class AgentRootsStoreTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "agent-roots-" + Guid.NewGuid().ToString("N"));

    public AgentRootsStoreTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* временная папка */ }
    }

    private AgentRootsStore Store => new(Path.Combine(_base, "roots.json"));

    [UnsupportedOSPlatform("windows")]
    private string Dir(string name, UnixFileMode mode)
    {
        var dir = Path.Combine(_base, name);
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, mode);
        return dir;
    }

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void ЗаписьГруппеИлиВсем_БезForce_Отказ_СForce_Добавляется()
    {
        Skip.If(OperatingSystem.IsWindows(), "На Windows права — ACL, проверка только предупреждением");
        var world = Dir("world", Private | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.OtherRead);
        var group = Dir("group", Private | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.GroupRead);
        var store = Store;

        store.Invoking(s => s.Add(world)).Should().Throw<SharedRootException>().WithMessage("*--force*");
        store.Invoking(s => s.Add(group)).Should().Throw<SharedRootException>();
        store.Roots.Should().BeEmpty();

        store.Add(world, force: true);
        store.Roots.Should().Equal(world);
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void ТолькоВладелец_ДобавляетсяБезForce()
    {
        Skip.If(OperatingSystem.IsWindows(), "На Windows права — ACL, проверка только предупреждением");
        var own = Dir("own", Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var store = Store;

        store.Add(own);

        store.Roots.Should().Equal(own);
    }
}
