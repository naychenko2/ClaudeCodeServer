using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// S2a-10c: отказ хаба по правилу «владелец или автор идущего хода» и по праву Configure несёт
/// машинный код ВНУТРИ текста HubException. Выбор формы не произвольный: SignalR доносит до
/// клиента только строку error — @microsoft/signalr 10 собирает из неё Error(invocationEvent.error)
/// и errorData не читает вовсе (в dist/esm/HubConnection.js обращения к errorData нет), поэтому
/// «HubException с data» для фронта бесполезен: код может жить только в сообщении.
/// Форма стабильная: «код: пояснение», код равен REST body.code (not_chat_author,
/// owner_settings). Префикс выбран не для красоты: по нему такой отказ опознаёт фронт
/// (participantGate.denialCodeOf, задача S2a-F7), а прежнему «Доступ запрещён» остался путь
/// всех прочих отказов — они приходят без кода (тест «чужому чату» ниже).
/// До клиента строка доходит в payload.error как есть: JS-клиент фронта делает из неё
/// Error(invocationEvent.error) (dist/esm/HubConnection.js:350) и errorData не читает вовсе, а
/// фронт уже различает отказы хаба подстрокой этой строки (useSession.ts:681). .NET-клиент
/// тестов ставит поверх свой префикс («An unexpected error occurred invoking 'Interrupt' on the
/// server. …»), поэтому конец строки и есть то, что сказал сервер: проверки читают EndWith.
/// Идём боевым входом: соединение с hubs/session под токеном человека и вызов метода хаба, как в
/// проде; точка доступа НЕ подменена — общий проект включён рубильником и флагом, как у фронта
/// (ср. RunTurnGateTests, где IProjectAccess подменяется «чистой» ради матрицы прав).
/// </summary>
public class HubDenialCodeTests : IAsyncDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _owner;
    private readonly HttpClient _editor;
    private readonly List<HubConnection> _connections = [];

    public HubDenialCodeTests()
    {
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _owner = _factory.CreateAuthenticatedClient();
        _editor = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        _factory.Dispose();
    }

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    // ─── «Стоп» и ответ на вопрос: не автору — код, автору и владельцу — проход ───────────

    [Fact]
    public async Task Стоп_УчастникНеАвторХода_НесётNotChatAuthor()
    {
        var chatId = await SharedChatAsync();
        await SendAsync(_owner, chatId, "ход ведёт владелец"); // актор идущего хода — владелец

        var conn = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        Func<Task> act = () => conn.InvokeAsync("Interrupt", chatId);

        var ex = (await act.Should().ThrowAsync<HubException>()).Which;
        // Конец строки клиента — текст сервера целиком, ничего после него не добавлено:
        // отказ звучает «код: пояснение» — тот же код и тот же смысл, что у REST body.code
        // ручки pending/preempt (TurnRightExtensions.DenyTurnAuthor → ChatAuthorGate).
        ex.Message.Should().EndWith($"{ChatAuthorGate.ErrorCode}: Только автор хода или владелец проекта",
            "фронт (S2a-F7) опознаёт отказ по префиксу кода");
    }

    [Fact]
    public async Task ОтветНаВопросАгента_УчастникНеАвторХода_НесётNotChatAuthor()
    {
        var chatId = await SharedChatAsync();
        await SendAsync(_owner, chatId, "ход ведёт владелец");

        var conn = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        Func<Task> act = () => conn.InvokeAsync("AnswerQuestion", chatId, "toolu_test", "ответ");

        var ex = (await act.Should().ThrowAsync<HubException>()).Which;
        ex.Message.Should().EndWith($"{ChatAuthorGate.ErrorCode}: Только автор хода или владелец проекта");
    }

    // Автор идущего хода и владелец чата — не отшиты правом. «Стоп» в тестовом хосте не даёт
    // отказа и не даёт живого прерывания: стаб-адаптера нет (HasLiveTurn = false), поэтому
    // Interrupt по занятому без прогона чату — уборка его состояния (SessionManager.InterruptCore,
    // «реанимируем чат»), а не исключение. Именно отсутствие HubException здесь и есть проверка
    // права: с отказом по правилу «автор или владелец» краснели бы оба вызова.
    [Fact]
    public async Task Стоп_АвторХодаИВладелец_ГейтПроходят_БезОтказа()
    {
        var chatId = await SharedChatAsync();
        await SendAsync(_editor, chatId, "ход ведёт редактор"); // актор идущего хода — редактор

        var asAuthor = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var asOwner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);

        Func<Task> byAuthor = () => asAuthor.InvokeAsync("Interrupt", chatId);
        Func<Task> byOwner = () => asOwner.InvokeAsync("Interrupt", chatId);
        await byAuthor.Should().NotThrowAsync<HubException>("автор идущего хода останавливает свой ход");
        await byOwner.Should().NotThrowAsync<HubException>("владелец останавливает любой ход своего чата");
    }

    // ─── настройки чата (Configure) — тот же формат с кодом owner_settings ────────────────

    [Fact]
    public async Task РежимПрав_Участнику_НесётOwnerSettings_ВладелецПроходит()
    {
        var chatId = await SharedChatAsync();

        var asMember = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        Func<Task> denied = () => asMember.InvokeAsync("SetMode", chatId, "auto");
        var ex = (await denied.Should().ThrowAsync<HubException>()).Which;
        ex.Message.Should().EndWith($"{OwnerSettingsGate.ErrorCode}: Настройки чата выбирает владелец",
            "тот же код, что у REST DenyOwnerTurn: фронт по нему держит выключенные строки меню модели");

        var asOwner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        Func<Task> allowed = () => asOwner.InvokeAsync("SetMode", chatId, "auto");
        await allowed.Should().NotThrowAsync<HubException>("настройки чата — право владельца");
    }

    // Чужому чату кода нет: он его не видит, и отказ не должен раскрывать существование чата
    // (тот же порядок, что 404 вместо 403 в REST). Прежний текст — без изменений.
    [Fact]
    public async Task Стоп_ЧужомуЧату_ОбщийОтказБезКода()
    {
        var chatId = await SharedChatAsync(member: null); // второй пользователь — не участник проекта

        var conn = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        Func<Task> act = () => conn.InvokeAsync("Interrupt", chatId);

        // Ровно прежняя строка, без кода: чужому чату не показываем ничего, что выдало бы
        // его существование — тот же порядок, что 404 вместо 403 в REST
        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().EndWith("Доступ запрещён");
    }

    // ─── обход ────────────────────────────────────────────────────────────────────────────

    /// <summary>Проект владельца с чатом; второй пользователь — участник с ролью
    /// <paramref name="member"/> (null — не участник: чат для него чужой).</summary>
    private async Task<string> SharedChatAsync(ProjectRole? member = ProjectRole.Editor)
    {
        foreach (var c in new[] { _owner, _editor })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var project = await _owner.PostAsJsonAsync("/api/projects", new { name = $"Хаб-отказ-{tag}" });
        project.EnsureSuccessStatusCode();
        var projectId = IdOf(await project.Content.ReadFromJsonAsync<JsonElement>());

        if (member is { } role)
        {
            var ownerUser = _factory.Services.GetRequiredService<UserStore>()
                .FindByUsername(TestWebApplicationFactory.TestUsername)!;
            var memberUser = _factory.Services.GetRequiredService<UserStore>()
                .FindByUsername(TestWebApplicationFactory.SecondUsername)!;
            _factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
                [new ProjectMember { UserId = memberUser.Id, Role = role, AddedBy = ownerUser.Id }]);
        }

        var chat = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        return IdOf(await chat.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>Послать сообщение (wait=none): первый — начинает ход, его автор — податель
    /// (реестр актора S2a-1). Стаб-адаптер ход не завершает — он остаётся идущим.</summary>
    private static async Task SendAsync(HttpClient client, string chatId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/sessions/{chatId}/messages", new { text, wait = "none" });
        ((int)response.StatusCode).Should().BeOneOf([200, 202], $"подача «{text}»");
    }

    private async Task<HubConnection> ConnectAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.HandshakeTimeout = TimeSpan.FromSeconds(60);
        connection.ServerTimeout = TimeSpan.FromSeconds(60);
        await connection.StartAsync();
        _connections.Add(connection);
        return connection;
    }
}
