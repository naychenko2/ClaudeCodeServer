using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-B6d: стоп-сигнал — POST /api/child/stop-signal (ADR-041 ручка №9), сервис ChildStopSignalService
/// и разбор маркера тьютора в конце хода (TurnCompletion). Инвариант: уведомление взрослым не
/// раскрывает переписку — проверяем текст, полезную нагрузку (Url/SessionId/ProjectId), ответ ребёнку.
/// </summary>
[Trait("Category", "Host")]
public class ChildStopSignalTests : IClassFixture<ChildStopSignalTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraServices = services => services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
    }

    private sealed class FixedSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0.1m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0.5m;
    }

    private sealed class ThrowingBroadcaster : ISessionBroadcaster
    {
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) => throw new InvalidOperationException("сбой рассылки");
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    private const string KidsKey = "kids-api";
    private const string ChildPassword = "kids-test-password";
    private const string Secret = "секретный текст переписки 42";
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();
    private NotificationStore Store => _hosts.Services.GetRequiredService<NotificationStore>();
    private UserStore Users => _hosts.Services.GetRequiredService<UserStore>();

    public ChildStopSignalTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-child-stop-" + Guid.NewGuid().ToString("N"));
        _admin = _hosts.CreateAuthenticatedClient();
    }

    private async Task<string> NewUser(string name, bool child, string[]? guardians = null)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        if (!child) return id;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        var from = local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var to = local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, providerKey = KidsKey, model = "sonnet",
            guardianUserIds = guardians ?? [],
            spaces = new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes = 40 } },
            dailyBudgetUsd = 2, monthlyBudgetUsd = 40,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = Users.GetById(id)!.ChildMode!;
        profile.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        profile.RulesSummary = "правила";
        return id;
    }

    private HttpClient As(string name) => _hosts.CreateAuthenticatedClient(name, ChildPassword);

    private async Task<string> CreateChat(HttpClient client)
    {
        var resp = await client.PostAsync("/api/child/spaces/lessons/chat", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("sessionId").GetString()!;
    }

    private static async Task<(HttpStatusCode Status, string Raw, string Code)> Post(HttpClient c, object body)
    {
        var r = await c.PostAsJsonAsync("/api/child/stop-signal", body);
        var raw = await r.Content.ReadAsStringAsync();
        var code = raw.Length > 0 && JsonSerializer.Deserialize<JsonElement>(raw).TryGetProperty("code", out var cd) ? cd.GetString() ?? "" : "";
        return (r.StatusCode, raw, code);
    }

    private async Task<List<NotificationListItem>> StopSignals(string userId) =>
        (await Store.GetListAsync(userId, 100)).Where(n => n.Type == "child_stop_signal").ToList();

    private string AdminId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    [Fact]
    public async Task Кнопка_202_УведомлениеАдминуИОпекуну_БезПереписки()
    {
        var guardian = await NewUser("kss-guardian", child: false);
        var child = await NewUser("kss-kid", child: true, guardians: [guardian]);
        var other = await NewUser("kss-stranger", child: false);
        var client = As("kss-kid");
        var sid = await CreateChat(client);
        var adminBefore = (await StopSignals(AdminId)).Count;

        var (status, raw, _) = await Post(client, new { sessionId = sid });

        status.Should().Be(HttpStatusCode.Accepted);
        JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("accepted").GetBoolean().Should().BeTrue();
        raw.Should().NotContain(sid, "ответ ребёнку не несёт ни id чата, ни списка получателей");
        (await StopSignals(AdminId)).Count.Should().Be(adminBefore + 1);
        var forGuardian = await StopSignals(guardian);
        forGuardian.Should().ContainSingle();
        (await StopSignals(other)).Should().BeEmpty("посторонний пользователь уведомления не получает");
        (await StopSignals(child)).Should().BeEmpty("сам ребёнок уведомления не получает");

        var n = forGuardian[0];
        // Пути к инварианту: текст, полезная нагрузка, ссылка
        n.Url.Should().BeNullOrEmpty("ссылки на чат нет");
        n.SessionId.Should().BeNullOrEmpty();
        n.ProjectId.Should().BeNullOrEmpty();
        (n.Title + " " + n.Body).Should().NotContain(sid).And.NotContain(Secret);
        n.Body.Should().Contain("Мне неприятно").And.Contain("Уроки");
    }

    [Fact]
    public async Task Кнопка_БезSessionId_Принимается()
    {
        await NewUser("kss-nosid", child: true);
        var (status, _, _) = await Post(As("kss-nosid"), new { });
        status.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Кнопка_ПовторВМинуту_429TooFrequent_ИОдноУведомление()
    {
        var child = await NewUser("kss-twice", child: true);
        var client = As("kss-twice");
        var sid = await CreateChat(client);
        var before = (await StopSignals(AdminId)).Count;

        (await Post(client, new { sessionId = sid })).Status.Should().Be(HttpStatusCode.Accepted);
        var (status, _, code) = await Post(client, new { sessionId = sid });

        status.Should().Be((HttpStatusCode)429);
        code.Should().Be("too_frequent");
        (await StopSignals(AdminId)).Count.Should().Be(before + 1, "повтор схлопнулся в уже отправленный");
    }

    [Fact]
    public async Task Кнопка_ЧужойИНесуществующийЧат_404ОдинаковоеТело()
    {
        await NewUser("kss-a", child: true);
        await NewUser("kss-b", child: true);
        var foreign = await CreateChat(As("kss-b"));
        var client = As("kss-a");

        var other = await Post(client, new { sessionId = foreign });
        var missing = await Post(client, new { sessionId = "no-such-chat" });

        other.Status.Should().Be(HttpStatusCode.NotFound);
        other.Code.Should().Be("chat_not_found");
        missing.Status.Should().Be(HttpStatusCode.NotFound);
        missing.Raw.Should().Be(other.Raw);
    }

    [Fact]
    public async Task Кнопка_СвойНеДетскийЧат_404()
    {
        var child = await NewUser("kss-own-adult", child: true);
        var sid = await CreateChat(As("kss-own-adult"));
        var own = Sessions.GetOwnedVisible(sid, child)!;
        own.ChildSpace = null; // свой чат ребёнка, но не детского пространства
        var before = (await StopSignals(AdminId)).Count;

        var (status, _, code) = await Post(As("kss-own-adult"), new { sessionId = own.Id });

        status.Should().Be(HttpStatusCode.NotFound);
        code.Should().Be("chat_not_found");
        (await StopSignals(AdminId)).Count.Should().Be(before, "отказ до рассылки");
    }

    [Fact]
    public async Task Raise_БезSessionIdИСSessionId_ОдноУведомление()
    {
        var guardian = await NewUser("kss-mix-g", child: false);
        var child = await NewUser("kss-mix", child: true, guardians: [guardian]);
        var svc = _hosts.Services.GetRequiredService<ChildStopSignalService>();

        var first = await svc.RaiseAsync(child, null, StopSignalSource.Child);
        var second = await svc.RaiseAsync(child, "chat-mix", StopSignalSource.Tutor);

        first.Should().Be(StopSignalOutcome.Raised);
        second.Should().Be(StopSignalOutcome.Collapsed);
        (await StopSignals(guardian)).Should().ContainSingle();
    }

    [Fact]
    public void Маркер_ПричинаСУглойСкобкой_Распознаётся()
    {
        var text = "до <ccs-stop-signal reason=\"ребёнок пишет <плохое> слово\"/> после";
        TeamProtocolMarkers.HasStopSignalMarker(text).Should().BeTrue();
        TeamProtocolMarkers.StripTeamProtocolMarkers(text).Should().Be("до  после");
    }

    [Fact]
    public async Task Кнопка_НеРебёнок_403NotChild()
    {
        await NewUser("kss-adult", child: false);
        var (status, _, code) = await Post(As("kss-adult"), new { });
        status.Should().Be(HttpStatusCode.Forbidden);
        code.Should().Be("not_child");
    }

    [Fact]
    public async Task Raise_ParallelSameChat_OneNotification()
    {
        var guardian = await NewUser("kss-par-g", child: false);
        var child = await NewUser("kss-par", child: true, guardians: [guardian]);
        var svc = _hosts.Services.GetRequiredService<ChildStopSignalService>();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            Task.Run(() => svc.RaiseAsync(child, "chat-par", i % 2 == 0 ? StopSignalSource.Tutor : StopSignalSource.Child))));

        outcomes.Count(o => o == StopSignalOutcome.Raised).Should().Be(1);
        (await StopSignals(guardian)).Should().ContainSingle("маркер и кнопка в одну секунду — одно уведомление");
    }

    [Fact]
    public async Task Raise_СбойУведомления_НеПадает()
    {
        var child = await NewUser("kss-fail", child: true);
        var sp = _hosts.Services;
        var broken = new NotificationService(Store, new ThrowingBroadcaster(),
            sp.GetRequiredService<PushService>(), sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<ProjectManager>(), NullLogger<NotificationService>.Instance);
        var svc = new ChildStopSignalService(Users, sp.GetRequiredService<IChildModePolicy>(),
            sp.GetRequiredService<ChildLimitsCalculator>(), broken, NullLogger<ChildStopSignalService>.Instance);

        var act = () => svc.RaiseAsync(child, "chat-x", StopSignalSource.Child);

        (await act.Should().NotThrowAsync()).Subject.Should().Be(StopSignalOutcome.Raised);
    }

    [Fact]
    public async Task МаркерТьютора_ВКонцеХода_ПоднимаетСигнал_ИВырезанИзЛентыИИстории()
    {
        var guardian = await NewUser("kss-tut-g", child: false);
        var child = await NewUser("kss-tut", child: true, guardians: [guardian]);
        var sid = await CreateChat(As("kss-tut"));
        var completion = _hosts.Services.GetRequiredService<TurnCompletion>();
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var marker = $"<ccs-stop-signal reason=\"{Secret}\"/>";

        await completion.OnMessageAsync(sid, acc, new TextDeltaMessage("Давай позовём взрослого. "));
        await completion.OnMessageAsync(sid, acc, new TextDeltaMessage(marker));
        await completion.OnMessageAsync(sid, acc, new ResultMessage("success", 10, 1, null, null));

        var signals = await WaitFor(() => StopSignals(guardian), l => l.Count > 0);
        signals.Should().ContainSingle();
        (signals[0].Title + signals[0].Body).Should().NotContain(Secret, "причина из маркера — часть переписки");
        var saved = string.Concat(acc.GetAll().OfType<StoredTextMessage>().Select(m => m.Text));
        saved.Should().NotContain("ccs-stop-signal").And.NotContain(Secret);
    }

    [Fact]
    public async Task МаркерВКодБлоке_СигналаНеПоднимает()
    {
        var guardian = await NewUser("kss-code-g", child: false);
        await NewUser("kss-code", child: true, guardians: [guardian]);
        var sid = await CreateChat(As("kss-code"));
        var completion = _hosts.Services.GetRequiredService<TurnCompletion>();
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await completion.OnMessageAsync(sid, acc,
            new TextDeltaMessage("Пример:\n```\n<ccs-stop-signal reason=\"x\"/>\n```\n"));
        await completion.OnMessageAsync(sid, acc, new ResultMessage("success", 10, 1, null, null));
        await Task.Delay(300);

        (await StopSignals(guardian)).Should().BeEmpty();
    }

    [Fact]
    public async Task МаркерВоВзросломЧате_СигналаНеПоднимает()
    {
        // Админ — взрослый: чат без ChildSpace; опекуном ребёнка назначен он же — сигнал был бы виден
        var adultId = await NewUser("kss-adult-chat", child: false);
        var kid = await NewUser("kss-adult-kid", child: true, guardians: [adultId]);
        var chat = await Sessions.CreateChatAsync(adultId, ClaudeMode.Default);
        var completion = _hosts.Services.GetRequiredService<TurnCompletion>();
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await completion.OnMessageAsync(chat.Id, acc, new TextDeltaMessage("<ccs-stop-signal reason=\"x\"/>"));
        await completion.OnMessageAsync(chat.Id, acc, new ResultMessage("success", 10, 1, null, null));
        await Task.Delay(300);

        (await StopSignals(adultId)).Should().BeEmpty("сигнал поднимается только в детском чате");
    }

    private static async Task<T> WaitFor<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var value = await read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            value = await read();
        }
        return value;
    }

    [Fact]
    public void МаркерПромпта_СтрогоСовпадаетСРазбором()
    {
        var sample = ChildSafetyPrompts.StopSignalMarker.Replace("…", "обидно");
        TeamProtocolMarkers.HasStopSignalMarker(sample).Should().BeTrue();
        TeamProtocolMarkers.StripTeamProtocolMarkers("до " + sample + " после").Should().Be("до  после");
    }
}
