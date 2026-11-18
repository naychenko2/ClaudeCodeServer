using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Controllers;

// Драйвер-заглушки хоста: генерация картинок всегда работает. Ответа без request_id —
// ручки опроса нет, догоняющий опрос цены не начинается и обещания уточнения нет:
// в учёте остаётся ровно одна запись на каждый вызов.
internal sealed class AlwaysImagesGenerator : IImageGenerator
{
    public string Key => "glif";
    public string DisplayName => "Тестовый генератор";
    // Выключаемой: тесту догоняющей очереди нужен прогон «генератора нет» (заявка) и
    // прогон «генератор появился» (сама догоняющая генерация) на одном хосте
    public bool Enabled { get; set; } = true;
    // Пустой ответ при включённом драйвере: ветка «провайдер не отдал картинок» в
    // GenerateAvatarAsync на том же хосте (п.4 хвоста R-S-SPEND-2)
    public bool ReturnImages { get; set; } = true;
    public IReadOnlyList<ImageModelInfo> Models => [];

    public Task<ImageGenerationResult> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default) =>
        Task.FromResult(ReturnImages
            ? new ImageGenerationResult(
                Enumerable.Range(1, count).Select(_ => new GeneratedImage([1, 2, 3], "image/png")).ToList(), null)
            : ImageGenerationResult.Empty);
}

// Черновик персоны для страховки «Применить итоги разговора» — тот же контракт, что в
// OnboardingApplyTranscriptTests; прочие действия остаются «ничего не подошло» ([]),
// как в стандартном стабе фабрики
internal sealed class DraftForQuickCreateRunner : ICheapTextRunner
{
    private static readonly string DraftJson = JsonSerializer.Serialize(new
    {
        role = "Личный тренер",
        name = "Марина",
        description = "Помощник и тренер",
        @character = "Ты обаятельный помощник.",
        tone = "тепло и на равных",
        mustDo = new[] { "Отвечай по делу" },
        mustNot = new[] { "Не воды" },
        outputFormat = "Коротко",
        speechExamples = new[] { "Привет!" },
        greeting = "Здравствуйте!",
        color = "blue",
        avatarPrompt = "friendly woman 30s",
    });

    private static string AnswerFor(string actionKey) =>
        actionKey == LocalActionCatalog.PersonaQuickCreate ? DraftJson : "[]";

    public bool UsesLocal(string actionKey) => false;
    public bool HasFreeRoute(string actionKey) => false;
    public bool HasRoute(string actionKey) => true;
    public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
    public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        => Task.FromResult(AnswerFor(actionKey));
    public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
        object? jsonFormat = null, CancellationToken ct = default)
        => Task.FromResult<string?>(AnswerFor(actionKey));
    public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
        string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
        int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class CapturingSpend : ISpendCollector
{
    private readonly List<SpendRecord> _records = [];
    public IReadOnlyList<SpendRecord> Records { get { lock (_records) return [.. _records]; } }
    public void Record(SpendRecord record) { lock (_records) _records.Add(record); }
}

// Хост, где генерация картинок работает, а весь учёт расхода пишется в наблюдаемый
// коллектор: видно, на кого и с каким инициатором роутер записал каждую генерацию
public class AvatarSpendFactory : TestWebApplicationFactory
{
    // Наблюдаемый коллектор доступен тестам этого же сборочника (internal)
    internal CapturingSpend Spend { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IImageGenerator>();
            services.AddSingleton<IImageGenerator, AlwaysImagesGenerator>();
            services.RemoveAll<ISpendCollector>();
            services.AddSingleton<ISpendCollector>(Spend);
            services.RemoveAll<ICheapTextRunner>();
            services.AddSingleton<ICheapTextRunner, DraftForQuickCreateRunner>();
        });
    }
}

// Учёт трат генерации аватаров по точкам входа (S-SPEND-2c, находка ревью R-S-SPEND-2):
// инициатор записи совпадает с инициатором вызова — та же операция из UI даёт Human,
// из чата через MCP-тулсет — Agent; и каждая успешная генерация видна в учёте владельца.
[Trait("Category", "Host")]
public class AvatarSpendPathsTests : IClassFixture<AvatarSpendFactory>
{
    private readonly AvatarSpendFactory _factory;
    private readonly HttpClient _client;

