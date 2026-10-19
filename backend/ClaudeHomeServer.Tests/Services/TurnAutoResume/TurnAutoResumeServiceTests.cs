using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.TurnAutoResume;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.TurnAutoResume;

/// <summary>
/// Тесты фонового оркестратора авто-возобновления. Время и хост подменены: пауз реальных нет,
/// Task.Delay не используется — события и порядок записываются в общий журнал.
/// </summary>
public class TurnAutoResumeServiceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void Start() => _started.Cancel();
    }

    private sealed class FakeHost : ITurnAutoResumeHost
    {
        public readonly List<string> Log = [];
        public readonly TaskCompletionSource MarkersTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TakeCalls;
        public List<string> Ids = [];
        public Func<string, bool> FlagOn = _ => true;
        public Func<string, Task<TurnResumeSendResult>>? OnResume;

        public Task AbortMarkersWritten => MarkersTcs.Task;
        public IReadOnlyList<string> TakeCandidates() { TakeCalls++; return Ids; }
        public Session? GetSession(string id) => new Session { Id = id, OwnerId = "owner-" + id, ProjectId = null };
        public Project? GetProject(string projectId) => null;
        public bool IsEnabledForOwner(string ownerId) => FlagOn(ownerId);
        public bool IsDeviceOnline(Project project) => true;
        public bool LastTurnWasDeployOrRollback(Session session, Project? project) => false;
        public Task<TurnResumeSendResult> ResumeAsync(string sessionId)
        {
            lock (Log) Log.Add("send:" + sessionId);
            return OnResume?.Invoke(sessionId) ?? Task.FromResult(new TurnResumeSendResult(true));
        }
    }

    private static (TurnAutoResumeService Svc, FakeHost Host, FakeLifetime Life, List<string> Events) Make(
        int ids = 1, int max = 10, int warmup = 15, int gap = 5, bool markersDone = true)
    {
        var host = new FakeHost { Ids = Enumerable.Range(1, ids).Select(i => "s" + i).ToList() };
        if (markersDone) host.MarkersTcs.SetResult();
        var life = new FakeLifetime();
        var events = host.Log;
        var opts = Options.Create(new TurnAutoResumeOptions { MaxPerBoot = max, WarmupSeconds = warmup, GapSeconds = gap });
        var svc = new TurnAutoResumeService(host, life, opts, NullLogger<TurnAutoResumeService>.Instance,
            (d, _) => { lock (events) events.Add("delay:" + (int)d.TotalSeconds); return Task.CompletedTask; });
        return (svc, host, life, events);
    }

    private static async Task RunToEndAsync(TurnAutoResumeService svc, FakeLifetime life)
    {
        await svc.StartAsync(CancellationToken.None);
        life.Start();
        await svc.ExecuteTask!.WaitAsync(Guard);
    }

    // Даём фоновой задаче провернуться: если бы она шла дальше ожидания, за эти ходы она бы дошла до TakeCandidates
    private static async Task SpinAsync()
    {
        for (var i = 0; i < 50; i++) await Task.Yield();
    }

    [Fact]
    public async Task ДоApplicationStarted_Ничего_НеОтправляется()
    {
        var (svc, host, _, events) = Make();
        await svc.StartAsync(CancellationToken.None);
        await SpinAsync();

        host.TakeCalls.Should().Be(0, "до ApplicationStarted кандидатов никто не берёт");
        events.Should().BeEmpty();
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ПослеStartedИПрогрева_Eligible_СессияВозобновляется()
    {
        var (svc, _, life, events) = Make(ids: 1, warmup: 15);
        await RunToEndAsync(svc, life);

        // Прогрев строго раньше первой отправки
        events.Should().Equal("delay:15", "send:s1");
    }

    [Fact]
    public async Task ФлагВыключен_ОтправокНет()
    {
        var (svc, host, life, events) = Make(ids: 3);
        host.FlagOn = _ => false;
        await RunToEndAsync(svc, life);

        events.Should().NotContain(e => e.StartsWith("send:"), "флаг владельца выключен — возобновлять нельзя");
    }

    [Fact]
    public async Task ПотолокMaxPerBoot_ОтправляетсяТолькоДесять_ИзДвенадцати()
    {
        var (svc, _, life, events) = Make(ids: 12, max: 10);
        await RunToEndAsync(svc, life);

        var sends = events.Where(e => e.StartsWith("send:")).ToList();
        sends.Should().HaveCount(10);
        sends.Should().NotContain(["send:s11", "send:s12"], "11-й и 12-й сверх потолка");
        // Пауза Gap между отправками: 9 штук между десятью
        events.Count(e => e == "delay:5").Should().Be(9);
    }

    [Fact]
    public async Task ИсключениеВОднойСессии_ОстальныеДевятьПродолжаются()
    {
        var (svc, host, life, events) = Make(ids: 10, max: 10);
        host.OnResume = id => id == "s3"
            ? throw new InvalidOperationException("сбой resume")
            : Task.FromResult(new TurnResumeSendResult(true));
        await RunToEndAsync(svc, life);

        svc.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("сервис сам себя не роняет");
        var ok = events.Where(e => e.StartsWith("send:") && e != "send:s3").ToList();
        ok.Should().HaveCount(9, "остальные 9 сессий отработали");
    }

    [Fact]
    public async Task ДоЗаписиМаркеровОбрыва_ПерваяОтправка_НеПроисходит()
    {
        var (svc, host, life, events) = Make(markersDone: false);
        await svc.StartAsync(CancellationToken.None);
        life.Start();
        await SpinAsync();

        events.Should().NotContain(e => e.StartsWith("send:"), "маркеры обрыва ещё не записаны");
        host.TakeCalls.Should().Be(0);

        host.MarkersTcs.SetResult();
        await svc.ExecuteTask!.WaitAsync(Guard);
        events.Should().Contain("send:s1", "после записи маркеров отправка идёт");
    }
}
