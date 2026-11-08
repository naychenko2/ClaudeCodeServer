using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// S2a-10c: отказ хаба по правилу «владелец или автор идущего хода» и по праву Configure несёт
/// машинный код ВНУТРИ текста HubException. Выбор формы не произвольный: SignalR доносит до
/// клиента только строку error — @microsoft/signalr 10 собирает из неё Error(invocationEvent.error)
/// и errorData не читает вовсе (в dist/esm/HubConnection.js обращения к errorData нет), поэтому
/// «HubException с data» для фронта бесполезен: код может жить только в сообщении.
/// Форма стабильная: «код: пояснение», код равен REST body.code (not_chat_author,
/// owner_settings). Но текст HubException доходит не дословно: DefaultHubDispatcher и
/// ErrorMessageHelper оборачивают его в «An unexpected error occurred invoking '<метод>' on the
/// server. HubException: <код>: <текст>» — доказано пробой ревью R-S2a-10c: тест увидел строку
/// с подставленным PROBE-ABC целиком. Клиент опознаёт код по маркеру «HubException: <код>:»
/// (константа SessionHub.DenialMarker) или «<код>:» в начале строки (эта форма — только у
/// REST body.code), а не по префиксу всей строки: по нему ищет разбор фронта
/// (participantGate.denialCodeOf, шаг S2a-F7). .NET-клиент тестов ставит поверх серверного текста
/// ту же обёртку, поэтому проверки читают фактический ex.Message на наличие маркера с кодом,
/// а не конец строки: хвост строки зелёный и тогда, когда код для фронта недостижим —
/// ровно та поломка, которую нашло ревью в прежних EndWith-проверках.
/// Чужому чату кода нет (тест «чужому чату»); читателю без права RunTurn — тоже: он не «не автор
/// хода», он не может вести ход (тест «читателю проекта»).
/// Идём боевым входом: соединение с hubs/session под токеном человека и вызов метода хаба, как в
/// проде; точка доступа НЕ подменена — общий проект включён рубильником и флагом, как у фронта
/// (ср. RunTurnGateTests, где IProjectAccess подменяется «чистой» ради матрицы прав).
/// </summary>
[Trait("Category", "Host")]
public class HubDenialCodeTests : IAsyncDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _owner;
    private readonly HttpClient _editor;
    private readonly List<HubConnection> _connections = [];
    private readonly LogSink _log = new();

    public HubDenialCodeTests()
    {
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        // Провайдер логов встаёт в хост до его постройки (ExtraServices применяется после
        // встроенных регистраций): тест журнала читает фактические записи хаба, а не догадки
        _factory.ExtraServices = services => services.AddSingleton<ILoggerProvider>(_log);
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
        // Фактическая строка клиента: обёртка SignalR + серверский текст «код: пояснение».
        // Контракт — достижимость кода маркером, а не хвост строки: хвост зелёный и тогда,
        // когда код для фронта недостижим (поломка, найденная ревью R-S2a-10c).
        ex.Message.Should().Contain($"{SessionHub.DenialMarker}{ChatAuthorGate.ErrorCode}:",
            "клиент опознаёт код по маркеру «HubException: <код>:» — по нему ищет отказ фронт (S2a-F7)");
    }

    [Fact]
    public async Task ОтветНаВопросАгента_УчастникНеАвторХода_НесётNotChatAuthor()
    {
        var chatId = await SharedChatAsync();
        await SendAsync(_owner, chatId, "ход ведёт владелец");

        var conn = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        Func<Task> act = () => conn.InvokeAsync("AnswerQuestion", chatId, "toolu_test", "ответ");

        var ex = (await act.Should().ThrowAsync<HubException>()).Which;
        // У AnswerQuestion нет REST-входа: маркер на хабе — единственный путь кода до фронта
        ex.Message.Should().Contain($"{SessionHub.DenialMarker}{ChatAuthorGate.ErrorCode}:");
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
        ex.Message.Should().Contain($"{SessionHub.DenialMarker}{OwnerSettingsGate.ErrorCode}:",
            "тот же код, что у REST DenyOwnerTurn: фронт по маркеру держит выключенные строки меню модели");

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
        // его существование — тот же порядок, что 404 вместо 403 в REST. Обёртка «HubException: »
        // есть в строке любого отказа — проверяется ровно отсутствие кода после неё
        var ex = (await act.Should().ThrowAsync<HubException>()).Which;
        ex.Message.Should().Contain("Доступ запрещён");
        ex.Message.Should().NotContain($"{SessionHub.DenialMarker}{ChatAuthorGate.ErrorCode}:");
        ex.Message.Should().NotContain($"{SessionHub.DenialMarker}{OwnerSettingsGate.ErrorCode}:");
    }

    // Читатель (роль reader, права RunTurn нет вовсе) — не «не автор хода»: он не может вести
    // ход. not_chat_author относится к тому, кто ход в этом чате вести может, но не является
    // автором этого хода и не владелец; читателю — прежний общий отказ без кода
    // (п. 2 вердикта R-S2a-10c: «Только автор хода» описывает не его случай).
    [Fact]
    public async Task Стоп_ЧитателюПроекта_ОбщийОтказБезКода()
    {
        var chatId = await SharedChatAsync(member: ProjectRole.Reader);

        var conn = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        Func<Task> act = () => conn.InvokeAsync("Interrupt", chatId);

        // Чат читателю виден (403-путь, не 404), но отказ не называет право, которого у него
        // никогда не было: маркера с кодом в строке нет
        var ex = (await act.Should().ThrowAsync<HubException>()).Which;
        ex.Message.Should().Contain("Доступ запрещён");
        ex.Message.Should().NotContain($"{SessionHub.DenialMarker}{ChatAuthorGate.ErrorCode}:");
    }

    // ─── журнал отказов: с кодом — код, без кода — «-» ──────────────────────────────────────

    [Fact]
    public async Task ЖурналОтказовХаба_БезКодаПишетМинус_СКодомПишетКод()
    {
        var chatId = await SharedChatAsync();                // второй пользователь — участник (editor)
        var foreignId = await SharedChatAsync(member: null); // второй пользователь — не участник

        var conn = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await ((Func<Task>)(() => conn.InvokeAsync("SetMode", chatId, "auto"))).Should().ThrowAsync<HubException>();
        await ((Func<Task>)(() => conn.InvokeAsync("Interrupt", foreignId))).Should().ThrowAsync<HubException>();

        // Строка журнала — инструмент, которым отказ ищут на сервере: пустое «код=» делает её
        // похожей на оборванную запись и не различается с отказом без кода (п. 3 вердикта R-S2a-10c)
        var lines = _log.Snapshot().Where(l => l.Contains("Хаб отказал в доступе")).ToList();
        lines.Should().HaveCount(2, "вызваны ровно два отказа: с кодом owner_settings и без кода");
        lines.Should().Contain(l => l.Contains("код=owner_settings"));
        lines.Should().Contain(l => l.EndsWith("код=-"), "отказ без кода пишет '-', а не пустое значение");
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

    // Записи лога хоста уровня Warning и выше: тест журнала читает фактические строки хаба
    // (образец — ErrorLogSink из NotesDisabledTests; уровень на ступень ниже — отказ хаб
    // пишет LogWarning)
    private sealed class LogSink : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Snapshot()
        {
            lock (_entries) return [.. _entries];
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        public void Dispose() { }

        private void Add(string entry)
        {
            lock (_entries) _entries.Add(entry);
        }

        private sealed class Sink(LogSink owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;
                owner.Add($"{category}: {formatter(state, exception)}");
            }
        }
    }
}
