using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Tests.Controllers;

// B3: GET /api/wiki/page отдаёт links[] — каждую [[…]] текста страницы с путём цели (резолв —
// только WikiGate.ResolveLink, фронт не заводит третью копию правил) и поле section у пунктов
// «Связанных». Контракт проверяется по web-JSON ответа (имена links/text/path/section), не по
// типам: это контракт соседа-фронта. Фикстура: цель [[A]] и [[A|форма]] — сосед по разделу,
// [[Вбок]] — страница бокового проекта: её резолв — Sideways, не право на ссылку (ADR-036,
// решение 4), path=null; сноска [^s1] с целью [[Карточка ИИ]] — её цель ВХОДИТ в links[]:
// сноска-ссылка — тоже ссылка, «Связанные» учитывают её так же (согласие с WikiRelated), а
// порядок массива детерминирован: сначала ссылки тела, затем цели сносок (OutboundLinks,
// WikiPage.cs:272-282). Мутация c2 («path по имени файла, без ResolveLink») нашла бы Вбок
// по заголовку и дала бы путь — тест красный. Страница с боковой ссылкой пишется на диск
// напрямую: такой записи гейт не позволяет, но уже написанную страницу GET отвечает как есть.
// Контроллер строится напрямую (чтение, не транспорт) — фабрики хоста нет, метки Host нет.
public class WikiControllerPageLinksTests : IDisposable
{
    private const string User = "u1";
    private const string AiCard = "Карточка ИИ";

    private readonly string _dir;
    private readonly string _pages;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly WikiKeeper _keeper;
    private readonly WikiJournalReader _journal;
    private readonly string _key;
    private readonly string _pageKey;
    private readonly string _sideKey;
    private readonly string _sideSectionKey;

