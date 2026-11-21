using System.Reflection;
using System.Runtime.CompilerServices;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services.Architecture;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.Plugins.Controllers;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// Сторож соответствия детской поверхности и серверных ручек (Б18b, ADR-041 ред. 8, решение 77,
/// разрез §14 риск 4): у каждой панели из <see cref="ChildModeAllowList.Surface"/>.Panels есть
/// открытая ребёнку ручка, у панелей вне списка — ни одной. Без этого default-deny поверхности
/// держится только на дисциплине: <c>ChildModeEndpointGuardTests</c> морозит состав размеченных,
/// но не отвечает на «разметка приехала к закрытой панели».
///
/// Таблица ниже — отражение реестра панелей фронта (panelCatalog.ts, PANEL_KEYS) на момент шага:
/// каждая закрытая панель стоит на своём контроллере (обслуживание сверено с фронтовым вызовом),
/// состав ключей таблицы сверяется с <c>Surface.Panels</c> тестом — новый ключ без строки красный.
///
/// Формулировка решения 77 «ручки без [ChildAllowed] → 403» держится здесь без исключений для
/// всех контроллеров, кроме трёх документированных: их размеченные экшены принадлежат не
/// закрытой панели, а открытой детской поверхности — чтение своего урока (ChatsController.Get /
/// GetHistory), аватар репетитора (PersonasController.Avatar), CRUD заметок проекта-предмета
/// (NotesController, открытая панель «notes»). Исключения привязаны к конкретному контроллеру строки;
/// любой другой экшен этих контроллеров с меткой красит сторож.
///
/// Вне таблицы и вне ответственности этого сторожа: <c>terminal</c> — хаб /hubs/terminal, замок в
/// ChildHubFilter на подключении (решение 77); <c>toc</c> — нет REST-контроллера, оглавление
/// фронт строит из текста документа.
/// </summary>
public class ChildSurfaceGuardTests
{
    // Форс-загрузка сборок: без неё сторож проходит вакуумно (набор контроллеров пуст).
    // typeof-ниже тянет Main, Core, Notes, Turn, Architecture, Plugins, ImageEditor, AudioEditor.
    static ChildSurfaceGuardTests()
    {
        _ = typeof(ChildModeAllowList).Assembly;
        _ = typeof(FilesController).Assembly;
        _ = typeof(NotesController).Assembly;
        _ = typeof(IPromptSectionContributor).Assembly;
        _ = typeof(ArchitectureController).Assembly;
        _ = typeof(PluginsController).Assembly;
        _ = typeof(ImageEditorController).Assembly;
        _ = typeof(AudioVoicesController).Assembly;
    }

    // Строка таблицы: контроллер панели и его документированные исключения — экшены,
    // размеченные ребёнку в силу другой открытой поверхности (причина — в комментарии строки).
    private sealed record Row(Type Controller, string[]? Exceptions = null);

    // Открытые панели (ровно ключи Surface.Panels) → контроллеры, обязанные иметь [ChildAllowed].
    private static readonly Dictionary<string, Row[]> OpenPanels = new()
    {
        // Рельса «Файлы» проекта-предмета (Б11a/Б11b): читающие и пишущие двери над папкой предмета.
        ["files"] = [new Row(typeof(FilesController))],
        // «Знания» — только чтение базы (Б20a).
        ["knowledge"] = [new Row(typeof(KnowledgeController))],
        // «Заметки» — CRUD notes/ проекта-предмета (Б20b).
        ["notes"] = [new Row(typeof(NotesController))],
        // «Контекст» — только GET (Б20c).
        ["chatContext"] = [new Row(typeof(ChatContextController))],
        // «Задачи» — доска своего проекта-предмета (Б20d). Панель обслуживают два контроллера
        // одного файла: ProjectTasksController (список/создание api/projects/{id}/tasks) и
        // TasksController (правка/закрытие/удаление api/tasks/{id}) — метки делят они пополам.
        ["tasks"] = [new Row(typeof(ProjectTasksController)), new Row(typeof(TasksController))],
        // «Чаты» — рельса уроков проекта-предмета (Х-W4-77, дефект fab7e418): список
        // GET api/projects/{id}/sessions (SessionsController.GetAll), «свой ли предмет» — внутри ручки.
        ["chats"] = [new Row(typeof(SessionsController))],
    };

    // Нижняя граница размеченных экшенов открытых панелей: текущий состав
    // Files 13 + Notes 5 + Knowledge 2 + ChatContext 2 + ProjectTasks 2 + Tasks 2 + Sessions 1.
    // Ниже — сборки не догружены или метки сняты без решения: сторож обязан краснеть,
    // а не пройти по пустому набору.
    private const int MarkedFloor = 27;

