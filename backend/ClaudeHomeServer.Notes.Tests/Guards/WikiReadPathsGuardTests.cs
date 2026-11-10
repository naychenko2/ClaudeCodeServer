using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// R3: сторож полноты перечня путей чтения вики (ADR-039 §7; инвариант «каждое чтение
/// страницы вики агентом или человеком учитывается»). Сторож — не сам учёт (его делают
/// R2/R4/R6), а полнота перечня: новый REST GET или новый MCP-инструмент вертикали Notes
/// без записи в таблице — красный; запись таблицы без метода в коде — тоже красный
/// (таблица не гниёт).
///
/// Три проверки:
/// 1. REST — рефлексией по сборке ClaudeHomeServer.Notes: все типы-наследники ControllerBase,
///    все публичные методы с HttpGetAttribute; ключ — «<Контроллер>.<Метод>».
/// 2. MCP — имена NotesToolset.CoreTools / WikiTools / AnnotationTools.
/// 3. Плагин — операция notes.read NotesPluginCapability по странице вики даёт not_found:
///    граница IsAllowedSource пускает только personal и проект слота (NotesPluginCapability.cs:97-98).
///    Плагин вики не отдаёт — в учёт не идёт. Если граница когда-нибудь пустит wiki, этот тест
///    красный и путь надо завести в таблицу.
///
/// Классификация каждой записи прочитана по коду ветки. Записи «найдено R3» — точки, которые
/// отдают текст страницы вики и не названы в перечне ADR-039 §7 (notes_thread, notes_resolve,
/// NotesController.Resolve): код не тронут, решение — Александр.
///
/// Оговорка о имени: в этой сборке уже есть одноимённый сторож Services/Wiki/
/// WikiReadPathsGuardTests (namespace ClaudeHomeServer.Tests.Services, S6g-2) — он держит
/// видимость путей чтения (ADR-036), а не учёт. Это разные задачи, поэтому этот — в
/// namespace ClaudeHomeServer.Tests.Guards. Переименование одного из них — решение постановщика.
///
/// Тест без хоста: TestWebApplicationFactory не поднимается, меток Host/Slow нет —
/// сторож идёт в быстром наборе.
/// </summary>
public class WikiReadPathsGuardTests : IDisposable
{
    private enum PathKind { Rest, Mcp, Ref }

    private enum PathClass
    {
        /// <summary>Путь учитывается в reads.json; StepRef — шаг, который его закрывает.</summary>
        Counted,
        /// <summary>Путь назван в ADR-039 §7 как не учитываемый, или найден R3 как путь к тексту
        /// страницы вне перечня — решение за Александром.</summary>
        NotCounted,
        /// <summary>Точка не отдаёт текст страницы: список, дерево, статус, запись.</summary>
        NoPageText,
    }

    /// <summary>Одна запись перечня. Key: REST — «Контроллер.Метод», MCP — имя инструмента,
    /// Ref — путь вне ручки/инструмента (рефлексией не сверяется). Counted несёт StepRef
    /// закрывающего шага; остальные — причину.</summary>
    private sealed record ReadPath(string Key, PathKind Kind, PathClass Class, string? StepRef, string Reason);

    private static ReadPath Rest(string key, PathClass cls, string reason, string? step = null) =>
        new(key, PathKind.Rest, cls, step, reason);

    private static ReadPath Mcp(string key, PathClass cls, string reason, string? step = null) =>
        new(key, PathKind.Mcp, cls, step, reason);