    public AvatarSpendPathsTests(AvatarSpendFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();

    // Записи генерации картинок — по ним видно адресата и инициатора траты. Опознаём по
    // числу вариантов: у догоняющей записи и чатовых LLM-записей оно 0, а единственный
    // генерирующий путь этого теста — аватарный. Источник провайдера — не признак: он
    // равен ключу заглушки этого хоста и не относится к проверяемым путям. Текст подписи
    // для этого тоже не признак: он меняется вместе с продуктовым текстом.
    private List<SpendRecord> ImageRecords() =>
        _factory.Spend.Records.Where(r => r.Generations > 0).ToList();

    private Persona NewPersona() =>
        Personas.Create(OwnerId, "А" + Guid.NewGuid().ToString("N")[..5], null, null, null, null, null,
            PersonaScope.Global, null, null, null, false);

    // Сессия-вызыватель — как в PersonasToolsetZoneTests: проект + его сессия, вызов от лица
    // этого чата (X-Caller-Session-Id) даёт широкую рамку хода
    private async Task<string> SessionAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"asp-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CallToolsetAsync(string sessionId, string tool, object args)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/personas/{sessionId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Caller-Session-Id", sessionId);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        isError.Should().BeFalse(result.GetProperty("content")[0].GetProperty("text").GetString());
        return result;
    }

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync())!;

    private async Task EnsureHomeConfiguredAsync()
    {
        var homes = Path.Combine(_factory.TempDir, "homes");
        Directory.CreateDirectory(homes);
        (await _client.PutAsJsonAsync("/api/settings", new { defaultProjectsPath = homes }))
            .EnsureSuccessStatusCode();
    }

    // Та же операция из UI: человек просит варианты аватара — трата на него и с ним же
    [Fact]
    public async Task АватарИзUI_ИнициаторЧеловека()
    {
        var persona = NewPersona();
        var before = ImageRecords().Count;

        var response = await _client.PostAsJsonAsync($"/api/personas/{persona.Id}/avatar/generate",
            new { prompt = "рыжий кот", count = 1 });
        response.EnsureSuccessStatusCode();

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Human, "вызов пришёл из формы (REST)");
    }

    // Тот же аватар из чата (personas_generate_avatar): трата того же владельца, но
    // инициатор — агент. Актор — актор хода: ход здесь самого владельца (GetTurnActor не
    // называет другого) — права арендатора, он и есть актор (п.2 хвоста R-S-SPEND-2)
    [Fact]
    public async Task АватарИзЧата_ИнициаторАгента()
    {
        var persona = NewPersona();
        var sessionId = await SessionAsync();
        var before = ImageRecords().Count;

        await CallToolsetAsync(sessionId, "personas_generate_avatar", new { id = persona.Id, prompt = "рыжий кот" });

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Agent,
            "personas_generate_avatar из чата — агентский вызов");
        added[0].ActorUserId.Should().Be(OwnerId,
            "актор неизвестен (ход владельца) — права арендатора, а не чужое значение");
    }

    // П.2 хвоста R-S-SPEND-2: в MCP-пути актором обязан называться актор хода, а не владелец
    // песочницы. Сегодня путь с чужим актором ненаблюдаем end-to-end: участник не доходит до
    // аватара проектной персоны (ValidProject требует владельца проекта, узкая рамка закрывает
    // personas_generate_avatar участнику). Поэтому проба — сам выбор значения: названный
    // вызывающим актор доходит до записи, ключом хранилища остаётся арендатор
    [Fact]
    public async Task АватарИзЧата_АкторХодаДостигаетЗаписи()
    {
        var persona = NewPersona();
        var before = ImageRecords().Count;

        var crud = _factory.Services.GetRequiredService<PersonasCrudService>();
        var result = await crud.GenerateAvatarAsync(OwnerId, persona.Id,
            new GenerateAvatarRequest("рыжий кот", 1), SpendInitiators.Agent, "actor-participant");
        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>("генерация удалась");

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId, "ключ записи — арендатор, а не исполнивший");
        added[0].ActorUserId.Should().Be("actor-participant",
            "актора называет вызывающий (актор хода), а не токен песочницы");
        added[0].Initiator.Should().Be(SpendInitiators.Agent);
    }

    // Проектная персона через REST (тесты веток Enqueue): проект назван в заявке — значит,
    // и заявка обязана назвать его, как успешная генерация
    private async Task<(string ProjectId, string PersonaId)> ProjectPersonaAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"asq-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var created = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Заявка" + Guid.NewGuid().ToString("N")[..4], scope = "project", projectId });
        created.EnsureSuccessStatusCode();
        return (projectId, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    private ImageBackfillRequest? QueuedRequest(string personaId) =>
        _factory.Services.GetRequiredService<ImageBackfillStore>()
            .Find(ImageBackfillKinds.PersonaAvatar, personaId);

    // П.4 хвоста R-S-SPEND-2: ветка «генератора нет» в GenerateAvatarAsync. Заявка обязана
    // нести ту же атрибуцию, что успешная генерация этого же вызова: актора, названного
    // вызывающим, и проект персоны. Проверяются значения, а не «факт вызова»: если ветка
    // подставит обратившегося вместо актора или снимет проект — этот тест красный
    [Fact]
    public async Task ЗаявкаБезГенератора_НесётАктораИПроектВызывающего()
    {
        var (projectId, personaId) = await ProjectPersonaAsync();
        var generator = (AlwaysImagesGenerator)_factory.Services.GetRequiredService<IImageGenerator>();
        generator.Enabled = false;
        try
        {
            var result = await _factory.Services.GetRequiredService<PersonasCrudService>()
                .GenerateAvatarAsync(OwnerId, personaId, new GenerateAvatarRequest("рыжий кот", 1),
                    SpendInitiators.Agent, "actor-participant");
            result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(
                "генератора нет — отказ, но аватар не теряется: заявка догонит");
        }
        finally { generator.Enabled = true; }

        try
        {
            var queued = QueuedRequest(personaId);
            queued.Should().NotBeNull("отказ с canBackfill ставит заявку в очередь");
            queued!.OwnerId.Should().Be(OwnerId, "ключ хранилища — арендатор");
            queued.ActorUserId.Should().Be("actor-participant",
                "актор заявки — тот же, что назван вызывающим для успешной генерации");
            queued.ProjectId.Should().Be(projectId, "проект заявки называется, как у успешной генерации");
            queued.Initiator.Should().Be(SpendInitiators.Agent);
        }
        finally
        {
            // Заявка этого теста не должна догоняться чужим прогоном: очередь — общий стор
            _factory.Services.GetRequiredService<ImageBackfillStore>()
                .Remove(ImageBackfillKinds.PersonaAvatar, personaId);
        }
    }

    // П.4 хвоста R-S-SPEND-2: вторая ветка Enqueue — «провайдер не отдал картинок». Та же
    // атрибуция в заявке, та же проверка значений
    [Fact]
    public async Task ЗаявкаБезКартинокПровайдера_НесётАктораИПроектВызывающего()
    {
        var (projectId, personaId) = await ProjectPersonaAsync();
        var generator = (AlwaysImagesGenerator)_factory.Services.GetRequiredService<IImageGenerator>();
        generator.ReturnImages = false;
        try
        {
            var result = await _factory.Services.GetRequiredService<PersonasCrudService>()
                .GenerateAvatarAsync(OwnerId, personaId, new GenerateAvatarRequest("рыжий кот", 1),
                    SpendInitiators.Agent, "actor-participant");
            result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Which.StatusCode.Should().Be(502,
                "провайдер не отдал картинок — отказ, но аватар не теряется: заявка догонит");
        }
        finally { generator.ReturnImages = true; }

        try
        {
            var queued = QueuedRequest(personaId);
            queued.Should().NotBeNull("отказ с canBackfill ставит заявку в очередь");
            queued!.OwnerId.Should().Be(OwnerId, "ключ хранилища — арендатор");
            queued.ActorUserId.Should().Be("actor-participant",
                "актор заявки — тот же, что назван вызывающим для успешной генерации");
            queued.ProjectId.Should().Be(projectId, "проект заявки называется, как у успешной генерации");
            queued.Initiator.Should().Be(SpendInitiators.Agent);
        }
        finally
        {
            // Заявка этого теста не должна догоняться чужим прогоном: очередь — общий стор
            _factory.Services.GetRequiredService<ImageBackfillStore>()
                .Remove(ImageBackfillKinds.PersonaAvatar, personaId);
        }
    }

    // П.1 хвоста R-S-SPEND-2: аватар проектной персоны догенерился фоном — догоняющая запись
    // обязана нести ту же атрибуцию, что интерактивная: проект и актора (раньше — пусто, и та же
    // персона при удачной генерации в разрез по проекту попадала, при догоняющей — нет)
    [Fact]
    public async Task ДогоняющаяГенерацияПроектнойПерсоны_НесётПроектИАктора()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"asb-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var generator = (AlwaysImagesGenerator)_factory.Services.GetRequiredService<IImageGenerator>();
        var backfill = _factory.Services.GetRequiredService<ImageBackfillService>();
        generator.Enabled = false;
        string personaId;
        try
        {
            // Генератора нет — аватар уходит в очередь; атрибуция считается при постановке
            // заявки (PersonasCrudService), пока персона известна вызывающему
            var created = await _client.PostAsJsonAsync("/api/personas",
                new { name = "Догоняющая" + Guid.NewGuid().ToString("N")[..4], scope = "project", projectId, autoAvatar = true });
            created.EnsureSuccessStatusCode();
            personaId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        finally
        {
            generator.Enabled = true;
        }

        var before = ImageRecords().Count;
        var summary = await backfill.RunAsync(OwnerId);
        summary.Generated.Should().Be(1, "провайдер появился — заявка догнала аватар");

        var saved = Personas.GetByIdInternal(personaId)!;
        saved.Avatar.Kind.Should().Be(PersonaAvatarKind.Image, "заявка догнала аватар");

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId, "ключ записи — владелец заявки");
        added[0].ProjectId.Should().Be(projectId,
            "догоняющая запись проектной персоны видна в разрезе по проекту, как интерактивная");
        added[0].ActorUserId.Should().Be(OwnerId,
            "актор сохранён вместе с заявкой — та же атрибуция, что у интерактивной генерации");
        added[0].Initiator.Should().Be(SpendInitiators.Human, "инициатор заявки — человек из формы создания");
    }

    // Хвост S-SPEND-TAIL-ATTR-2: заявка очереди несёт ИМЕНОВАННОГО чужого актора, а не фолбэк
    // на обратившегося. Прежняя проба догоняющей ветки называла актора владельцем, совпадающим
    // с фолбэком, и снятие аргумента в EnqueueAvatarBackfill её не краснило
    [Fact]
    public async Task ДогоняющаяГенерация_НесётИменованногоЧужогоАктора()
    {
        var persona = NewPersona();
        var crud = _factory.Services.GetRequiredService<PersonasCrudService>();
        var generator = (AlwaysImagesGenerator)_factory.Services.GetRequiredService<IImageGenerator>();
        var backfill = _factory.Services.GetRequiredService<ImageBackfillService>();
        generator.Enabled = false;
        try
        {
            // Генератора нет — аватар уходит в очередь, актор фиксируется при постановке
            await crud.TryAutoGenerateAvatarAsync(OwnerId, persona, null, SpendInitiators.Agent, "actor-participant");
        }
        finally
        {
            generator.Enabled = true;
        }

        var before = ImageRecords().Count;
        var summary = await backfill.RunAsync(OwnerId);
        summary.Generated.Should().Be(1, "провайдер появился — заявка догнала аватар");

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId, "ключ записи — владелец заявки");
        added[0].ActorUserId.Should().Be("actor-participant",
            "актор переживает постановку в очередь: заявка несёт названного, а не обратившегося");
        added[0].Initiator.Should().Be(SpendInitiators.Agent);
    }

    // Персона из чата (personas_create с AutoAvatar): аватар генерирует агент, и его
    // инициатор доходит до записи траты. Актор здесь — сам владелец: ход его собственный,
    // GetTurnActor другого не называет → права арендатора (тот же фолбэк, что у
    // personas_generate_avatar выше)
    [Fact]
    public async Task ПерсонаИзЧата_АвтоАватарСИнициаторомАгента()
    {
        var sessionId = await SessionAsync();
        var before = ImageRecords().Count;

        await CallToolsetAsync(sessionId, "personas_create",
            new { name = "ИзЧата" + Guid.NewGuid().ToString("N")[..4], scope = "global" });

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle("AutoAvatar у персона из чата всегда true — генерация была");
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Agent);
        added[0].ActorUserId.Should().Be(OwnerId,
            "актор хода неизвестен (ход самого владельца) — актором становится арендатор");
    }

    // П.2 хвоста R-S-SPEND-2 (хвост-2): в MCP-пути создания персоны актором обязан называться
    // актор хода, а не владелец песочницы. Путь с чужим актором сегодня ненаблюдаем end-to-end:
    // узкая рамка закрывает personas_create участнику (PersonasToolset.cs:116). Поэтому проба —
    // сам выбор значения: названный вызывающим актор доходит до записи через CreateAsync →
    // TryAutoGenerateAvatarAsync → AvatarSpendAttribution; ключом хранилища остаётся арендатор
    [Fact]
    public async Task ПерсонаИзЧата_АвтоАватар_АкторХодаДостигаетЗаписи()
    {
        var before = ImageRecords().Count;

        var crud = _factory.Services.GetRequiredService<PersonasCrudService>();
        var result = await crud.CreateAsync(OwnerId,
            new CreatePersonaRequest("АкторХода" + Guid.NewGuid().ToString("N")[..4], null, null, null, null,
                null, PersonaScope.Global, null, null, null, null, AutoAvatar: true),
            null, SpendInitiators.Agent, "actor-participant");
        // Успех — по обёртке: хелпер Ok() сервиса даёт OkObjectResult (PersonasCrudService.cs:54),
        // Value у ActionResult<Persona> при этом остаётся null
        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>("создание персоны удалось");

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle("autoAvatar=true — генерация аватара была");
        added[0].OwnerId.Should().Be(OwnerId, "ключ записи — арендатор, а не исполнивший");
        added[0].ActorUserId.Should().Be("actor-participant",
            "актора называет вызывающий (актор хода), а не токен песочницы");
        added[0].Initiator.Should().Be(SpendInitiators.Agent);
    }

    // Аватар персоны проекта — достояние проекта (п.3-4 ревью R-S-SPEND-2): трата пишется на
    // арендатора — владельца проекта — и видна в разрезе по проекту, а исполнивший назван
    // актором, даже когда он совпадает с владельцем. Ключом хранилища остаётся арендатор:
    // раньше ProjectId был null всегда, а ActorUserId не заполнялся вовсе.
    [Fact]
    public async Task ПроектнаяПерсона_ТратаАрендаторуИВиднаПроекту()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"asp-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var created = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Проектная" + Guid.NewGuid().ToString("N")[..4], scope = "project", projectId });
        created.EnsureSuccessStatusCode();
        var personaId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var before = ImageRecords().Count;
        var response = await _client.PostAsJsonAsync($"/api/personas/{personaId}/avatar/generate",
            new { prompt = "рыжий кот", count = 1 });
        response.EnsureSuccessStatusCode();

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId, "ключ записи — арендатор, владелец проекта");
        added[0].ProjectId.Should().Be(projectId, "трата аватара проектной персоны видна в разрезе по проекту");
        added[0].ActorUserId.Should().Be(OwnerId, "актора называет вызывающий, а не чтение «нет поля»");
    }

    // Страховка «Применить итоги разговора»: аватар заготовки генерирует сервер по действию
    // человека — трата обязана быть в учёте этого владельца (раньше её не было вовсе)
    [Fact]
    public async Task ИтогиЗнакомства_ТратаАватараНаВладельца()
    {
        await EnsureHomeConfiguredAsync();
        // Заготовку завёл стартовый проход провижна: DefaultPersonaId == AssistantPersonaId
        (await MeAsync()).GetProperty("defaultPersonaId").ValueKind.Should().Be(JsonValueKind.String);
        (await _client.PostAsync("/api/onboarding/user/start", null)).EnsureSuccessStatusCode();
        var before = ImageRecords().Count;

        var response = await _client.PostAsync("/api/onboarding/user/apply-transcript", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle("аватар онбординга оплачен инстансом — трата видна в учёте");
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Human, "итог знакомства применяет человек из UI");
    }
}