    // Панели вне детской поверхности → их контроллеры (обслуживание сверено с фронтовым вызовом).
    private static readonly Dictionary<string, Row[]> ClosedPanels = new()
    {
        // ChatsController: Get/GetHistory — чтение своего урока (Б6f), открытая детская поверхность
        // вне панели. Остальные экшены класса метки не имеют. Сам список уроков — открытая панель
        // «chats» (OpenPanels, SessionsController.GetAll, Х-W4-77).
        ["lessonRead"] = [new Row(typeof(ChatsController), ["Get", "GetHistory"])],
        // «Изменения» (решение 77: «GitController без [ChildAllowed]», git выключен у проекта-предмета).
        ["changes"] = [new Row(typeof(GitController))],
        // «Документация».
        ["docs"] = [new Row(typeof(DocsController))],
        // «История решений».
        ["dossiers"] = [new Row(typeof(DossiersController))],
        // «Граф» зависимостей кода.
        ["graph"] = [new Row(typeof(CodeGraphController))],
        // «Архитектура» (модуль Viaduct).
        ["arch"] = [new Row(typeof(ArchitectureController))],
        // «Команда» — персоны проекта (TeamCommandCenter читает api.personas.*). Avatar — аватар
        // репетитора, плитка предмета ребёнка (Т1e), не панель «Команда».
        ["team"] = [new Row(typeof(PersonasController), ["Avatar"])],
        // «Навыки».
        ["skills"] = [new Row(typeof(SkillsController))],
        // «Сервисы» (решение 77: «PreviewController и /preview/** за middleware»). Services
        // (Х-W4-31, дефект d7c8477b) — исключение: закрытие прямого обращения к взрослому
        // page-level опросу (метка + «свой проект-предмета» внутри ручки, образец «Знаний»);
        // панель ребёнку не открыта, start/stop/active/external и launch.json метки не имеют.
        ["preview"] = [new Row(typeof(PreviewController), ["Services"])],
        // «План» хода (PlanSection читает api/plans/map).
        ["plan"] = [new Row(typeof(PlansController))],
        // «Агенты» (решение 77: «чтение транскриптов субагентов (WorkflowController) без [ChildAllowed]»).
        ["agents"] = [new Row(typeof(WorkflowController))],
        // «Персона» (досье собеседника хода): память — api.personas.memory, командная память —
        // api/projects/{id}/team-memory; задачи по персоне читает TasksController — у него своя
        // открытая строка. Avatar — та же причина, что у «Команды». GetAll/GetById (Б21) — док
        // проектов ребёнка (решение 84): открытая ему поверхность вне рельса панелей (ключа дока
        // в Surface.Panels нет и не заводится), не панель «Персона»; team-memory-ручки класса
        // метки не имеют.
        ["context"] = [new Row(typeof(PersonasController), ["Avatar"]),
            new Row(typeof(ProjectsController), ["GetAll", "GetById"])],
        // «Эфир» (api.video.*).
        ["video"] = [new Row(typeof(VideoController))],
        // «Персонажи» редактора картинок: ручки персонажей живут в базе ImageEditorCharacterEndpoints,
        // которую наследуют оба контроллера.
        ["characters"] = [new Row(typeof(ImageEditorController)), new Row(typeof(PersonalImageEditorController))],
        // «Ферма» локальных моделей.
        ["localModel"] = [new Row(typeof(LocalModelStatsController))],
        // «Голоса» — библиотека voices/ проекта.
        ["voices"] = [new Row(typeof(AudioVoicesController))],
        // «Звук из Личного» (Ф-2): читает персональную папку через PersonalFilesController.
        ["personal-sound"] = [new Row(typeof(PersonalFilesController))],
        // Маркер семейства панелей плагинов (ADR-031 S5).
        ["pluginPanels"] = [new Row(typeof(PluginsController)), new Row(typeof(PluginBridgeController)), new Row(typeof(PluginDraftsController))],
        // Раздел хаба «Заметки» (все источники): ребёнку раздел не открыт; List/Get/Create/Update/
        // Delete — открытая панель «notes» проекта-предмета (Б20b), не раздел хаба.
        ["notesList"] = [new Row(typeof(NotesController), ["List", "Get", "Create", "Update", "Delete"])],
        // «Граф» раздела «Заметки»: Graph/Backlinks/Move/Sources — без метки.
        ["notesGraph"] = [new Row(typeof(NotesController), ["List", "Get", "Create", "Update", "Delete"])],
        // Панели вики раздела «Заметки» («Решения», «Журнал», «Здоровье», «Настройки»): весь
        // ряд wiki*-контроллеров закрыт ребёнку.
        ["wikiInbox"] = WikiRows(),
        ["wikiJournal"] = WikiRows(),
        ["wikiHealth"] = WikiRows(),
        ["wikiSettings"] = WikiRows(),
        // Раздел хаба «Базы» (все датасеты): закрыт; открыто только чтение базы своего проекта
        // (KnowledgeController — открытая строка «knowledge»).
        ["knowledgeList"] = [new Row(typeof(KnowledgeBasesController))],
        // Раздел хаба «Персоны»: тот же контроль, что у «Команды» (Avatar — исключение).
        ["personasList"] = [new Row(typeof(PersonasController), ["Avatar"])],
        // Раздел хаба «Группы» проектов.
        ["projectGroups"] = [new Row(typeof(ProjectGroupsController))],
    };

