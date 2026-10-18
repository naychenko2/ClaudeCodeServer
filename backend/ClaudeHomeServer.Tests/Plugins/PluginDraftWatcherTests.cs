using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Plugins;

// Наблюдатель черновиков build-it-yourself (ADR-031, шаг 4.2): один inotify-экземпляр на
// процесс, следит за data/plugins/{owner}/_drafts, шлёт владельцу событие plugin_draft_changed
// (slug + монотонная ревизия, без содержимого/хостовых путей). Критерий:
//   - правка файла → событие владельцу;
//   - запись десяти файлов подряд → не больше двух событий (дебаунс);
//   - чужой владелец события не получает (изоляция);
//   - нет каталога _drafts → не падает, подхватывает создание;
//   - повторное гашение идемпотентно;
//   - без флага событий нет.
// Ожидание событий — через TaskCompletionSource (не Task.Delay): CI на Linux.
[Collection(TestCollections.Inotify)]
public class PluginDraftWatcherTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string Stranger = "owner-b";
    private const string Slug = "my-plugin";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly DraftBroadcaster _broadcaster;
    private readonly FakeFlagGate _flags;

    public PluginDraftWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_draftwatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _broadcaster = new DraftBroadcaster();
        _flags = new FakeFlagGate(enabled: true);
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

    private string DraftDir(string owner, string slug) => _store.DraftDir(owner, slug);

    // Создать файл в черновике (каталог создаётся, если нет). Возвращает путь к файлу.
    private string WriteDraftFile(string owner, string slug, string fileName)
    {
        var dir = DraftDir(owner, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "content");
        return path;
    }

    private PluginDraftWatcher NewWatcher() =>
        new(_store, _broadcaster, _flags, NullLogger<PluginDraftWatcher>.Instance);

    // ── 1. Правка файла даёт событие владельцу ─────────────────────────────────

    [Fact]
    public async Task ПравкаФайла_ДаётСобытиеВладельцу()
    {
        var file = WriteDraftFile(Owner, Slug, "a.md");
        var watcher = NewWatcher();
        watcher.StartWatch();
        try
        {
            File.WriteAllText(file, "changed");
            (await _broadcaster.WaitEventAsync(Owner, 5000)).Should().BeTrue(
                "правка файла в черновике даёт событие владельцу");
            var ev = _broadcaster.SingleEvent(Owner);
            ev.Slug.Should().Be(Slug);
            ev.Revision.Should().BeGreaterThan(0, "монотонная ревизия");
        }
        finally { watcher.StopWatch(); }
    }

    // ── 2. Дебаунс: запись десяти файлов подряд → не больше двух событий ─────────

    [Fact]
    public async Task Дебаунс_ДесятьФайлов_НеБольшеДвухСобытий()
    {
        WriteDraftFile(Owner, Slug, "seed.md");
        var watcher = NewWatcher();
        watcher.DebounceMs = 100;   // узкое окно: 10 быстрых записей склеятся в одно-два события
        watcher.StartWatch();
        try
        {
            for (var i = 0; i < 10; i++)
                WriteDraftFile(Owner, Slug, $"f{i}.md");

            // Ждём первое событие (TCS), затем короткую «тишину», чтобы дошли все склеенные.
            (await _broadcaster.WaitEventAsync(Owner, 5000)).Should().BeTrue();
            await Task.Delay(400);   // quiescence: окно дебаунса (100 мс) давно истекло
            _broadcaster.CountFor(Owner).Should().BeLessOrEqualTo(2,
                "запись десяти файлов подряд даёт не больше двух событий, а не десять");
        }
        finally { watcher.StopWatch(); }
    }

    // ── 3. Изоляция: чужой владелец события не получает ─────────────────────────

    [Fact]
    public async Task Изоляция_ЧужойВладелец_СобытияНеПолучает()
    {
        WriteDraftFile(Owner, Slug, "a.md");
        var watcher = NewWatcher();
        watcher.StartWatch();
        try
        {
            File.WriteAllText(Path.Combine(DraftDir(Owner, Slug), "a.md"), "changed");
            (await _broadcaster.WaitEventAsync(Owner, 5000)).Should().BeTrue("свой владелец получил");
            await Task.Delay(400);   // quiescence
            _broadcaster.CountFor(Stranger).Should().Be(0,
                "чужой владелец событие не получает никогда");
        }
        finally { watcher.StopWatch(); }
    }

    // ── 4. Нет каталога _drafts → не падает, подхватывает создание ──────────────

    [Fact]
    public async Task НетКаталогаDrafts_НеПадает_ПодхватываетСоздание()
    {
        // Корень владельца data/plugins/{owner} существует, но _drafts ещё нет.
        Directory.CreateDirectory(_store.OwnerRoot(Owner));

        var watcher = NewWatcher();
        watcher.StartWatch();   // не падает, хотя _drafts нет
        try
        {
            // Создаём _drafts/{slug} и файл: примитив сам подписывается на новый каталог,
            // создание файла в нём даёт событие.
            WriteDraftFile(Owner, Slug, "a.md");
            (await _broadcaster.WaitEventAsync(Owner, 5000)).Should().BeTrue(
                "создание _drafts подхвачено, файл в нём дал событие");
        }
        finally { watcher.StopWatch(); }
    }

    // ── 5. Повторное гашение идемпотентно ───────────────────────────────────────

    [Fact]
    public void ПовторноеГашение_Идемпотентно()
    {
        var watcher = NewWatcher();
        watcher.StartWatch();
        watcher.StopWatch();
        // Второе StopWatch не бросает и не вешается (Join уже вышедшего потока мгновенен).
        Action act = () => watcher.StopWatch();
        act.Should().NotThrow();
    }

    // ── 6. Без флага событий нет ───────────────────────────────────────────────

    [Fact]
    public async Task БезФлага_СобытийНет()
    {
        _flags.Enabled = false;
        WriteDraftFile(Owner, Slug, "a.md");
        var watcher = NewWatcher();
        watcher.StartWatch();
        try
        {
            File.WriteAllText(Path.Combine(DraftDir(Owner, Slug), "a.md"), "changed");
            await Task.Delay(400);   // quiescence: дебаунс истёк, события быть не должно
            _broadcaster.CountFor(Owner).Should().Be(0,
                "флаг plugins-build-yourself выключен — событие не уходит");
        }
        finally { watcher.StopWatch(); }
    }

    // ── Фейки ──────────────────────────────────────────────────────────────────

    // Фейк IFeatureFlagGate: один общий переключатель (тестам хватает «включён/выключен»).
    private sealed class FakeFlagGate : IFeatureFlagGate
    {
        public bool Enabled { get; set; }
        public FakeFlagGate(bool enabled) => Enabled = enabled;
        public bool IsEnabled(string userId, string key) =>
            Enabled && key == FeatureFlagKeys.PluginsBuildYourself;
    }

    // Фейк ISessionBroadcaster: собирает plugin_draft_changed по владельцу и будит TCS
    // на первое событие (ожидание — через TaskCompletionSource, не Task.Delay).
    private sealed class DraftBroadcaster : ISessionBroadcaster
    {
        private readonly object _lock = new();
        private readonly List<(string Owner, PluginDraftChangedMessage Msg)> _events = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new();

        public int CountFor(string owner)
        {
            lock (_lock) return _events.Count(e => e.Owner == owner);
        }

        public PluginDraftChangedMessage SingleEvent(string owner)
        {
            lock (_lock)
            {
                var e = _events.Where(x => x.Owner == owner).ToList();
                e.Should().HaveCount(1, "в этом тесте ожидается ровно одно событие");
                return e[0].Msg;
            }
        }

        // true — событие для owner уже есть или пришло за timeout; false — таймаут.
        public async Task<bool> WaitEventAsync(string owner, int timeoutMs)
        {
            TaskCompletionSource<bool> tcs;
            lock (_lock)
            {
                if (_events.Any(e => e.Owner == owner)) return true;
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[owner] = tcs;
            }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return ReferenceEquals(done, tcs.Task) && tcs.Task.Result;
        }

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            if (message is not PluginDraftChangedMessage m) return Task.CompletedTask;
            TaskCompletionSource<bool>? wake = null;
            lock (_lock)
            {
                _events.Add((ownerId, m));
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
