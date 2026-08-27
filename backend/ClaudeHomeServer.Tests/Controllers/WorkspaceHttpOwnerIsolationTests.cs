using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Изоляция и живой путь http-тулсета рабочего пространства (ADR-012, фаза 2 волна 3) —
/// самый опасный по данным сервер волны: файловые операции в ЛЮБОМ проекте владельца,
/// причём projectId контролирует модель (параметр инструмента, не хвост маршрута).
///
/// Оси приёмки задачи волны 3:
/// - живой ход: дерево файлов, поиск и запись в другом проекте ТОГО ЖЕ владельца работают;
/// - токен владельца A не читает и не пишет файлы проекта владельца B (проверка одинакова
///   для чтения и записи — урок приёмки волны 2, где запись проверяла только readonly);
/// - path traversal: «..» в пути отбивается на КАЖДОЙ файловой операции (SafeJoin
///   внутри FileService, тулсет его не обходит);
/// - чужая сессия в хвосте — ни состава, ни вызова (fail-closed).
/// Парные тесты: NotesHttpOwnerIsolationTests (волна 2), MemoryHttpTransportTests (волна 1).
/// </summary>
public class WorkspaceHttpOwnerIsolationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private static async Task<string> CreateProjectAsync(HttpClient client, string prefix)
    {
        var resp = await client.PostAsJsonAsync("/api/projects", new { name = $"{prefix}-{Guid.NewGuid():N}" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client, string projectId)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string sessionId,
        string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string TextOf(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement rpc) =>
        rpc.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean();

    /// <summary>
    /// Живой ход приёмки: чтение дерева файлов ДРУГОГО проекта того же владельца, поиск
    /// по имени и запись — работают без единого процесса node.
    /// </summary>
    [Fact]
    public async Task ЖивойХод_ДеревоПоискЗапись_РаботаютВЧужомПроектеВладельца()
    {
        var client = Client;
        var chatProjectId = await CreateProjectAsync(client, "wsp-chat");
        var otherProjectId = await CreateProjectAsync(client, "wsp-other");
        var sessionId = await CreateSessionAsync(client, chatProjectId);

        // Запись в ДРУГОЙ проект владельца — основной сценарий wsp
        var written = await CallToolAsync(client, sessionId, "files_write",
            new { projectId = otherProjectId, path = "docs/note.md", content = "# Из http-тулсета" });
        IsError(written).Should().BeFalse(TextOf(written));
        TextOf(written).Should().Contain("docs/note.md");

        var tree = await CallToolAsync(client, sessionId, "files_tree",
            new { projectId = otherProjectId });
        IsError(tree).Should().BeFalse(TextOf(tree));
        TextOf(tree).Should().Contain("docs/note.md", "дерево видит только что записанный файл");

        var search = await CallToolAsync(client, sessionId, "files_search",
            new { projectId = otherProjectId, query = "note" });
        IsError(search).Should().BeFalse(TextOf(search));
        TextOf(search).Should().Contain("docs/note.md");

        var read = await CallToolAsync(client, sessionId, "files_read",
            new { projectId = otherProjectId, path = "docs/note.md" });
        IsError(read).Should().BeFalse(TextOf(read));
        TextOf(read).Should().Contain("Из http-тулсета");
    }

    /// <summary>
    /// Токен владельца A с ЧУЖИМ projectId (проект владельца B): ни чтения, ни записи.
    /// projectId контролирует модель, поэтому владение проверяется на каждый вызов —
    /// одинаково для read- и write-инструментов.
    /// </summary>
    [Fact]
    public async Task ЧужойПроект_НиЧтенияНиЗаписи()
    {
        var clientA = Client;
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var projectA = await CreateProjectAsync(clientA, "wsp-a");
        var sessionA = await CreateSessionAsync(clientA, projectA);
        var projectB = await CreateProjectAsync(clientB, "wsp-b");

        // Чтение чужого проекта
        var tree = await CallToolAsync(clientA, sessionA, "files_tree", new { projectId = projectB });
        IsError(tree).Should().BeTrue("проект владельца B недоступен токену владельца A");
        TextOf(tree).Should().Contain("не найден или недоступен");

        var read = await CallToolAsync(clientA, sessionA, "files_read",
            new { projectId = projectB, path = "any.md" });
        IsError(read).Should().BeTrue();

        // Запись в чужой проект — та же проверка, что и на чтении
        var write = await CallToolAsync(clientA, sessionA, "files_write",
            new { projectId = projectB, path = "hack.md", content = "x" });
        IsError(write).Should().BeTrue("запись обязана проверять владение так же, как чтение");
        TextOf(write).Should().Contain("не найден или недоступен");

        var mkdir = await CallToolAsync(clientA, sessionA, "files_mkdir",
            new { projectId = projectB, path = "hacked" });
        IsError(mkdir).Should().BeTrue();

        var rename = await CallToolAsync(clientA, sessionA, "files_rename",
            new { projectId = projectB, oldPath = "a.md", newPath = "b.md" });
        IsError(rename).Should().BeTrue();

        // Карточка проекта и его база знаний — тоже за проверкой владения
        var card = await CallToolAsync(clientA, sessionA, "projects_get", new { projectId = projectB });
        IsError(card).Should().BeTrue();
    }

    /// <summary>
    /// Path traversal: «..» в пути отбивается на каждой файловой операции — SafeJoin внутри
    /// FileService, тулсет его не обходит. Инвариант проекта, перенос транспорта не повод
    /// его размывать (требование задачи волны 3).
    /// </summary>
    [Theory]
    [InlineData("files_read")]
    [InlineData("files_write")]
    [InlineData("files_mkdir")]
    [InlineData("files_tree")]
    [InlineData("files_delete")]
    public async Task PathTraversal_ОтбиваетсяНаВсехФайловыхОперациях(string tool)
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-traversal");
        var sessionId = await CreateSessionAsync(client, projectId);
        // Путь наружу проекта: и через ../, и через абсолютную форму. Целевое имя заведомо
        // существует у соседа по каталогу проектов — успех означал бы утечку
        const string escape = "../../../../windows/system32/drivers/etc/hosts";

        var result = await CallToolAsync(client, sessionId, tool, new
        {
            projectId,
            path = escape,
            content = "x",
        });

        // files_delete живёт в секции destructive (флаг + tool-ключ): в тестовой среде она
        // выключена, и отказ приходит по секции — это тоже «наружу не пустили»
        IsError(result).Should().BeTrue($"{tool} обязан отбить выход за пределы проекта");
        TextOf(result).Should().Match(t =>
            t.Contains("за пределы проекта") || t.Contains("не найден") || t.Contains("недоступен"),
            "отказ, а не содержимое чужого файла");
    }

    /// <summary>
    /// Токен B с хвостом сессии владельца A: ни состава, ни вызова — доступ к рабочему
    /// пространству закрывается целиком (fail-closed), а не «пустым деревом файлов».
    /// </summary>
    [Fact]
    public async Task ЧужаяСессияВХвосте_НиСоставаНиВызова()
    {
        var clientA = Client;
        var projectA = await CreateProjectAsync(clientA, "wsp-tail-a");
        var sessionA = await CreateSessionAsync(clientA, projectA);
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var list = await clientB.PostAsJsonAsync($"/mcp/wsp/{sessionA}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        list.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools").GetArrayLength()
            .Should().Be(0, "чужая сессия — пустой состав (fail-closed)");

        var call = await CallToolAsync(clientB, sessionA, "files_tree", new { projectId = projectA });
        IsError(call).Should().BeTrue();
        TextOf(call).Should().Contain("другому владельцу");
    }

    /// <summary>
    /// Состав на своей сессии непуст и несёт базовые секции (projects/files/knowledge/search),
    /// а разрушающая секция в дефолтной среде выключена — её инструментов в составе нет.
    /// </summary>
    [Fact]
    public async Task СоставСвоейСессии_БазовыеСекцииЕсть_ДеструктивВыключен()
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-tools");
        var sessionId = await CreateSessionAsync(client, projectId);

        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.EnsureSuccessStatusCode();
        var tools = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList();

        tools.Should().Contain(["projects_list", "files_tree", "files_read", "files_write",
            "knowledge_search", "search_unified"]);
        tools.Should().NotContain("files_delete",
            "секция destructive за фич-флагом workspace-destructive — в дефолтной среде выключена");
        tools.Should().NotContain("chats_delete");

        // Контекстная заметка в описании projects_list подставляется живьём по сессии
        var listResp = JsonSerializer.Deserialize<JsonElement>(
            await (await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}",
                new { jsonrpc = "2.0", id = 2, method = "tools/list" })).Content.ReadAsStringAsync());
        var projectsList = listResp.GetProperty("result").GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "projects_list");
        projectsList.GetProperty("description").GetString()!
            .Should().Contain(projectId, "контекст чата виден модели в описании")
            .And.NotContain("{CONTEXT_NOTE}", "плейсхолдер обязан быть заменён");
    }

    /// <summary>
    /// ГЕЙТ СЕКЦИИ НА ВЫЗОВЕ (defense-in-depth, урок приёмки волны 2): инструмента нет в
    /// составе, но вызов до обработчика ВСЁ РАВНО доходит — и обязан отбиться по секции,
    /// а не выполниться. Проверяем на деструктиве: файл после отказа цел.
    /// </summary>
    [Fact]
    public async Task ВыключеннаяСекция_ОтбиваетсяНаВызове_АНеТолькоВСоставе()
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-gate");
        var sessionId = await CreateSessionAsync(client, projectId);

        var written = await CallToolAsync(client, sessionId, "files_write",
            new { projectId, path = "keep.md", content = "цел" });
        IsError(written).Should().BeFalse(TextOf(written));

        // files_delete в составе отсутствует (секция destructive выключена) — но вызвать его
        // модель может: транспорт имени не фильтрует
        var deleted = await CallToolAsync(client, sessionId, "files_delete",
            new { projectId, path = "keep.md" });
        IsError(deleted).Should().BeTrue("вызов инструмента выключенной секции обязан отбиваться");
        TextOf(deleted).Should().Contain("секция destructive выключена");

        var read = await CallToolAsync(client, sessionId, "files_read",
            new { projectId, path = "keep.md" });
        IsError(read).Should().BeFalse("файл обязан остаться на месте");
        TextOf(read).Should().Contain("цел");
    }

    /// <summary>
    /// ПУСТОЕ ЗНАЧЕНИЕ = ОЧИСТКА, а не «параметр не передан» (урок приёмки волны 2:
    /// OptionalArg трактовал "" как отсутствие, и снятие значения молча не выполнялось,
    /// хотя модель рапортовала об успехе). Семантика решается по ContainsKey.
    /// </summary>
    [Fact]
    public async Task ПустаяСтрока_Очищает_АОтсутствиеКлюча_НеМеняет()
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-clear");
        var sessionId = await CreateSessionAsync(client, projectId);

        var set = await CallToolAsync(client, sessionId, "projects_update",
            new { projectId, systemPrompt = "Промпт проекта" });
        IsError(set).Should().BeFalse(TextOf(set));

        // Ключа нет — поле не трогаем
        var renamed = await CallToolAsync(client, sessionId, "projects_update",
            new { projectId, name = "wsp-clear-renamed" });
        IsError(renamed).Should().BeFalse(TextOf(renamed));
        var afterRename = await CallToolAsync(client, sessionId, "projects_get", new { projectId });
        JsonSerializer.Deserialize<JsonElement>(TextOf(afterRename))
            .GetProperty("systemPrompt").GetString()
            .Should().Be("Промпт проекта", "отсутствие ключа — «не менять»");

        // Пустая строка — очистка
        var cleared = await CallToolAsync(client, sessionId, "projects_update",
            new { projectId, systemPrompt = "" });
        IsError(cleared).Should().BeFalse(TextOf(cleared));
        var afterClear = await CallToolAsync(client, sessionId, "projects_get", new { projectId });
        JsonSerializer.Deserialize<JsonElement>(TextOf(afterClear))
            .GetProperty("systemPrompt").GetString()
            .Should().BeEmpty("пустая строка обязана ОЧИЩАТЬ, а не игнорироваться");
    }
}