/// <summary>
/// Мягкий гейт прав актора: живой отказывает personas_generate_avatar любому, кто не
/// арендатор (OwnerOnly в ActorToolRights), поэтому вызов с чужим актором хода не доходит
/// до измеряемой строки тулсета. Гейт — не предмет теста; подменяется, чтобы тест
/// дотянулся до тулсета.
/// </summary>
internal sealed class AllowAllActorRightGate : IActorRightGate
{
    public string? Denied(string server, string tool, string? callerSessionId, string ownerId, string? projectId) => null;
}

/// <summary>
/// Хост с открытыми граничными швами (рамка — WideTurnFrame, права актора — AllowAll).
/// Оба — не предмет теста: живая рамка закрывает personas_generate_avatar в общем чате
/// всем, включая владельца (решение 29.10, TurnFrame.Resolve), а живой гейт прав — всем,
/// кто не арендатор (OwnerOnly). Поэтому end-to-end с чужим актором хода в сегодняшней
/// конфигурации невозможен в принципе, а строка тулсета defensive: она обязана звать
/// актора из реестра на случай, когда право откроют. Швы подменяются, чтобы тест дотянулся
/// до строки; актор при этом настоящий — его ставит реальный SessionManager, ход запущен
/// участником.
/// </summary>
public class SharedChatAvatarSpendFactory : AvatarSpendFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITurnFrameSource>();
            services.AddSingleton<ITurnFrameSource>(new WideTurnFrame());
            services.RemoveAll<IActorRightGate>();
            services.AddSingleton<IActorRightGate>(new AllowAllActorRightGate());
        });
    }
}

