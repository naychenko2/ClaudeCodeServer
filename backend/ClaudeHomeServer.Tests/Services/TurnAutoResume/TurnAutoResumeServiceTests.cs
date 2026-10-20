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
        // Id'ы, для которых ResumeAsync возвращает Sent=false (ветка отказа: битый
        // транскрипт / Waiting-статус отбиты Eligibility'ем) — раньше не была покрыта.
        public HashSet<string> NotSent = [];
        // Id'ы, реально отправленные (Sent=true) — для проверки, что потолок считает
        // реальные отправки, а не попытки.
        public readonly List<string> SentIds = [];

        // Подмена сессии, которую отдаёт GetSession (регресс-тест причины 2: проектный чат
        // с OwnerId = null — владелец резолвится из проекта, а не из OwnerId).
        public Func<string, Session>? SessionFactory;
        public Task AbortMarkersWritten => MarkersTcs.Task;
        public IReadOnlyList<string> TakeCandidates() { TakeCalls++; return Ids; }
        public Session? GetSession(string id) =>
            SessionFactory?.Invoke(id) ?? new Session { Id = id, OwnerId = "owner-" + id, ProjectId = null };
        public Project? GetProject(string projectId) => null;
        // Владелец резолвится как в боевом адаптере (ResolveOwnerId): у проектной сессии
        // OwnerId пустой — владелец из проекта. Тесты дают OwnerId напрямую, поэтому здесь
        // резолв тривиальный; регресс-тест причины 2 подменяет GetSession на проектный чат.
        public bool IsEnabledFor(Session session) => FlagOn(session.OwnerId ?? "");
        public bool IsDeviceOnline(Project project) => true;
        public bool LastTurnWasDeployOrRollback(Session session, Project? project) => false;
        public Task<TurnResumeSendResult> ResumeAsync(string sessionId)
        {
            lock (Log) Log.Add("send:" + sessionId);
            if (OnResume is { } f) return f(sessionId);
            var sent = !NotSent.Contains(sessionId);
            if (sent) lock (SentIds) SentIds.Add(sessionId);
            return Task.FromResult(new TurnResumeSendResult(sent, sent ? null : "refused"));
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

    // Регресс (ADR-033, причина 2): у проектной сессии OwnerId пустой — владелец берётся
    // из проекта. Здесь проверяется только оркестратор (FakeHost владельца не резолвит); сам
    // резолв владельца в боевом адаптере покрыт SessionManagerTurnResumeHostTests (мутация
    // «session.OwnerId вместо ResolveOwnerId(session)» краснит их, а не этот тест).
    [Fact]
    public async Task ПроектныйЧат_СПустымOwnerId_ФлагВладельцаПроектаВключён_Возобновляется()
    {
        var (svc, host, life, events) = Make(ids: 1);
        // Проектный чат: OwnerId = null, владелец резолвится из проекта (флаг у него включён).
        host.SessionFactory = id => new Session { Id = id, OwnerId = null, ProjectId = "proj-1" };
        host.FlagOn = _ => true; // флаг включён у резолвлённого владельца
        await RunToEndAsync(svc, life);

        events.Should().Contain("send:s1",
            "проектный чат с пустым OwnerId возобновляется: владелец резолвится из проекта, флаг у него включён");
    }

    [Fact]
    public async Task ПотолокMaxPerBoot_ОтправляетсяТолькоДесять_ИзДвенадцати()
    {
        var (svc, host, life, events) = Make(ids: 12, max: 10);
        // 2 из 12 отбиты Eligibility'ем (битый транскрипт / Waiting-статус): Sent=false.
        // Потолок обязан считать только реальные отправки, а не попытки.
        host.NotSent = new() { "s1", "s2" };
        await RunToEndAsync(svc, life);

        var sends = events.Where(e => e.StartsWith("send:")).ToList();
        // Все 12 кандидатов дошли до ResumeAsync (12 попыток), чтобы набрать 10 реальных:
        // потолок считает реальные отправки, а не попытки. Считай попытки — остановился бы
        // на 10-й попытке (s1–s10) и реальных было бы 8 (s3–s10).
        sends.Should().HaveCount(12, "потолок по реальным отправкам, не по попыткам");
        sends.Should().Contain(["send:s1", "send:s2"], "отбитые кандидаты тоже попытаны");
        // Реальные отправки — ровно 10 (s3–s12); s1/s2 — Sent=false, в потолок не входят.
        host.SentIds.Should().HaveCount(10, "потолок MaxPerBoot=10 по реальным отправкам");
        host.SentIds.Should().Equal(Enumerable.Range(3, 10).Select(i => "s" + i),
            "реальные отправки — s3–s12, отбитые s1/s2 не в счёт");
        // Пауза Gap между реальными отправками: 9 штук между десятью
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
