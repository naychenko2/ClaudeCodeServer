using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// S6: событие «установка плагина изменилась» (plugins_changed). Точка испускания —
// хранилище (PluginStore/PluginSecretsStore), а не контроллеры: каждая запись установки
// (install/grant/enable/disable/uninstall) и запись/удаление секрета испускают владельцу
// одно событие, а чужому — ничего. Критерий:
//   - install (Update), grant (Update<T> с изменением), disable (Update), uninstall
//     (SaveInstalled), запись секрета (Set) → ровно одно событие владельцу, ноль чужому;
//   - Update<T> без изменений (no-op) → события нет;
//   - хранилище без брокастера (null) → запись без исключения.
// Ожидание — через TaskCompletionSource (не Task.Delay): CI на Linux.
public class PluginStoreChangedEventTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string Stranger = "owner-b";

    private readonly string _dir;
    private readonly PluginsBroadcaster _broadcaster;

    public PluginStoreChangedEventTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_pluginschanged_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _broadcaster = new PluginsBroadcaster();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    // Свежий каталог на тест: installed.json/plugin-secrets.json не наследуются между тестами,
    // поэтому «ровно одно событие» считается от нуля.
    private string FreshDir()
    {
        var d = Path.Combine(_dir, "t" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private PluginStore NewStore() => new(BuildConfig(FreshDir()), _broadcaster);
    private PluginSecretsStore NewSecretsStore() => new(BuildConfig(FreshDir()), _broadcaster);

    // ── 1. install (Update) → ровно одно событие владельцу, ноль чужому ─────────────

    [Fact]
    public async Task Install_ОдноСобытиеВладельцу_НольЧужому()
    {
        var store = NewStore();

        // install: PluginInstaller пишет через Update (добавляет запись установки).
        store.Update(Owner, records =>
        {
            records.Add(new PluginInstall
            {
                Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft",
            });
            return records;
        });

        (await _broadcaster.WaitEventAsync(Owner, 2000)).Should().BeTrue("install даёт событие владельцу");
        _broadcaster.CountFor(Owner).Should().Be(1, "ровно одно событие");
        _broadcaster.CountFor(Stranger).Should().Be(0, "чужой владелец ничего не получает");
    }

    // ── 2. grant (Update<T> с изменением) → ровно одно событие, ноль чужому ──────────

    [Fact]
    public async Task Grant_ИзменениеВыданных_ОдноСобытие()
    {
        var store = NewStore();
        store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a",
                Requested = ["notes:read"], Enabled = true, Source = "draft",
            },
        ]);
        _broadcaster.Reset();

        // grant: PluginsController.Grant пишет через Update<T> (меняет Granted). T = bool
        // (GrantOutcome — internal, из тестовой сборки не виден); тип результата не влияет
        // на логику испускания, важен сам Update<T> с изменением.
        var ok = store.Update<bool>(Owner, records =>
        {
            var r = records.Single(x => x.Id == "local-a/pkg");
            r.Granted = ["notes:read"];
            return true;
        });

        ok.Should().BeTrue();
        (await _broadcaster.WaitEventAsync(Owner, 2000)).Should().BeTrue("grant даёт событие владельцу");
        _broadcaster.CountFor(Owner).Should().Be(1, "ровно одно событие");
        _broadcaster.CountFor(Stranger).Should().Be(0, "чужой владелец ничего не получает");
    }

    // ── 3. disable (Update) → ровно одно событие, ноль чужому ───────────────────────

    [Fact]
    public async Task Disable_ОдноСобытие()
    {
        var store = NewStore();
        store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft",
            },
        ]);
        _broadcaster.Reset();

        // disable: PluginsController.SetEnabled пишет через Update (меняет Enabled).
        store.Update(Owner, records =>
        {
            records.Single(x => x.Id == "local-a/pkg").Enabled = false;
            return records;
        });

        (await _broadcaster.WaitEventAsync(Owner, 2000)).Should().BeTrue("disable даёт событие владельцу");
        _broadcaster.CountFor(Owner).Should().Be(1, "ровно одно событие");
        _broadcaster.CountFor(Stranger).Should().Be(0, "чужой владелец ничего не получает");
    }

    // ── 4. uninstall (SaveInstalled) → ровно одно событие, ноль чужому ──────────────

    [Fact]
    public async Task Uninstall_ОдноСобытие()
    {
        var store = NewStore();
        // Запись без секретов: каскад uninstall (SaveInstalled + RemovePlugin) даёт ровно
        // одно событие (RemovePlugin на плагине без секретов не пишет → не дублирует).
        store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft",
            },
        ]);
        _broadcaster.Reset();

        // uninstall: PluginUninstall пишет через SaveInstalled (удаляет запись).
        store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = "local-a/other", Version = "2.0.0", Sha256 = "b", Enabled = true, Source = "upload",
            },
        ]);

        (await _broadcaster.WaitEventAsync(Owner, 2000)).Should().BeTrue("uninstall даёт событие владельцу");
        _broadcaster.CountFor(Owner).Should().Be(1, "ровно одно событие");
        _broadcaster.CountFor(Stranger).Should().Be(0, "чужой владелец ничего не получает");
    }

    // ── 5. запись секрета (Set) → ровно одно событие, ноль чужому ───────────────────

    [Fact]
    public async Task ЗаписьСекрета_ОдноСобытие()
    {
        var secrets = NewSecretsStore();

        // запись секрета: PluginSecretsStore.Set (PUT /secrets/{name} / grant).
        secrets.Set(Owner, "local-a/pkg", "LINEAR_TOKEN", "tok-123");

        (await _broadcaster.WaitEventAsync(Owner, 2000)).Should().BeTrue("запись секрета даёт событие владельцу");
        _broadcaster.CountFor(Owner).Should().Be(1, "ровно одно событие");
        _broadcaster.CountFor(Stranger).Should().Be(0, "чужой владелец ничего не получает");
    }

    // ── 6. Update<T> без изменений (no-op) → события нет ────────────────────────────

    [Fact]
    public async Task UpdateT_NoOp_СобытияНет()
    {
        var store = NewStore();
        store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft",
            },
        ]);
        _broadcaster.Reset();

        // no-op: записи «ghost» нет → список не мутируется → файл не переписывается → события нет.
        var found = store.Update<bool>(Owner, records => records.Any(r => r.Id == "ghost"));

        found.Should().BeFalse("записи с id ghost нет");
        // Короткая тишина: если событие всё же ушло, WaitEventAsync сразу вернит true.
        (await _broadcaster.WaitEventAsync(Owner, 150)).Should().BeFalse(
            "no-op Update<T> не испускает событие");
        _broadcaster.CountFor(Owner).Should().Be(0, "no-op Update<T> → ноль событий");
    }

    // ── 7. хранилище без брокастера (null) → запись без исключения ──────────────────

    [Fact]
    public void БезБрокастера_ЗаписьБезИсключения()
    {
        var store = new PluginStore(BuildConfig(FreshDir()));   // broadcaster = null
        var secrets = new PluginSecretsStore(BuildConfig(FreshDir()));

        Action act = () =>
        {
            store.SaveInstalled(Owner,
            [
                new PluginInstall
                {
                    Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft",
                },
            ]);
            store.Update(Owner, records =>
            {
                records.Single(x => x.Id == "local-a/pkg").Enabled = false;
                return records;
            });
            store.Update<bool>(Owner, _ => true);
            secrets.Set(Owner, "local-a/pkg", "LINEAR_TOKEN", "tok-123");
        };
        act.Should().NotThrow("без брокастера (null) запись не падает");
    }

    // ── Фейк ISessionBroadcaster: считает plugins_changed по владельцу и будит TCS ───

    // Фейк ISessionBroadcaster: собирает plugins_changed по владельцу и будит TCS на первое
    // событие (ожидание — через TaskCompletionSource, не Task.Delay). PluginsChangedMessage
    // без полезной нагрузки, поэтому держим только счётчик по владельцу.
    private sealed class PluginsBroadcaster : ISessionBroadcaster
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, int> _counts = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new();

        public int CountFor(string owner)
        {
            lock (_lock) return _counts.TryGetValue(owner, out var c) ? c : 0;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _counts.Clear();
                _waiters.Clear();
            }
        }

        // true — событие для owner уже есть или пришло за timeout; false — тишина за timeout.
        public async Task<bool> WaitEventAsync(string owner, int timeoutMs)
        {
            TaskCompletionSource<bool> tcs;
            lock (_lock)
            {
                if (_counts.TryGetValue(owner, out var c) && c > 0) return true;
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[owner] = tcs;
            }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return ReferenceEquals(done, tcs.Task) && tcs.Task.Result;
        }

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            if (message is not PluginsChangedMessage) return Task.CompletedTask;
            TaskCompletionSource<bool>? wake = null;
            lock (_lock)
            {
                _counts[ownerId] = _counts.TryGetValue(ownerId, out var c) ? c + 1 : 1;
                if (_waiters.TryGetValue(ownerId, out var t)) wake = t;
            }
            wake?.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