/// <summary>
/// П.3 хвоста ревью R-S-SPEND-2: строка тулсета PersonasToolset.cs:481 обязана звать актора
/// хода (GetTurnActor), а не владельца песочницы. Раньше ни один тест на неё не краснел:
/// существующие били по CRUD-уровню, а в достижимых тогда рамках актор совпадал с арендатором
/// и подмена в строке была незаметна. Здесь: ход участника в общем чате (актор — реальный
/// реестр SessionManager) → вызов personas_generate_avatar токеном арендатора (как агент в
/// чате участника) → трата несёт актора хода. Граничные швы этой ветки (рамка, права актора)
/// в живом составе закрывают такой вызов — они открыты в SharedChatAvatarSpendFactory и НЕ
/// являются предметом теста; меряется ровно выбор значения в строке тулсета. Мутация
/// (подмена GetTurnActor(session.Id, ownerId) ?? ownerId на ownerId в PersonasToolset.cs:481)
/// краснит ровно этот тест.
/// </summary>
[Trait("Category", "Host")]
public class AvatarSpendTurnActorTests : IClassFixture<SharedChatAvatarSpendFactory>
{
    private readonly SharedChatAvatarSpendFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _member;

    public AvatarSpendTurnActorTests(SharedChatAvatarSpendFactory factory)
    {
        _factory = factory;
        // Два РАЗНЫХ замка: здесь — серверный рубильник (без него общие проекты отвечают 503),
        // ниже PUT /api/feature-flags — пользовательский флаг каждого из двух клиентов. Один другого не заменяет.
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _owner = factory.CreateAuthenticatedClient();
        _member = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();
    private string OwnerId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private string MemberId => Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private List<SpendRecord> ImageRecords() =>
        _factory.Spend.Records.Where(r => r.Generations > 0).ToList();

    // Тот же вызов тулсета, что в AvatarSpendPathsTests: токен — владельца-арендатора,
    // заголовок — чат-вызыватель (X-Caller-Session-Id)
    private async Task CallToolsetAsync(string sessionId, string tool, object args)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/personas/{sessionId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Caller-Session-Id", sessionId);
        var resp = await _owner.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        isError.Should().BeFalse(result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task АватарИзЧатаУчастника_ТулсетЗовётАктораХодаАНеВладельца()
    {
        foreach (var c in new[] { _owner, _member })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
                .EnsureSuccessStatusCode();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var project = await _owner.PostAsJsonAsync("/api/projects", new { name = $"att-{tag}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        // чат до участников: в общем проекта без руководителя провижна владельца нет (W4-B1a 2, финал ed64271c)
        var session = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await _owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();

        // Страховка замера: ход действительно принадлежит участнику — без этого подмена
        // в строке тулсета осталась бы незаметной (тот же приём, что в TasksToolsetAssigneeTests)
        (await _member.PostAsJsonAsync($"/api/sessions/{sessionId}/messages",
            new { text = "сгенерируй аватар", wait = "none" })).EnsureSuccessStatusCode();
        Sessions.GetTurnActor(sessionId, OwnerId).Should().Be(MemberId,
            "актор хода — участник, а не владелец чата; иначе тест мерил бы совпадение");

        var persona = Personas.Create(OwnerId, "А" + tag, null, null, null, null, null,
            PersonaScope.Global, null, null, null, false);

        var before = ImageRecords().Count;
        await CallToolsetAsync(sessionId, "personas_generate_avatar",
            new { id = persona.Id, prompt = "рыжий кот" });

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId, "ключ хранилища — арендатор песочницы, не исполнивший");
        added[0].ActorUserId.Should().Be(MemberId,
            "тулсет называет актора хода (GetTurnActor), а не владельца песочницы (PersonasToolset.cs:481)");
        added[0].Initiator.Should().Be(SpendInitiators.Agent);
    }
}

/// <summary>
/// Т2c2 (путь И5 №12): аватар репетитора из админки платит админу, а не ребёнку — инвариант
/// «генерация аватара репетитора никогда не списывает бюджет ребёнка». Ручки —
/// POST /api/users/{id}/child-tutors/{personaId}/avatar/generate|select|upload: ключ персоны и
/// папки ассетов — ребёнок (владелец персоны), владелец расхода и актор — вызывающий админ.
/// Ребёнок и репетитор — тот же setup, что у ChildTutorsControllerTests.SetupChild (профиль
/// пишется прямо в сторе, персона-репетитор принадлежит ребёнку); хост — AvatarSpendFactory:
/// генератор работает, весь учёт виден в наблюдаемом коллекторе. Админ — дефолтный пользователь
/// фабрики (роль admin в сторе).
/// Фоновый догон для такой заявки не ставится вовсе: очередь ключуется и оплачивается по
/// OwnerId сущности (ImageBackfillService), а другого владельца расхода не называет —
/// отказ возвращается как есть (502 {code,message}), заявки в очереди нет.
/// </summary>
[Trait("Category", "Host")]
public class ChildTutorAvatarSpendPathsTests : IClassFixture<AvatarSpendFactory>
{
    private readonly AvatarSpendFactory _factory;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;

    public ChildTutorAvatarSpendPathsTests(AvatarSpendFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAuthenticatedClient();
        _users = factory.Services.GetRequiredService<UserStore>();
        _personas = factory.Services.GetRequiredService<PersonaManager>();
    }

    private string AdminId => _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private string ChildId => _users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private List<SpendRecord> ImageRecords() =>
        _factory.Spend.Records.Where(r => r.Generations > 0).ToList();

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    /// <summary>Ребёнок с валидным профилем: репетитор профиля — живая персона ребёнка (тот же
    /// приём, что у ChildTutorsControllerTests.SetupChild).</summary>
    private string SetupChildTutor()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var tutor = _personas.Create(ChildId, "Репетитор " + tag, role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: false);
        _users.GetById(ChildId)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = tutor.Id,
            ProviderKey = "key",
            Model = "sonnet",
            Subjects =
            [
                new ChildSubject
                {
                    Id = "math", Label = "Математика " + tag, Glyph = "М", Color = "info",
                    TutorPersonaId = tutor.Id, ProviderKey = "key", Model = "sonnet", Enabled = true,
                },
            ],
        };
        return tutor.Id;
    }

    // c1: генерация через админскую ручку — строка расхода адресуется админу (и актор — он же);
    // у ребёнка в этом прогоне нет ни одной строки расхода
    [Fact]
    public async Task ГенерацияАватараРепетитора_ТратаНаАдменаНеНаРебёнка()
    {
        var tutorId = SetupChildTutor();
        var before = ImageRecords().Count;

        var response = await _admin.PostAsJsonAsync(
            $"/api/users/{ChildId}/child-tutors/{tutorId}/avatar/generate", new { prompt = "учительница", count = 1 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle("генератор на этом хосте работает — генерация была");
        added[0].OwnerId.Should().Be(AdminId,
            "владелец расхода — вызвавший админ: бюджет ребёнка аватар репетитора не съедает (И5)");
        added[0].ActorUserId.Should().Be(AdminId, "актор — исполнивший вызов админ, а не владелец персоны");
        added[0].Initiator.Should().Be(SpendInitiators.Human, "вызов пришёл из админки (REST)");
        ImageRecords().Any(r => r.OwnerId == ChildId).Should().BeFalse("у ребёнка строк расхода нет");
    }

    // c1/c2: полный путь админки — генерация, затем выбор кандидата. Сам выбор строки расхода
    // не пишет; генерация этого пути — на адмена, у ребёнка — ноль строк
    [Fact]
    public async Task ВыборКандидатаРепетитора_ГенерацияПутиНаАдмена_selectБезРасхода()
    {
        var tutorId = SetupChildTutor();

        var beforeGen = ImageRecords().Count;
        var gen = await _admin.PostAsJsonAsync(
            $"/api/users/{ChildId}/child-tutors/{tutorId}/avatar/generate", new { prompt = "учительница", count = 2 });
        gen.StatusCode.Should().Be(HttpStatusCode.OK);
        var candidates = (await JsonAsync(gen)).GetProperty("candidates");
        candidates.GetArrayLength().Should().Be(2);

        var genAdded = ImageRecords().Skip(beforeGen).ToList();
        genAdded.Should().ContainSingle("один вызов генерации — одна строка");
        genAdded[0].OwnerId.Should().Be(AdminId, "генерацию пути репетитора платит адмен (И5)");

        var beforeSelect = ImageRecords().Count;
        var select = await _admin.PostAsJsonAsync(
            $"/api/users/{ChildId}/child-tutors/{tutorId}/avatar/select", new { file = candidates[0].GetString() });
        select.StatusCode.Should().Be(HttpStatusCode.OK);
        _personas.Get(tutorId, ChildId)!.Avatar.Kind.Should().Be(PersonaAvatarKind.Image,
            "кандидат стал аватаром репетитора");

        ImageRecords().Count.Should().Be(beforeSelect, "select — не генерация: новой строки расхода нет");
        ImageRecords().Any(r => r.OwnerId == ChildId).Should().BeFalse("бюджет этого пути ребёнка не тронул");
    }

    // Путь загрузки: человек принёс свою картинку — генерации нет, строки расхода нет
    [Fact]
    public async Task ЗагрузкаАватараРепетитора_СтрокиРасходаНет()
    {
        var tutorId = SetupChildTutor();
        var before = ImageRecords().Count;

        // Сигнатура PNG + хвост до требуемых 12 байт (ValidateImageAsync смотрит magic bytes)
        var png = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        var form = new MultipartFormDataContent();
        var original = new ByteArrayContent(png);
        original.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(original, "original", "original.png");
        var cropped = new ByteArrayContent(png);
        cropped.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(cropped, "cropped", "cropped.png");

        var response = await _admin.PostAsync($"/api/users/{ChildId}/child-tutors/{tutorId}/avatar/upload", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = _personas.Get(tutorId, ChildId)!;
        persona.Avatar.Kind.Should().Be(PersonaAvatarKind.Image);
        persona.Avatar.OriginalFile.Should().NotBeNullOrEmpty("оригинал сохранён для перекропа");

        ImageRecords().Count.Should().Be(before, "загрузка — не генерация: строку расхода не пишем");
    }

    // Фоновый догон ImageBackfillService для персон ребёнка: не идёт. Заявка очереди
    // ключуется и оплачивается по OwnerId сущности (ребёнка), а другого владельца расхода
    // очередь не называет — админская ручка заявки не ставит: отказ 502 {code,message},
    // заявки в очереди нет, ребёнка отказ не тронул даже попыткой
    [Fact]
    public async Task ДогонАватараРепетитора_НеИдётОтказБезЗаявки()
    {
        var tutorId = SetupChildTutor();
        var generator = (AlwaysImagesGenerator)_factory.Services.GetRequiredService<IImageGenerator>();
        generator.ReturnImages = false;
        try
        {
            var resp = await _admin.PostAsJsonAsync(
                $"/api/users/{ChildId}/child-tutors/{tutorId}/avatar/generate", new { prompt = "учительница", count = 1 });
            resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
                "провайдер не отдал картинок — отказ, но не за счёт ребёнка");
            (await JsonAsync(resp)).GetProperty("code").GetString().Should().Be("avatar_generation_failed");
        }
        finally { generator.ReturnImages = true; }

        _factory.Services.GetRequiredService<ImageBackfillStore>()
            .Find(ImageBackfillKinds.PersonaAvatar, tutorId).Should().BeNull(
                "заявка догона оплачивается по OwnerId владельца персоны (ребёнка) — для репетитора её не ставят (И5)");
        ImageRecords().Any(r => r.OwnerId == ChildId).Should().BeFalse("отказанный вызов ничего не сгенерировал");
    }

    // Допуск тот же, что у остальных ручек репетитора (И5): ребёнок — 403 (closed-периметр),
    // чужая персона — 404 tutor_not_found (существование чужой не раскрываем); отказанный
    // вызов ничего не сгенерировал
    // Проверка расхода — по снимку ДО вызовов и только по записям после него: CapturingSpend
    // один на весь класс (общий экземпляр фабрики по IClassFixture), строки соседних тестов
    // этого класса в общем списке есть всегда, и проверка по всему списку держала бы тест на
    // чужих записях: сегодня ни один тест класса не списывает на ребёнка, но любой новый
    // такой тест сделал бы этот красный без всякой связи с его вызовами
    // (тот же приём снимка before, что у тестов выше этого файла).
    [Fact]
    public async Task АватарРепетитора_Ребёнок403_ЧужаяПерсона404()
    {
        var tutorId = SetupChildTutor();
        var child = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var before = ImageRecords().Count;

        var asChild = await child.PostAsJsonAsync(
            $"/api/users/{ChildId}/child-tutors/{tutorId}/avatar/generate", new { prompt = "учительница" });
        asChild.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var foreign = await _admin.PostAsJsonAsync("/api/personas", new { name = "Чужая " + Guid.NewGuid().ToString("N")[..4] });
        var foreignId = (await JsonAsync(foreign)).GetProperty("id").GetString()!;
        var asForeign = await _admin.PostAsJsonAsync(
            $"/api/users/{ChildId}/child-tutors/{foreignId}/avatar/generate", new { prompt = "учительница" });
        asForeign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonAsync(asForeign)).GetProperty("code").GetString().Should().Be("tutor_not_found");

        ImageRecords().Skip(before).Should().BeEmpty("отказанный вызов не дошёл до генерации");
    }
}