    /// <summary>Полный перечень точек входа вертикали Notes (24 REST GET + 23 MCP-инструмента)
    /// и одна справочная строка recall. Класс каждой записи — прочитан по коду ветки;
    /// причина каждой NotCounted/NoPageText — в строке.</summary>
    private static readonly ReadPath[] Table =
    [
        // ─── NotesController (api/notes) ───
        Rest("NotesController.List", PathClass.NoPageText,
            "список суммариев: NoteSummary не несёт содержимого (Note.cs:23-31)"),
        Rest("NotesController.Sources", PathClass.NoPageText,
            "список источников для выбора «куда создать» (NoteSourceDto)"),
        Rest("NotesController.Graph", PathClass.NoPageText,
            "граф связей: узлы и рёбра без текста (Note.cs:80-95)"),
        Rest("NotesController.Resolve", PathClass.NotCounted,
            "найдено R3, решение — Александр: ResolveByName возвращает полный NoteDetail цели "
            + "(NotesService.cs:744,753), ручка отдаёт его с содержимым (NotesController.cs:113) — "
            + "текст видимой страницы; в перечне ADR-039 §7 нет"),
        Rest("NotesController.Attachment", PathClass.NoPageText,
            "файл вложения (картинка для <img>), не текст страницы"),
        Rest("NotesController.Caps", PathClass.NoPageText,
            "статус возможностей раздела (semantic — настроен ли Dify)"),
        Rest("NotesController.Annotations", PathClass.NoPageText,
            "список комментариев к документу; документ — файл личного vault/проекта, не страница"),
        Rest("NotesController.Replies", PathClass.NoPageText,
            "список ответов треда (NoteReplyDto.Excerpt); комментарии — не страницы"),
        Rest("NotesController.Semantic", PathClass.NotCounted,
            "выдача списка семантического поиска (сниппеты), та же причина, что у notes_search: "
            + "выдача списка не доказывает, что страницу прочли (ADR-039 §7)"),
        Rest("NotesController.Templates", PathClass.NoPageText,
            "список шаблонов личного vault (файлы templates/)"),
        Rest("NotesController.NoteTasks", PathClass.NoPageText,
            "список задач, связанных с заметкой"),
        Rest("NotesController.Folders", PathClass.NoPageText,
            "список физических папок владельца"),
        Rest("NotesController.Get", PathClass.Counted,
            "открытие человеком в «Заметках» (ADR-039 §7, таблица «Учитываемые пути»)", step: "R4"),
        Rest("NotesController.Backlinks", PathClass.NoPageText,
            "список обратных ссылок: NoteBacklinkDto.Snippet — строка-контекст источника "
            + "(Note.cs:47-52); текст читаемой страницы не отдаётся"),

        // ─── WikiController (api/wiki) ───
        Rest("WikiController.Tree", PathClass.NoPageText,
            "дерево разделов: имена, признаки, число страниц"),
        Rest("WikiController.Section", PathClass.NoPageText,
            "каталог страниц: поля шапки страницы (title/summary/type/status), не текст страницы"),
        Rest("WikiController.Page", PathClass.Counted,
            "открытие человеком во вкладке «Вики» (ADR-039 §7, таблица «Учитываемые пути»)", step: "R4"),
        Rest("WikiController.Health", PathClass.NoPageText,
            "отчёт последнего линта _health.md — служебный файл Хранителя (WikiKeeper.HealthFileName), "
            + "не страница"),
        Rest("WikiController.Journal", PathClass.NoPageText,
            "журнал операций: строки log.jsonl и пакеты, не текст страницы"),

        // ─── WikiChatsController (api/wiki/chats) ───
        Rest("WikiChatsController.List", PathClass.NoPageText,
            "список отбора чатов: зоны, баллы, причины"),
        Rest("WikiChatsController.AnalyzeStatus", PathClass.NoPageText,
            "статус процесса разбора (running / последний исход)"),

        // ─── WikiPackagesController (api/wiki/packages) ───
        Rest("WikiPackagesController.List", PathClass.NoPageText,
            "список пакетов: сводки (Dto), текст операций отдельной карточки здесь нет"),
        Rest("WikiPackagesController.Get", PathClass.NoPageText,
            "карточка пакета: Text операций — кандидат изменения (WikiPackageOperationDto), "
            + "не текст страницы"),

        // ─── WikiSettingsController (api/wiki/settings) ───
        Rest("WikiSettingsController.Get", PathClass.NoPageText,
            "настройки вики и замер классов (WikiSettingsDto)"),

        // ─── NotesToolset.CoreTools ───
        Mcp("notes_list", PathClass.NoPageText,
            "список brief-ов без содержимого (NotesToolset.Brief)"),
        Mcp("notes_search", PathClass.NotCounted,
            "выдача списка не доказывает, что страницу прочли (ADR-039 §7 «Не учитываются», назван явно)"),
        Mcp("notes_read", PathClass.Counted,
            "чтение страницы вики моделью (ADR-039 §7, таблица «Учитываемые пути»)", step: "R2"),
        Mcp("notes_create", PathClass.NoPageText, "запись"),
        Mcp("notes_update", PathClass.NoPageText, "запись"),
        Mcp("notes_move", PathClass.NoPageText, "запись"),
        Mcp("notes_semantic_search", PathClass.NotCounted,
            "выдача списка (сниппеты), та же причина, что у notes_search (ADR-039 §7); "
            + "полный текст модель читает через notes_read — там и учитывается"),

        // ─── NotesToolset.WikiTools ───
        Mcp("wiki_ingest", PathClass.NoPageText,
            "заявка в очередь разбора: ответ — исход заявки, не текст источника"),
        Mcp("wiki_add_fact", PathClass.NoPageText,
            "заявка в очередь разбора: ответ — исход заявки"),
        Mcp("wiki_query", PathClass.Counted,
            "чтение видимых страниц через сборку контекста (ADR-039 §7, таблица «Учитываемые пути»)", step: "R2"),
        Mcp("wiki_status", PathClass.NoPageText,
            "состояние пакетов: BriefPackage — id/раздел/статус/счётчики, без текста операций "
            + "(NotesToolset.cs:1192-1201)"),

        // ─── NotesToolset.AnnotationTools ───
        Mcp("notes_suggest_title", PathClass.NoPageText,
            "отдаёт сгенерированный заголовок, не текст страницы; содержимое читается внутри "
            + "NotesAiService — служебный читатель (вне шага, в отчёт)"),
        Mcp("notes_backlinks", PathClass.NoPageText,
            "список обратных ссылок со сниппетами источников; текст читаемой страницы не отдаётся "
            + "(запись кода «список обратных ссылок — тоже чтение страницы» про видимость ADR-036, "
            + "не про текст)"),
        Mcp("notes_graph", PathClass.NoPageText, "граф узлов/рёбер без текста"),
        Mcp("notes_delete", PathClass.NoPageText, "запись"),
        Mcp("notes_annotate", PathClass.NoPageText, "запись"),
        Mcp("notes_annotations", PathClass.NoPageText,
            "список комментариев; документ — файл личного vault/проекта, пути вики — не область документа"),
        Mcp("notes_reply", PathClass.NoPageText, "запись"),
        Mcp("notes_thread", PathClass.NotCounted,
            "найдено R3, решение — Александр: корень треда берётся GetDetail (NotesToolset.cs:374), "
            + "видимая страница-корень возвращается полным текстом через RedactWikiDetail "
            + "(NotesToolset.cs:382); в перечне ADR-039 §7 нет"),
        Mcp("notes_set_status", PathClass.NoPageText, "запись"),
        Mcp("notes_daily", PathClass.NoPageText,
            "дневниковая заметка — обычная заметка личного vault, не страница"),
        Mcp("notes_resolve", PathClass.NotCounted,
            "найдено R3, решение — Александр: видимая страница отдаётся целиком через RedactWikiDetail "
            + "(NotesToolset.cs:482), пул резолва глобален (NotesService.cs:744); в перечне ADR-039 §7 нет"),
        Mcp("notes_promote_task", PathClass.NoPageText,
            "результат — созданная задача из чекбокса, не текст страницы"),

        // ─── вне ручек и инструментов (справочно, рефлексией не сверяется) ───
        new("NotesRecallContributor", PathKind.Ref, PathClass.Counted, "R6",
            "recall в ходе в момент сборки подсказки (ADR-039 §7, таблица «Учитываемые пути»); "
            + "не ручка и не инструмент — справочная строка перечня"),
    ];