    public WikiControllerPageLinksTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_page_links_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _pages = WikiPaths.Pages(_dir, User);
        var notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new Flags(true));
        _keeper = new WikiKeeper(notes, _config);
        // GET page читает журнал (overrides): имена источников там не нужны — каталог сессий не зовётся.
        _journal = new WikiJournalReader(notes, null!, _config);

        var root = Path.Combine(_dir, "repo", User, "Проект");
        Directory.CreateDirectory(root);
        var project = _projects.Create("Проект", root, User, User);
        _key = "projects/" + project.Id;
        _pageKey = $"Проекты/{project.Id}/Знание.md";
        // Боковой проект того же владельца: его страница — «вбок» (не в цепочке вверх), резолв
        // голой ссылки в неё — Sideways. Путь — через папку-имя раздела, как его держит диск.
        var sideRoot = Path.Combine(_dir, "repo", User, "Соседний");
        Directory.CreateDirectory(sideRoot);
        var side = _projects.Create("Соседний", sideRoot, User, User);
        _sideSectionKey = "projects/" + side.Id;
        _sideKey = $"Проекты/{side.Id}/Вбок.md";

        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── c1: links — порядок «тело, затем сноски», текст с «|», путь только у разрешённых ──

    [Fact]
    public async Task Page_Links_ResolvedAndNot()
    {
        var links = Json(PageResult()).GetProperty("links");
        // Три [[…]] тела + цель сноски = четыре записи; порядок детерминирован: сначала тело,
        // затем сноски (OutboundLinks, WikiPage.cs:272-282).
        links.GetArrayLength().Should().Be(4, "три [[…]] тела и цель сноски — четыре записи, порядка текста это касается");

        links[0].GetProperty("text").GetString().Should().Be("A");
        links[0].GetProperty("path").GetString().Should().Be(_pageKey.Replace("Знание.md", "A.md"));

        // Текст — внутренность скобок как написана, вместе с «|подписью»: фронт сопоставляет
        // по ней; своей обрезки «|» здесь нет (цель для резолва режет WikiPage.LinkTarget).
        links[1].GetProperty("text").GetString().Should().Be("A|форма");
        links[1].GetProperty("path").GetString().Should().Be(_pageKey.Replace("Знание.md", "A.md"),
            "подпись не меняет цель: та же страница");

        // [[Вбок]] — страница бокового проекта: вне раздела и цепочки вверх, резолв — Sideways,
        // не право на ссылку. Мутация «path по имени файла, без ResolveLink» нашла бы её и
        // дала бы путь — эта строка краснеет.
        links[2].GetProperty("text").GetString().Should().Be("Вбок");
        links[2].GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);

        // Цель сноски [^s1] — тоже ссылка: идёт после ссылок тела и резолвится общими
        // правилами — путь карточки. «Связанные» учитывают её так же (согласие с WikiRelated).
        // Мутация «отбросить цели сносок в links[]» — тест краснеет на длине, а без пути
        // цели — на этой строке.
        links[3].GetProperty("text").GetString().Should().Be(AiCard);
        links[3].GetProperty("path").GetString().Should().Be(_pageKey.Replace("Знание.md", $"Источники/{AiCard}.md"),
            "цель сноски резолвится общими правилами: путь карточки");
    }

    // ─── c2: section у каждого пункта related — ключ раздела его pageKey ──────────

    [Fact]
    public async Task Page_RelatedItems_Section_КлючРазделаPageKey()
    {
        var body = Json(PageResult());
        var items = body.GetProperty("related").EnumerateArray()
            .SelectMany(g => g.GetProperty("items").EnumerateArray()).ToList();
        items.Should().NotBeEmpty("без пункта проверка была бы вакуумной");
        foreach (var item in items)
        {
            var pageKey = item.GetProperty("pageKey").GetString()!;
            item.GetProperty("section").GetString()
                .Should().Be(WikiSections.Parse(pageKey), "section — ключ раздела цели, не её папка");
        }
    }

    // ─── Помощники ────────────────────────────────────────────────────────────────

    private IActionResult PageResult() =>
        new WikiController(_keeper, _projects, new NoSpheres(), null!, _journal, new Flags(true), _config, null!, null!, null!, null!)
        {
            ControllerContext = Ctx(),
        }.Page(_key, _pageKey);

    private async Task SeedAsync()
    {
        await _keeper.EnsureInitializedAsync(User);

        var cardKey = _pageKey.Replace("Знание.md", $"Источники/{AiCard}.md");
        var card = $"---\ntype: source\norigin: note\nref: note:n1\nhash: abc\ncaptured: 2026-11-01T10:00:00.0000000+00:00\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# {AiCard}\n";
        (await _keeper.WritePageAsync(User, _key, cardKey, card)).Status.Should().Be(WikiOpStatus.Applied);

        var target = "---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# A\n\n## Сводка\nСтраница-цель ссылок.\n\n## Хронология\n\n## Противоречия\n\n## История\n";
        (await _keeper.WritePageAsync(User, _key, _pageKey.Replace("Знание.md", "A.md"), target)).Status.Should().Be(WikiOpStatus.Applied);

        var side = "---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Вбок\n\n## Сводка\nБоковая страница.\n\n## Хронология\n\n## Противоречия\n\n## История\n";
        (await _keeper.WritePageAsync(User, _sideSectionKey, _sideKey, side)).Status.Should().Be(WikiOpStatus.Applied);

        // Сама страница — на диск напрямую: [[Вбок]] — боковая ссылка, такой записи гейт
        // отказывает, но уже написанную страницу GET отвечает как есть (200, links с null).
        var page = "---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Знание\n\n## Сводка\nСервер работает на порту пять тысяч. [^s1]\nСм. [[A]]. См. ещё [[A|форма]]. И боковое [[Вбок]].\n\n## Хронология\n\n## Противоречия\n\n## История\n\n[^s1]: [[Карточка ИИ]] — «Сервер слушает порт пять тысяч в конфиге»\n";
        File.WriteAllText(Path.Combine(_pages, _pageKey.Replace('/', Path.DirectorySeparatorChar)), page);
    }

    private static JsonElement Json(IActionResult result)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Which;
        obj.StatusCode.Should().Be(200, JsonSerializer.Serialize(obj.Value));
        return JsonDocument.Parse(JsonSerializer.Serialize(obj.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.Clone();
    }

    private static ControllerContext Ctx()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", User)], "test"));
        return new ControllerContext { HttpContext = http };
    }

    // Один флаг: llm-wiki; остальные ключи выключены.
    private sealed class Flags(bool llmWiki) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki && llmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }
}
