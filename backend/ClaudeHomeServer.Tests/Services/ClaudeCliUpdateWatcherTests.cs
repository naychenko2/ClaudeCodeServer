using System.Net;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож обновлений claude CLI: одно уведомление админу на каждую новую версию,
// переживает рестарт, молчит при любой неизвестности и не теряет версию, если флаг
// включили после первой проверки.
public class ClaudeCliUpdateWatcherTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "cli_update_tests_" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_tempDir, "claude-cli-update.json");

    private readonly NpmHandler _npm = new();
    private readonly StubUsers _users = new();
    private readonly StubFlags _flags = new();
    private readonly RecordingSender _sender = new();
    private string? _current = "2.1.283";

    public ClaudeCliUpdateWatcherTests()
    {
        Directory.CreateDirectory(_tempDir);
        _users.Add("admin1", "admin");
        _flags.On("admin1");
        _npm.Version = "2.1.300";
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private ClaudeCliUpdateWatcher Create() => new(
        new StubHttpClientFactory(_npm), _users, _flags, _sender,
        NullLogger<ClaudeCliUpdateWatcher>.Instance,
        () => Task.FromResult(_current), StatePath, TimeSpan.FromHours(24));

    [Fact]
    public async Task Outdated_NotifiesOnce_AndNotAgainOnSecondCheck()
    {
        var w = Create();
        await w.CheckOnceAsync();
        await w.CheckOnceAsync();

        _sender.Sent.Should().ContainSingle();
        var (userId, req, push) = _sender.Sent[0];
        userId.Should().Be("admin1");
        push.Should().BeTrue();
        req.Type.Should().Be("claude_cli_update");
        req.Url.Should().Be("/models");
        req.Body.Should().Contain("2.1.283").And.Contain("2.1.300");
        var status = w.GetStatus();
        (status.Current, status.Latest, status.UpdateAvailable).Should().Be(("2.1.283", "2.1.300", true));
    }

    [Fact]
    public async Task Restart_OnSameState_DoesNotRepeat_ButNewerVersionNotifiesAgain()
    {
        await Create().CheckOnceAsync();

        var restarted = Create();
        await restarted.CheckOnceAsync();
        _sender.Sent.Should().ContainSingle();

        _npm.Version = "2.1.301";
        await restarted.CheckOnceAsync();
        _sender.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task NoAdminHasFlag_VersionNotMarked_FlagLaterStillNotifies()
    {
        _flags.Off("admin1");
        var w = Create();
        await w.CheckOnceAsync();
        _sender.Sent.Should().BeEmpty();

        _flags.On("admin1");
        await w.CheckOnceAsync();
        _sender.Sent.Should().ContainSingle();
    }

    [Theory]
    [InlineData("2.1.283", "2.1.283")] // актуальна
    [InlineData("2.1.300", "2.1.283")] // хост новее npm
    [InlineData(null, "2.1.300")]      // CLI не ответил
    [InlineData("2.1.283", null)]      // npm не ответил
    public async Task NoUpdateOrUnknown_NoNotification(string? current, string? latest)
    {
        _current = current;
        _npm.Version = latest;
        if (latest is null) _npm.Status = HttpStatusCode.InternalServerError;

        await Create().CheckOnceAsync();

        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task GarbageVersionFromNpm_NoNotification()
    {
        _npm.RawBody = "{\"version\":\"не-версия\"}";
        var w = Create();
        await w.CheckOnceAsync();

        _sender.Sent.Should().BeEmpty();
        w.GetStatus().UpdateAvailable.Should().BeNull();
    }

    [Fact]
    public async Task NpmThrows_NoExceptionNoNotification()
    {
        _npm.Throw = true;
        var act = () => Create().CheckOnceAsync();
        await act.Should().NotThrowAsync();
        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyAdminsWithFlag_AndFailureOfOneDoesNotStopOthers()
    {
        _users.Add("user1", "user");
        _flags.On("user1");
        _users.Add("admin2", "admin");
        _flags.On("admin2");
        _sender.FailFor.Add("admin1");

        await Create().CheckOnceAsync();

        _sender.Sent.Select(s => s.UserId).Should().Equal("admin2");
    }

    [Fact]
    public async Task RefreshCurrent_UpdatesCurrentWithoutNpm()
    {
        var w = Create();
        await w.CheckOnceAsync();
        var npmCalls = _npm.Calls;

        _current = "2.1.300";
        var status = await w.RefreshCurrentAsync();

        status.Current.Should().Be("2.1.300");
        status.UpdateAvailable.Should().BeFalse();
        _npm.Calls.Should().Be(npmCalls);
        // Сохранилось в файл — новый экземпляр видит свежую версию
        Create().GetStatus().Current.Should().Be("2.1.300");
    }

    [Fact]
    public async Task CliVersionNull_KeepsKnownCurrent()
    {
        var w = Create();
        await w.CheckOnceAsync();

        _current = null;
        (await w.RefreshCurrentAsync()).Current.Should().Be("2.1.283");
    }

    // --- список изменений и новые модели ---

    private static string Section(string version, params string[] items) =>
        $"## {version}\n\n" + string.Concat(items.Select(i => $"- {i}\n")) + "\n";

    private const string Sonnet56 = "Added Claude Sonnet 5.6 (`claude-sonnet-5-6`), now the default Sonnet model";
    private const string Haiku5 = "Added Claude Haiku 5 (`claude-haiku-5`), now the default Haiku model";
    private const string Opus6 = "Added Claude Opus 6 (`claude-opus-6`), now the default Opus model";

    [Fact]
    public async Task OneModel_InTitle_ChangesInStatus()
    {
        _npm.Changelog = Section("2.1.300", Sonnet56, "Fixed a", "[VSCode] Fixed b") + Section("2.1.290", "Fixed c");
        var w = Create();
        await w.CheckOnceAsync();

        _sender.Sent.Should().ContainSingle().Which.Req.Title.Should().Be("Вышла Claude Sonnet 5.6 — обновите claude CLI");
        _sender.Sent[0].Req.Body.Should().Contain("Sonnet 5.6 (CLI 2.1.300)").And.Contain("2.1.283");
        var st = w.GetStatus();
        st.NewModels.Should().ContainSingle().Which.IsFamilyDefault.Should().BeTrue();
        st.Changes.Select(c => c.Version).Should().Equal("2.1.300", "2.1.290");
        st.HiddenCount.Should().Be(1);
    }

    [Fact]
    public async Task TwoModels_JoinedWithAnd()
    {
        _npm.Changelog = Section("2.1.300", Sonnet56, Haiku5);
        await Create().CheckOnceAsync();
        _sender.Sent.Single().Req.Title.Should().Be("Вышли Claude Sonnet 5.6 и Haiku 5 — обновите claude CLI");
    }

    [Fact]
    public async Task ThreeModels_CountInTitle_AllInBody()
    {
        _npm.Changelog = Section("2.1.300", Sonnet56, Haiku5, Opus6);
        await Create().CheckOnceAsync();
        var req = _sender.Sent.Single().Req;
        req.Title.Should().Be("Вышли новые модели Claude (3) — обновите claude CLI");
        req.Body.Should().Contain("Sonnet 5.6").And.Contain("Haiku 5").And.Contain("Opus 6");
    }

    [Fact]
    public async Task ChangelogDown_OldNotification_NoDigest_RetriedNextCheck()
    {
        _npm.ChangelogStatus = HttpStatusCode.InternalServerError;
        var w = Create();
        await w.CheckOnceAsync();

        _sender.Sent.Single().Req.Title.Should().Be("Доступна новая версия claude CLI");
        w.GetStatus().Changes.Should().BeEmpty();

        await w.CheckOnceAsync();
        _npm.ChangelogCalls.Should().Be(2);
    }

    [Fact]
    public async Task CompleteDigest_NotFetchedAgain()
    {
        _npm.Changelog = Section("2.1.300", "Fixed a");
        var w = Create();
        await w.CheckOnceAsync();
        await w.CheckOnceAsync();
        _npm.ChangelogCalls.Should().Be(1);
    }

    [Fact]
    public async Task MissingLatestSection_KeptPartial_RetriedAndNotWipedByNetworkError()
    {
        _npm.Changelog = Section("2.1.290", "Fixed c");   // секции 2.1.300 ещё нет
        var w = Create();
        await w.CheckOnceAsync();
        w.GetStatus().Changes.Select(c => c.Version).Should().Equal("2.1.290");

        _npm.ChangelogStatus = HttpStatusCode.BadGateway;
        await w.CheckOnceAsync();
        _npm.ChangelogCalls.Should().Be(2);
        w.GetStatus().Changes.Select(c => c.Version).Should().Equal("2.1.290");
    }

    [Fact]
    public async Task Refresh_NarrowsDigest_WithoutNetwork_AndNoRefetch()
    {
        _npm.Changelog = Section("2.1.300", Sonnet56) + Section("2.1.290", Haiku5);
        var w = Create();
        await w.CheckOnceAsync();

        _current = "2.1.295";
        var st = await w.RefreshCurrentAsync();
        st.Changes.Select(c => c.Version).Should().Equal("2.1.300");
        st.NewModels.Select(m => m.Name).Should().Equal("Sonnet 5.6");
        _npm.ChangelogCalls.Should().Be(1);

        await w.CheckOnceAsync();
        _npm.ChangelogCalls.Should().Be(1);
    }

    [Fact]
    public async Task UpdatedWhileChangelogLoading_NoNotification_NoRefetch()
    {
        _npm.Changelog = Section("2.1.300", Sonnet56);
        _npm.ChangelogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var w = Create();

        var check = w.CheckOnceAsync();
        await _npm.ChangelogStarted.Task;
        // Пока CHANGELOG качается, админ сделал claude update
        _current = "2.1.300";
        (await w.RefreshCurrentAsync()).UpdateAvailable.Should().BeFalse();
        _npm.ChangelogGate.SetResult();
        await check;

        _sender.Sent.Should().BeEmpty();
        w.GetStatus().Changes.Should().BeEmpty();
        await w.CheckOnceAsync();
        _npm.ChangelogCalls.Should().Be(1);
    }

    [Fact]
    public void OldStateFileWithoutDigest_Reads()
    {
        File.WriteAllText(StatePath, "{\"current\":\"2.1.283\",\"latest\":\"2.1.300\",\"lastNotifiedVersion\":null}");
        var st = Create().GetStatus();
        st.UpdateAvailable.Should().BeTrue();
        st.Changes.Should().BeEmpty();
        st.NewModels.Should().BeEmpty();
    }

    [Fact]
    public async Task DigestSurvivesRestart()
    {
        _npm.Changelog = Section("2.1.300", Sonnet56);
        await Create().CheckOnceAsync();
        Create().GetStatus().NewModels.Select(m => m.Id).Should().Equal("claude-sonnet-5-6");
    }

    // --- фейки ---

    // Отвечает по адресу: npm latest или CHANGELOG (по умолчанию 404 — списка нет)
    private sealed class NpmHandler : HttpMessageHandler
    {
        public string? Version;
        public string? RawBody;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Throw;
        public int Calls;
        public string? Changelog;
        public HttpStatusCode? ChangelogStatus;
        public int ChangelogCalls;
        public TaskCompletionSource? ChangelogGate;
        public readonly TaskCompletionSource ChangelogStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host.Contains("githubusercontent"))
            {
                ChangelogCalls++;
                ChangelogStarted.TrySetResult();
                if (ChangelogGate is not null) await ChangelogGate.Task;
                var code = ChangelogStatus ?? (Changelog is null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
                return new HttpResponseMessage(code) { Content = new StringContent(Changelog ?? "") };
            }
            Calls++;
            if (Throw) throw new HttpRequestException("сеть недоступна");
            var body = RawBody ?? $"{{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"{Version}\"}}";
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubUsers : IUserStore
    {
        private readonly List<User> _all = [];
        public void Add(string id, string role) => _all.Add(new User { Id = id, Role = role });
        public User? GetById(string id) => _all.FirstOrDefault(u => u.Id == id);
        public IReadOnlyList<User> GetAll() => _all;
        public bool IsTokenVersionCurrent(string userId, int version) => true;
    }

    private sealed class StubFlags : IFeatureFlagGate
    {
        private readonly HashSet<string> _on = [];
        public void On(string userId) => _on.Add(userId);
        public void Off(string userId) => _on.Remove(userId);
        public bool IsEnabled(string userId, string key) =>
            key == FeatureFlagKeys.ClaudeCliUpdateWatch && _on.Contains(userId);
    }

    private sealed class RecordingSender : INotificationSender
    {
        public readonly List<(string UserId, CreateNotificationRequest Req, bool Push)> Sent = [];
        public readonly HashSet<string> FailFor = [];

        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false)
        {
            if (FailFor.Contains(userId)) throw new InvalidOperationException("отказ доставки");
            Sent.Add((userId, req, sendPush));
            return Task.FromResult(Guid.NewGuid().ToString());
        }
    }
}

// Кэш версии CLI: пустой опрос не затирает известное, параллельные опросы — один процесс.
public class CliVersionCacheTests
{
    [Fact]
    public async Task NullProbe_KeepsKnownVersion()
    {
        var answers = new Queue<string?>(["2.1.283", null]);
        var cache = new CliVersionCache(() => Task.FromResult(answers.Dequeue()));

        (await cache.GetAsync()).Should().Be("2.1.283");
        (await cache.RefreshAsync()).Should().Be("2.1.283");
        cache.TryGetKnown(out var known).Should().BeTrue();
        known.Should().Be("2.1.283");
    }

    [Fact]
    public async Task Refresh_ReplacesKnownVersion_GetDoesNotProbeAgain()
    {
        var calls = 0;
        var answers = new Queue<string?>(["2.1.283", "2.1.300"]);
        var cache = new CliVersionCache(() => { calls++; return Task.FromResult(answers.Dequeue()); });

        await cache.GetAsync();
        (await cache.RefreshAsync()).Should().Be("2.1.300");
        (await cache.GetAsync()).Should().Be("2.1.300");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ParallelRefresh_SharesOneProbe()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new CliVersionCache(() => { Interlocked.Increment(ref calls); return gate.Task; });

        var a = cache.RefreshAsync();
        var b = cache.RefreshAsync();
        var c = cache.GetAsync();
        gate.SetResult("2.1.300");

        (await Task.WhenAll(a, b, c)).Should().AllBe("2.1.300");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ThrowingProbe_TreatedAsFailure()
    {
        var cache = new CliVersionCache(() => throw new InvalidOperationException("нет CLI"));
        (await cache.GetAsync()).Should().BeNull();
        cache.TryGetKnown(out _).Should().BeTrue();
    }
}