    // ─── 1. REST: каждая GET-ручка вертикали — в таблице; каждая строка таблицы — в коде ───

    [Fact]
    public void РестКаждаяГетРучкаВертикалиЗаписанаВТаблице()
    {
        var actual = RestGetKeys();
        var tableKeys = Table.Where(p => p.Kind == PathKind.Rest).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var actualKeys = actual.ToHashSet(StringComparer.Ordinal);

        // Новая ручка без записи: в сообщении — имя метода (критерий c2).
        actual.Where(k => !tableKeys.Contains(k)).ToList()
            .Should().BeEmpty(
                "новая REST-ручка чтения без записи в таблице: класс и причину заводит таблица (ADR-039 §7)");

        // Таблица не гниёт: запись без метода в коде — красная.
        tableKeys.Where(k => !actualKeys.Contains(k)).ToList()
            .Should().BeEmpty(
                "запись таблицы без GET-метода в коде: строку убирают вместе с ручкой, с вердиктом в дифе");
    }

    /// <summary>Все публичные методы с HttpGetAttribute во всех типах-наследниках ControllerBase
    /// сборки ClaudeHomeServer.Notes. Ключ — «<Контроллер>.<Метод>». Internal-методы
    /// (AnalyzeCore, AfterDecision) — не действия MVC, их здесь нет по построению.</summary>
    private static List<string> RestGetKeys()
    {
        var assembly = typeof(NotesController).Assembly; // форс-загружает сборку вертикали
        var keys = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type)) continue;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false).Length > 0)
                    keys.Add($"{type.Name}.{m.Name}");
            }
        }
        return keys;
    }

    // ─── 2. MCP: каждый инструмент тулсета — в таблице; каждая строка таблицы — в тулете ───

    [Fact]
    public void МцпКаждыйИнструментТулсетаЗаписанВТаблице()
    {
        var actual = NotesToolset.CoreTools.Concat(NotesToolset.WikiTools).Concat(NotesToolset.AnnotationTools)
            .Select(t => t.Name).ToList();
        var tableKeys = Table.Where(p => p.Kind == PathKind.Mcp).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var actualKeys = actual.ToHashSet(StringComparer.Ordinal);

        actual.Where(k => !tableKeys.Contains(k)).ToList()
            .Should().BeEmpty(
                "новый MCP-инструмент без записи в таблице: класс и причину заводит таблица (ADR-039 §7)");

        tableKeys.Where(k => !actualKeys.Contains(k)).ToList()
            .Should().BeEmpty(
                "запись таблицы без инструмента в тулсете: строку убирают вместе с инструментом, с вердиктом в дифе");
    }

    // ─── 3. Плагин: notes.read по странице вики — not_found (граница не пускает wiki) ───

    [Fact]
    public async Task ПлагинНотесРедПоСтраницеВикиОтдаётНетФаунд()
    {
        // Фикстура — по образцу NotesToolsetWikiTests: NotesService на временном каталоге,
        // вики заведена, страница под pages/. Плагин конструируется на том же NotesService.
        _notes = new NotesService(new NoProjects(), _config, NullLogger<NotesService>.Instance,
            flags: new WikiFlagOn());
        var keeper = new WikiKeeper(_notes, _config);
        await keeper.EnsureInitializedAsync(User);
        var w = await keeper.WritePageAsync(User, WikiSections.Common, "Общее/Сторож-факт.md", Page("Сторож-факт"));
        w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);

        var id = PageId("Общее/Сторож-факт.md");

        // Страница реальна и читается сервисом: not_found ниже — ровно из границы
        // IsAllowedSource, а не из отсутствия страницы (иначе тест был бы пустым).
        var detail = _notes.GetDetail(User, id);
        detail.Should().NotBeNull("фикстура обязана дать читаемую страницу вики");
        detail!.Source.Should().Be(NotesService.WikiKey);

        // Слот с проектом — чтобы отказ исходил именно из границы («personal | проект слота»),
        // а не из отсутствия слота: страница вики не является ни тем, ни другим.
        var result = await new NotesPluginCapability(_notes).HandleAsync(
            new PluginCallContext(User, "local-a/pkg", "p-slot", null), "notes.read",
            JsonDocument.Parse($"{{\"id\":\"{id}\"}}").RootElement,
            [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeFalse(
            "граница IsAllowedSource (NotesPluginCapability.cs:97-98) пускает только personal и проект слота; "
            + "если она заговорила про wiki — этот тест красный и путь notes.read по вики надо завести в таблицу");
        result.Error!.Code.Should().Be("not_found", "отказ не подтверждает существования страницы (тот же приём, что у чужого id)");
    }

    // ─── фикстура плагинного теста ───

    private const string User = "u1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "wiki_read_paths_accounting_" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private NotesService? _notes;

    public WikiReadPathsGuardTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string Page(string title) => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}

        ## Сводка
        Страница для сторожа полноты перечня.

        ## Хронология

        ## Противоречия

        ## История
        """;

    /// <summary>Id страницы — детерминированный (base64url(sourceKey|relPath), как NotesService.EncodeId).</summary>
    private static string PageId(string relUnderPages) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("wiki|" + relUnderPages))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed class NoProjects : IProjectManager
    {
        public Project? GetById(string id) => null;
        public IReadOnlyCollection<Project> GetByOwner(string userId) => [];
        public IReadOnlyCollection<Project> GetAll() => [];
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
    }

    private sealed class WikiFlagOn : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