    private static Row[] WikiRows() =>
    [
        new Row(typeof(WikiController)),
        new Row(typeof(WikiPagesController)),
        new Row(typeof(WikiPackagesController)),
        new Row(typeof(WikiChatsController)),
        new Row(typeof(WikiSettingsController)),
    ];

    private static bool HasChildAllowed(MethodInfo m) =>
        m.GetCustomAttributes(typeof(ChildAllowedAttribute), inherit: true).Length > 0;

    // Экшены контроллера: public-метки с атрибутом HTTP-метода (HttpGet и наследники),
    // включая унаследованные (характер-ручки живут в базе ImageEditorCharacterEndpoints).
    private static List<MethodInfo> HttpActions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(inherit: true).Any(a => a is HttpMethodAttribute))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

    private static List<string> MarkedNames(Row row) =>
        HttpActions(row.Controller).Where(HasChildAllowed).Select(m => m.Name).Distinct().ToList();

    // Правило 1: у каждой панели из Surface.Panels хотя бы один экшен с [ChildAllowed].
    [Fact]
    public void ОткрытыеПанели_ИмеютРучкиРебёнку()
    {
        var markedTotal = 0;
        foreach (var (panel, rows) in OpenPanels)
        {
            var marked = rows.SelectMany(MarkedNames).ToList();
            marked.Should().NotBeEmpty(
                $"панель «{panel}» в ChildModeAllowList.Surface.Panels — её контроллеры обязаны иметь [ChildAllowed]");
            markedTotal += marked.Count;
        }

        // Защита от вакуумного прохождения: сборки не догружены или метки сняты без решения.
        markedTotal.Should().BeGreaterThanOrEqualTo(MarkedFloor,
            "нижняя граница размеченных экшенов открытых панелей (13+5+2+2+2+2+1): ниже — сборки не догружены");
    }

    // Правило 2: у контроллеров панелей вне списка — ни одного [ChildAllowed],
    // кроме документированных исключений их строки.
    [Fact]
    public void ЗакрытыеПанели_НеИмеютРучекРебёнку()
    {
        foreach (var (panel, rows) in ClosedPanels)
        {
            foreach (var (controller, exceptions) in rows)
            {
                var actions = HttpActions(controller);
                actions.Count.Should().BeGreaterThan(0,
                    $"«{panel}» → {controller.Name}: у контроллера нет HTTP-экшенов — строка таблицы пуста, проверка была бы вакуумной");

                var leaks = actions
                    .Where(a => HasChildAllowed(a) && !(exceptions ?? []).Contains(a.Name))
                    .Select(a => a.Name)
                    .ToList();
                leaks.Should().BeEmpty(
                    $"панель «{panel}» вне детской поверхности (ADR-041 решение 77): её экшены не помечены [ChildAllowed], " +
                    "кроме исключений строки таблицы");

                // Исключение обязано указывать на живой экшен: переименованный — строка устарела.
                foreach (var name in exceptions ?? [])
                {
                    actions.Where(a => a.Name == name).Should().NotBeEmpty(
                        $"исключение строки «{panel}» → {controller.Name}.{name}: такого экшена нет — строка таблицы устарела");
                }
            }
        }
    }

    // Правило 3: таблица и Surface.Panels — один и тот же набор ключей.
    // Новый ключ Panels без строки — красный; снятый ключ с оставшейся строкой — тоже.
    [Fact]
    public void КлючиПанелей_СовпадаютСТаблицей()
    {
        var surface = ChildModeAllowList.Surface.Panels;
        surface.Should().NotBeEmpty();
        surface.Should().OnlyHaveUniqueItems();

        surface.Order(StringComparer.Ordinal).Should().Equal(OpenPanels.Keys.Order(StringComparer.Ordinal),
            "каждый ключ Surface.Panels обязан иметь строку открытых панелей, и наоборот");

        OpenPanels.Keys.Intersect(ClosedPanels.Keys).Should().BeEmpty(
            "панель не может быть одновременно открытой и закрытой");
        ClosedPanels.Keys.Intersect(surface).Should().BeEmpty(
            "строка закрытой панели не может стоять для ключа из Surface.Panels");
    }
}
