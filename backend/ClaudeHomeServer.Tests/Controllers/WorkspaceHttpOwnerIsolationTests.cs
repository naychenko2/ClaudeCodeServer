using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

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

    // ---------- Правки приёмки волны 3.1 ----------

    /// <summary>
    /// Блокер 2: projects_create не принимает абсолютный rootPath. Раньше модель сама
    /// переносила границу SafeJoin — подключала проектом любую папку сервера, и файловые
    /// инструменты работали в ней законно. Теперь — отказ с подсказкой; папка выбирает
    /// сервер в стандартном каталоге, подключение существующей — человек через UI.
    /// </summary>
    [Fact]
    public async Task ProjectsCreate_АбсолютныйRootPath_Отказ()
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-rootpath");
        var sessionId = await CreateSessionAsync(client, projectId);
        var foreignDir = Path.Combine(_factory.TempDir, "wsp-alien-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(foreignDir);

        var denied = await CallToolAsync(client, sessionId, "projects_create", new
        {
            name = "Чужая папка",
            rootPath = foreignDir,
        });
        IsError(denied).Should().BeTrue("подключение произвольной папки из MCP — блокер волны 3.1");
        TextOf(denied).Should().Contain("недоступно из MCP");

        // Без rootPath проект создаётся в стандартном каталоге — штатный путь жив
        var created = await CallToolAsync(client, sessionId, "projects_create", new
        {
            name = "wsp-обычный-" + Guid.NewGuid().ToString("N")[..8],
        });
        IsError(created).Should().BeFalse(TextOf(created));
        var rootPath = JsonSerializer.Deserialize<JsonElement>(TextOf(created))
            .GetProperty("rootPath").GetString()!;
        rootPath.Should().NotBe(foreignDir, "путь выбирает сервер, а не модель");
    }

    /// <summary>
    /// Блокер 4: tags_apply для задачи шлёт task_changed(updated) в группу владельца —
    /// как REST-путь (TasksController.Update). Бродкаст был обязанностью контроллера и
    /// потерялся при переходе на прямые вызовы: интерфейс жил с устаревшими метками.
    /// Ловим через живой SignalR (LongPolling через TestServer).
    /// </summary>
    [Fact]
    public async Task TagsApply_Задача_ШлётTaskUpdated()
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-tags");
        var sessionId = await CreateSessionAsync(client, projectId);

        var task = await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача с метками" });
        task.EnsureSuccessStatusCode();
        var taskId = JsonSerializer.Deserialize<JsonElement>(
            await task.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(_factory.GetToken(
                    TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
            })
            .Build();
        var updated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("message", msg =>
        {
            if (msg.TryGetProperty("type", out var type) && type.GetString() == "task_changed"
                && msg.GetProperty("action").GetString() == "updated"
                && msg.GetProperty("task").GetProperty("id").GetString() == taskId)
                updated.TrySetResult(msg);
        });
        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync("JoinUser", me.GetProperty("userId").GetString());

            var applied = await CallToolAsync(client, sessionId, "tags_apply", new
            {
                entityType = "task",
                entityId = taskId,
                tags = new[] { "метка-из-mcp" },
            });
            IsError(applied).Should().BeFalse(TextOf(applied));

            var received = await Task.WhenAny(updated.Task, Task.Delay(15_000));
            received.Should().Be(updated.Task, "task_changed(updated) обязан прийти в группу владельца");
            updated.Task.Result.GetProperty("task").GetProperty("labels").EnumerateArray()
                .Select(l => l.GetString())
                .Should().Contain("метка-из-mcp", "в событии — уже обновлённая задача");
        }
        finally { await connection.DisposeAsync(); }
    }

    /// <summary>
    /// Зона сессии (AllowedProjectIds) обязана держать и поиск, и метки задач: суженная
    /// персона не читает и не правит данные за пределами своих проектов — ни задачи чужого
    /// проекта, ни личные задачи владельца (правка волны 3.1).
    /// </summary>
    [Fact]
    public async Task ЗонаСессии_SearchUnifiedИTagsApply_ДержатДанныеВнутри()
    {
        var client = Client;
        var inZone = await CreateProjectAsync(client, "wsp-zone-in");
        var outZone = await CreateProjectAsync(client, "wsp-zone-out");

        // Персона с Project-привязкой к inZone: её чат видит ровно один проект
        var persona = await client.PostAsJsonAsync("/api/personas", new { name = "Суженная зона" });
        persona.EnsureSuccessStatusCode();
        var personaId = JsonSerializer.Deserialize<JsonElement>(
            await persona.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var binding = await client.PutAsJsonAsync($"/api/personas/{personaId}/bindings", new
        {
            bindings = new object[] { new { type = "Project", target = inZone } },
        });
        binding.EnsureSuccessStatusCode();
        var chat = await client.PostAsJsonAsync($"/api/personas/{personaId}/chats",
            new { mode = "acceptEdits" });
        chat.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await chat.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var marker = "зонамаркер-" + Guid.NewGuid().ToString("N")[..8];
        var outTask = await client.PostAsJsonAsync($"/api/projects/{outZone}/tasks",
            new { title = $"{marker} чужой проект" });
        outTask.EnsureSuccessStatusCode();
        var outTaskId = JsonSerializer.Deserialize<JsonElement>(
            await outTask.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var personal = await client.PostAsJsonAsync("/api/tasks", new { title = $"{marker} личная" });
        personal.EnsureSuccessStatusCode();
        var personalId = JsonSerializer.Deserialize<JsonElement>(
            await personal.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var inTask = await client.PostAsJsonAsync($"/api/projects/{inZone}/tasks",
            new { title = $"{marker} своя" });
        inTask.EnsureSuccessStatusCode();
        var inTaskId = JsonSerializer.Deserialize<JsonElement>(
            await inTask.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        // search_unified: видна только задача проекта зоны
        var search = await CallToolAsync(client, sessionId, "search_unified", new { query = marker });
        IsError(search).Should().BeFalse(TextOf(search));
        var hits = TextOf(search);
        hits.Should().Contain("своя", "задача проекта зоны — в выдаче");
        hits.Should().NotContain("чужой проект", "задача вне зоны скрыта")
            .And.NotContain("личная", "личные задачи при суженной зоне скрыты");

        // tags_apply: метки меняются только у задачи проекта зоны
        var denyForeign = await CallToolAsync(client, sessionId, "tags_apply", new
        { entityType = "task", entityId = outTaskId, tags = new[] { "не-туда" } });
        IsError(denyForeign).Should().BeTrue("задача чужого проекта — вне зоны");
        TextOf(denyForeign).Should().Contain("вне разрешённой зоны");

        var denyPersonal = await CallToolAsync(client, sessionId, "tags_apply", new
        { entityType = "task", entityId = personalId, tags = new[] { "не-туда" } });
        IsError(denyPersonal).Should().BeTrue("личная задача при суженной зоне — вне зоны");

        var allow = await CallToolAsync(client, sessionId, "tags_apply", new
        { entityType = "task", entityId = inTaskId, tags = new[] { "в-зону" } });
        IsError(allow).Should().BeFalse(TextOf(allow));

        // tags_remove: снятие меток — та же зона, снимается только у задачи проекта зоны
        var denyRemoveForeign = await CallToolAsync(client, sessionId, "tags_remove", new
        { entityType = "task", entityId = outTaskId, tags = new[] { "не-туда" } });
        IsError(denyRemoveForeign).Should().BeTrue("задача чужого проекта — вне зоны");
        TextOf(denyRemoveForeign).Should().Contain("вне разрешённой зоны");

        var removeInZone = await CallToolAsync(client, sessionId, "tags_remove", new
        { entityType = "task", entityId = inTaskId, tags = new[] { "в-зону" } });
        IsError(removeInZone).Should().BeFalse(TextOf(removeInZone));
    }

    // ---------- tags_remove: снятие тегов (зеркало tags_apply) ----------

    /// <summary>
    /// Roundtrip снятия на сессии: apply навешивает [alpha, Готово], remove снимает —
    /// сравнение без учёта регистра («готово» снимает «Готово»), остальные теги целы.
    /// removed — реально снятые имена в написании сущности; повторное снятие — не ошибка
    /// с пустым removed (идемпотентность).
    /// </summary>
    [Fact]
    public async Task TagsRemove_Сессия_СнимаетПеречисленноеБезУчётаРегистра()
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-tags-rm");
        var caller = await CreateSessionAsync(client, projectId);
        var target = await CreateSessionAsync(client, projectId);

        var applied = await CallToolAsync(client, caller, "tags_apply", new
        { entityType = "session", entityId = target, projectId, tags = new[] { "alpha", "Готово" } });
        IsError(applied).Should().BeFalse(TextOf(applied));

        var removed = await CallToolAsync(client, caller, "tags_remove", new
        { entityType = "session", entityId = target, projectId, tags = new[] { "готово" } });
        IsError(removed).Should().BeFalse(TextOf(removed));
        var body = JsonSerializer.Deserialize<JsonElement>(TextOf(removed));
        body.GetProperty("removed").EnumerateArray().Select(t => t.GetString())
            .Should().Equal(["Готово"], "снятое — в каноническом написании сущности");
        body.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
            .Should().Equal(["alpha"], "неперечисленные теги не трогаются");

        // Повторное снятие того же тега — идемпотентность: не ошибка, removed пуст
        var again = await CallToolAsync(client, caller, "tags_remove", new
        { entityType = "session", entityId = target, projectId, tags = new[] { "готово" } });
        IsError(again).Should().BeFalse(TextOf(again));
        var againBody = JsonSerializer.Deserialize<JsonElement>(TextOf(again));
        againBody.GetProperty("removed").EnumerateArray()
            .Should().BeEmpty("снимать уже нечего — но это не ошибка");
        againBody.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
            .Should().Equal(["alpha"]);
    }

    /// <summary>
    /// session-ветка tags_remove: чужой и несуществующий projectId — отказ, как у всех
    /// инструментов с проектным маршрутом (владение и зона проверяются на каждый вызов).
    /// </summary>
    [Fact]
    public async Task TagsRemove_Сессия_ЧужойИлиНесуществующийПроект_Отказ()
    {
        var clientA = Client;
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var projectA = await CreateProjectAsync(clientA, "wsp-rm-a");
        var caller = await CreateSessionAsync(clientA, projectA);
        var projectB = await CreateProjectAsync(clientB, "wsp-rm-b");

        var denyForeign = await CallToolAsync(clientA, caller, "tags_remove", new
        { entityType = "session", entityId = "whatever", projectId = projectB, tags = new[] { "x" } });
        IsError(denyForeign).Should().BeTrue("проект владельца B недоступен токену владельца A");
        TextOf(denyForeign).Should().Contain("не найден или недоступен");

        var denyMissing = await CallToolAsync(clientA, caller, "tags_remove", new
        { entityType = "session", entityId = "whatever", projectId = "proj-missing", tags = new[] { "x" } });
        IsError(denyMissing).Should().BeTrue("несуществующий проект — тот же отказ");
        TextOf(denyMissing).Should().Contain("не найден или недоступен");
    }

    /// <summary>
    /// Зеркало «Блокера 4» для снятия: tags_remove у задачи шлёт task_changed(updated) в
    /// группу владельца — интерфейс не должен жить с устаревшими метками после снятия.
    /// Метку навешиваем до подключения, чтобы событие apply не попало в подписку.
    /// </summary>
    [Fact]
    public async Task TagsRemove_Задача_ШлётTaskUpdated()
    {
        var client = Client;
        var projectId = await CreateProjectAsync(client, "wsp-tags-rm-task");
        var sessionId = await CreateSessionAsync(client, projectId);

        var task = await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача со снятием метки" });
        task.EnsureSuccessStatusCode();
        var taskId = JsonSerializer.Deserialize<JsonElement>(
            await task.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var applied = await CallToolAsync(client, sessionId, "tags_apply", new
        { entityType = "task", entityId = taskId, tags = new[] { "снять-меня" } });
        IsError(applied).Should().BeFalse(TextOf(applied));

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(_factory.GetToken(
                    TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
            })
            .Build();
        var updated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("message", msg =>
        {
            if (msg.TryGetProperty("type", out var type) && type.GetString() == "task_changed"
                && msg.GetProperty("action").GetString() == "updated"
                && msg.GetProperty("task").GetProperty("id").GetString() == taskId
                // Только событие снятия: labels уже без метки (события apply отфильтруются)
                && !msg.GetProperty("task").GetProperty("labels").EnumerateArray()
                    .Any(l => l.GetString() == "снять-меня"))
                updated.TrySetResult(msg);
        });
        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync("JoinUser", me.GetProperty("userId").GetString());

            var removed = await CallToolAsync(client, sessionId, "tags_remove", new
            {
                entityType = "task",
                entityId = taskId,
                tags = new[] { "Снять-Меня" },
            });
            IsError(removed).Should().BeFalse(TextOf(removed));
            JsonSerializer.Deserialize<JsonElement>(TextOf(removed))
                .GetProperty("removed").EnumerateArray().Select(t => t.GetString())
                .Should().Equal(["снять-меня"], "снятие без учёта регистра — в написании сущности");

            var received = await Task.WhenAny(updated.Task, Task.Delay(15_000));
            received.Should().Be(updated.Task, "task_changed(updated) обязан прийти в группу владельца");
            updated.Task.Result.GetProperty("task").GetProperty("labels").EnumerateArray()
                .Should().BeEmpty("в событии — уже обновлённая задача без метки");
        }
        finally { await connection.DisposeAsync(); }
    }
}
