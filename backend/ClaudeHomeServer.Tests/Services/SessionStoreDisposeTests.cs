using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// SessionStore держит два фоновых таймера: автосейв (_autoSaveTimer) и one-shot дебаунса
// MarkRead (_pendingSaveTimer). Стор — синглтон DI: без IDisposable контейнер оставил бы их
// жить в процессе (фасад гасил таймеры только в своём cleanup-пути). Тесты: Dispose гасит оба
// таймера, висящую запись дебаунса сбрасывает синхронно, а повторный вызов — и после гашения
// фасада (StopAutoSave/FlushPendingSave) — безопасный no-op.
public class SessionStoreDisposeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionStore _store;

    public SessionStoreDisposeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_dispose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Автосейв включён (1с): дебаунс живёт под тем же гейтом AutoSaveSeconds > 0.
                ["Session:AutoSaveSeconds"] = "1",
                ["Session:MarkReadDebounceSeconds"] = "0.2",
            })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        _store = new SessionStore(config, projects, new TestSessionBroadcaster(), NullLogger<SessionStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestFs.DeleteDirectoryResilient(_tempDir);
    }

    // Ожидание условия с дедлайном (таймеры гоняют колбэки на thread pool) — тот же приём,
    // что в SessionManagerTestBase.
    private static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"условие не выполнилось за {timeout?.TotalSeconds ?? 10}с: {what}");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Dispose_ГаситАвтосейв_ТикиНеПишутПосле()
    {
        _store.StartAutoSave();
        WaitUntil(() => _store.SaveSessionsCalls >= 1, "первый тик автосейва (интервал 1с)");
        var before = _store.SaveSessionsCalls;

        _store.Dispose();

        // Окно заведомо длиннее интервала (1с): живой таймер дал бы ещё тики.
        Thread.Sleep(2500);
        _store.SaveSessionsCalls.Should().Be(before, "после Dispose автосейв не должен тикать");
    }

    [Fact]
    public void Dispose_ГаситДебаунс_ВисящаяЗапискаСбрасываетсяСинхронно()
    {
        var before = _store.SaveSessionsCalls;
        _store.ScheduleDebouncedSave(); // one-shot таймер на 0,2с

        _store.Dispose();

        _store.SaveSessionsCalls.Should().Be(before + 1,
            "висящая запись сбрасывается синхронно (FlushPendingSave), а не через таймер");
        Thread.Sleep(1500); // дольше задержки дебаунса (0,2с)
        _store.SaveSessionsCalls.Should().Be(before + 1, "гашенный one-shot таймер не должен сработать");
    }

    [Fact]
    public void ГашениеФасадаЗатемDispose_НеДублируетИБезопасен()
    {
        // Боевой порядок остановки: фасад (StopAutoSave + FlushPendingSave из KillAllProcesses
        // /Dispose), затем контейнер диспозит синглтон стора.
        _store.StartAutoSave();
        _store.ScheduleDebouncedSave();
        var before = _store.SaveSessionsCalls;

        _store.StopAutoSave();
        _store.FlushPendingSave();
        _store.Dispose();
        _store.Dispose();

        _store.SaveSessionsCalls.Should().Be(before + 1,
            "один сброс висящей записи: повторные гашения — no-op");
        Thread.Sleep(1500); // дольше дебаунса (0,2с) и интервала автосейва (1с)
        _store.SaveSessionsCalls.Should().Be(before + 1, "оба таймера гасны, тиков нет");
    }
}
