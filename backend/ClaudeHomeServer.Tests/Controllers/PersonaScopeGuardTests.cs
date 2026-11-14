using System.Reflection;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// W3-B1b (разрез docs/research/shared-projects-personas-cut-2026-11.md, §5 п.1–2): два сторожа
/// путей персон.
///
/// 1. Покрытие {id}-действий (default-deny, образец — таблица прав ActorToolRights): каждое действие
///    PersonasController с {id} в шаблоне имеет действующий [ProjectScope(From = Persona)] с правом
///    из таблицы §3.1. Новое {id}-действие без разметки или без строки таблицы — красный.
///    Действия без {id} — явный список с причиной: фильтр [ProjectScope] требует параметр id,
///    поэтому на таком действии он дал бы 400, а право там решается телом.
///
/// 2. Запрет инлайнового резолва. Текстовый сторож по backend/ClaudeHomeServer/Controllers/*.cs:
///    вызовы PersonaManager, ключованные вызывающим (_personas./personas. Get|GetProjectTeam|
///    GetForContext|GetForContextWithExtras|GetByHandle с аргументом UserId), допустимы только из
///    постоянного allow-list «личных» мест (§5 п.2, строки «без изменений» §3.2) и списка долга —
///    записей «файл + метод действия + шаг, который уберёт» (по таблицам §3.1–§3.2). Ключ записи —
///    файл+метод, не номер строки: строки поплывут при переносе тел. Запись без вызова сторож
///    НЕ краснит — её снимает шаг-владелец или W3-B8. Новый вызов вне списков — красный
///    с подсказкой «через ResolvePersona».
/// </summary>
public class PersonaScopeGuardTests
{
    // ─── Сторож 1: покрытие {id}-действий ────────────────────────────────────────────

    // Таблица прав {id}-действий — столбец «Р» таблицы §3.1 разреза. Read — право по умолчанию
    // атрибута; в таблице назван явно, чтобы таблица была единственным источником решения.
    private static readonly Dictionary<string, ProjectRight> IdActions = new(StringComparer.Ordinal)
    {
        ["Get"] = ProjectRight.Read,
        // Чтение эффективных секций промпта (разрез 2, §2.6, P1d): то же право, что у карточки
        ["PromptSections"] = ProjectRight.Read,
        ["Update"] = ProjectRight.Configure,
        ["Delete"] = ProjectRight.Configure,
        ["MakeDefault"] = ProjectRight.Configure,
        ["Chats"] = ProjectRight.Read,
        ["CreateChat"] = ProjectRight.RunTurn,
        ["GenerateAvatar"] = ProjectRight.Configure,
        ["AvatarCandidate"] = ProjectRight.Read,
        ["SelectAvatar"] = ProjectRight.Configure,
        ["Avatar"] = ProjectRight.Read,
        ["AvatarOriginal"] = ProjectRight.Read,
        ["UploadAvatar"] = ProjectRight.Configure,
        ["RecropAvatar"] = ProjectRight.Configure,
        ["SetVoice"] = ProjectRight.Configure,
        ["Bindings"] = ProjectRight.Read,
        ["AddBinding"] = ProjectRight.Configure,
        ["SetBindings"] = ProjectRight.Configure,
        ["UpdateBinding"] = ProjectRight.Configure,
        ["DeleteBinding"] = ProjectRight.Configure,
        ["SuggestBindings"] = ProjectRight.Configure,
        ["GenerateBindings"] = ProjectRight.Configure,
        ["ApplyDefaultBindings"] = ProjectRight.Configure,
        ["Automation"] = ProjectRight.Read,
        ["AddAutomationRule"] = ProjectRight.Configure,
        ["SetAutomationRules"] = ProjectRight.Configure,
        ["UpdateAutomationRule"] = ProjectRight.Configure,
        ["DeleteAutomationRule"] = ProjectRight.Configure,
        ["TestAutomationRule"] = ProjectRight.Configure,
        ["SuggestAutomation"] = ProjectRight.Configure,
        ["GenerateAutomation"] = ProjectRight.Configure,
        ["Memory"] = ProjectRight.Read,
        ["MemorySearch"] = ProjectRight.Read,
        ["MemoryRecall"] = ProjectRight.Read,
        ["Remember"] = ProjectRight.Configure,
        ["GetFocus"] = ProjectRight.Read,
        ["ClearFocus"] = ProjectRight.Configure,
        ["UpdateMemory"] = ProjectRight.Configure,
        // Отметка «секции специальности просмотрены» (B09): «только владелец» — как у правки персоны (решение 12.11)
        ["MarkSectionsSeen"] = ProjectRight.Configure,
        ["Forget"] = ProjectRight.Configure,
        ["ConfirmMemory"] = ProjectRight.Configure,
        ["MemoryToNote"] = ProjectRight.Configure,
        ["NoteToMemory"] = ProjectRight.Configure,
    };

    // Действия без {id}: явный allow-list с причиной (§3.1 «без изменений», строки B2/B3/B5).
    private static readonly Dictionary<string, string> NoIdActions = new(StringComparer.Ordinal)
    {
        ["List"] = "списки персон: id персоны нет, scope/projectId в query — W3-B2 (§3.1)",
        ["MatchPersona"] = "подбор под задачу: кандидаты по projectId из тела — W3-B2 (§3.1)",
        ["GetPantheon"] = "пантеон: личное пространство вызывающего (§3.1 «без изменений»)",
        ["ConnectPantheon"] = "пантеон: личное пространство вызывающего (§3.1 «без изменений»)",
        ["Create"] = "создание персоны: право по проекту из тела (W3-B3a закрыто: ProjectWriteArea → " +
            "Resolve(Configure)); ключ создания и OwnerId новой персоны — арендатор (§3.1)",
        ["Caps"] = "возможности аватара инстанса: не персона (§3.1 «без изменений»)",
        ["AiVoiceCandidates"] = "подбор голосов для формы: id персоны ещё нет (§3.1 «без изменений»)",
        ["AiCharacter"] = "генерация характера для формы: id персоны нет (§3.1 «без изменений»)",
        ["AiQuickCreate"] = "создание по промпту: то же право по проекту из тела и тот же ключ " +
            "арендатора (W3-B3a закрыто, §3.1)",
        ["AiTeam"] = "AI-команда проекта: то же право по проекту из тела и тот же ключ арендатора " +
            "(W3-B3a закрыто, §3.1)",
        ["BindingTargets"] = "каталог целей привязки: personaId в query, не в маршруте — W3-B5 (§3.1)",
        ["KnowledgeSearch"] = "агентский путь: доступ к датасету по владельцу (§3.1 «без изменений»)",
        ["AiCondition"] = "AI-формулировка условия: id персоны нет (§3.1 «без изменений»)",
        ["Ask"] = "агентский путь: DenyOnDelegatedTurn, токен арендатора (§3.1 «без изменений»)",
        ["MigrateSpecialtyInheritance"] = "разовая миграция прав персон: маршрут админский ([Authorize(Roles = "
            + "\"admin\")]), объекта персоны в пути нет, доступ проверяется ролью, а не scope — A1b (c7334f391), "
            + "разрез ai-fabrika-workflow-cut-2026-11 §2 «Миграция»",
    };

    private static List<(MethodInfo Method, string Verb, string Template)> RouteActions()
    {
        var result = new List<(MethodInfo, string, string)>();
        foreach (var m in typeof(PersonasController)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            foreach (var attr in m.GetCustomAttributes<HttpMethodAttribute>())
                result.Add((m, attr.HttpMethods?.FirstOrDefault() ?? "HTTP", attr.Template ?? ""));
        return result;
    }

    [Fact]
    public void Сторож_КаждоеIdДействиеПерсон_РазмеченоProjectScopePersonaСПравомИзРазреза()
    {
        typeof(PersonasController).GetCustomAttribute<ProjectScopeAttribute>().Should().BeNull(
            "классовый [ProjectScope] дал бы то же право действиям без {id}, а им фильтр дал бы 400 "
            + "(он требует параметр id) — разметка только по действиям");

        var actions = RouteActions();
        var withId = actions.Where(a => a.Template.Contains("{id}", StringComparison.Ordinal)).ToList();
        withId.Should().HaveCountGreaterThan(40, "сверка не должна быть вакуумной: базовый состав {id}-действий — 41");

        foreach (var (m, verb, template) in withId)
        {
            IdActions.Should().ContainKey(m.Name,
                $"новое {{id}}-действие {verb} {template} ({m.Name}) должно быть внесено в таблицу "
                + "сторожа с правом из §3.1 — default-deny: без строки оно не размечено");
            var scope = ProjectScopeAttribute.For(m);
            scope.Should().NotBeNull($"действие {m.Name} ({verb} {template}) обязано иметь [ProjectScope(From = Persona)]");
            scope!.From.Should().Be(ScopeSource.Persona, $"источник фильтра {m.Name} — Persona, а не {scope.From}");
            scope.Right.Should().Be(IdActions[m.Name], $"право {m.Name} обязано быть как в таблице §3.1 разреза");
        }

        IdActions.Keys.Except(withId.Select(a => a.Method.Name)).Should().BeEmpty(
            "в таблице нет мёртвых строк: снятое действие снимается из таблицы тем же коммитом");
    }

    [Fact]
    public void Сторож_ДействияПерсонБезId_ВЯвномСпискеСПричинойИБезАтрибута()
    {
        var withoutId = RouteActions().Where(a => !a.Template.Contains("{id}", StringComparison.Ordinal)).ToList();
        // 15 = базовые 14 (пантеон, списки, ai-пути, ask) + админская ручка разовой миграции A1b
        // (c7334f391): объекта персоны в пути нет, доступ закрыт ролью admin.
        withoutId.Should().HaveCount(15, "базовый состав действий без {id} — 15 (пантеон, списки, ai-пути, ask, "
            + "админская миграция A1b)");

        foreach (var (m, verb, template) in withoutId)
        {
            NoIdActions.Should().ContainKey(m.Name,
                $"новое действие {verb} {template} ({m.Name}) без {{id}} обязано быть внесено в список "
                + "явно и с причиной (личное пространство или резолв по проекту из тела)");
            ProjectScopeAttribute.For(m).Should().BeNull(
                $"[ProjectScope] на действии {m.Name} без {{id}} дал бы 400: фильтр требует параметр id");
        }

        NoIdActions.Keys.Except(withoutId.Select(a => a.Method.Name)).Should().BeEmpty(
            "в списке нет мёртвых строк: снятое действие снимается из списка тем же коммитом");
    }

    // ─── Сторож 2: запрет инлайнового резолва по вызывающему (§5 п.2) ────────────────

    // Постоянный allow-list «личных» мест (§5 п.2: онбординг, чаты вне проекта, McpServers;
    // строки «без изменений» §3.2): там ключ по вызывающему — правило, а не дефект.
    // Пантеон и ChatsController/McpServersController строк пока не имеют: вызовов их методов там нет;
    // новый вызов в этих местах краснит сторож и вносится автором шага со ссылкой на §5 п.2.
    private static readonly (string File, string Method, string Reason)[] PersonalAllowList =
    [
        ("OnboardingController.cs", "OnboardingController.StartProject", "онбординг: дефолт-персона проекта и ассистент владельца (§5 п.2)"),
        ("OnboardingController.cs", "OnboardingController.ApplyTranscript", "онбординг: персона из транскрипта (§5 п.2)"),
        ("AuthController.cs", "AuthController.Me", "личное пространство: DefaultPersonaId вызывающего в /auth/me (§3.2 «без изменений»)"),
        ("ProjectsController.cs", "ProjectsController.TeamMemoryWriteAllowed", "заголовок персоны-вызывающего для записи командной памяти (§3.2 «без изменений»)"),
        ("TasksController.cs", "TasksController.ChatModelResolver", "резолвер модели чата: персона задачи для слота модели (§3.2 «без изменений», та же строка, что «по задаче»)"),
    ];

    // Список долга: «файл + метод действия + шаг, который уберёт» — по таблицам §3.1–§3.2.
    // Ключ — файл+метод, не номер строки. Запись без вызова не краснит: снимает шаг-владелец или W3-B8.
    // W3-B8 (закрытие сторожа волны): строки сняты шагами волны (W3-B1c/B1d/B2/B3a/B3b/B4b/B5a/
    // B5b; история снятия каждой строки — в истории файла). Слепые участки паттерна §5 п.2
    // остаются и решением этого шага не стали: вызовы через _crud.* (правка карточки,
    // голос, выбор аватара, ValidateAsync/UpdateBindings, путь создания) паттерн не видит; смысл
    // тех шагов держат строки матриц SharedProjectPersonaRightsTests/SharedProjectPersonaBindingsTests.
    // Остаток W3-B7 закрыт доработкой (задача d18fb86a): параметр TaskPersonaValidator.Error
    // переименован в tenantId, все вызовы (мерж фичи, TasksToolset) идут с ключом арендатора
    // проекта задачи. Список долга пуст; новый инлайн с ключом вызывающего красит сторож.
    private static readonly (string File, string Method, string Step)[] DebtList = [];

    // Паттерн §5 п.2: _personas./personas.(Get|GetProjectTeam|GetForContext|GetForContextWithExtras|
    // GetByHandle)(…UserId…) — вызов PersonaManager, ключованный вызывающим.
    private static readonly Regex CallRe = new(
        @"(?<![A-Za-z0-9_.])(_?personas)\.(Get|GetProjectTeam|GetForContext|GetForContextWithExtras|GetByHandle)\(",
        RegexOptions.Compiled);

    // Аргумент-ключ вызывающего: отдельное слово userId/UserId (createdByUserId, tenantId, OwnerId —
    // не аргумент-ключ вызывающего, их этот сторож не касается).
    private static readonly Regex CallerKeyArgRe = new(
        @"(?<![A-Za-z0-9_])userId(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClassRe = new(
        @"^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|partial\s+)*class\s+(\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Объявление метода (включая expression-bodied). Первичные конструкторы классов/рекордов
    // исключены: имя типа — не метод.
    private static readonly Regex MethodRe = new(
        @"^\s*(?:public|private|internal|protected)\s(?!.*\b(?:class|record|interface|enum)\s+\w+\()[^\n]*?\s(\w+)\s*\(",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Сторож_РезолвПерсонывызывающим_ТолькоИзЛичныхМестИСпискаДолга()
    {
        var dir = Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer", "Controllers");
        var allowed = PersonalAllowList.Select(x => (x.File, x.Method))
            .Concat(DebtList.Select(x => (x.File, x.Method)))
            .ToHashSet();

        var offenders = new List<string>();
        var found = 0;
        foreach (var path in Directory.GetFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            var source = File.ReadAllText(path);
            foreach (Match call in CallRe.Matches(source))
            {
                var args = BalancedArgs(source, call.Index + call.Length - 1);
                if (!CallerKeyArgRe.IsMatch(args)) continue; // ключ не вызывающий (tenant/owner) — не нарушение
                found++;
                var key = (fileName, EnclosingMethod(source, call.Index));
                if (allowed.Contains(key)) continue;
                var line = source.Take(call.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{fileName}:{line} {key.Item2} — ключ по вызывающему, резолвить через ResolvePersona");
            }
        }

        // Пол «невакуумности» следует за долгом: базовый состав был 40+; шаги волны переводят
        // инлайновые вызовы на ResolvePersona и он законно падает (W3-B4b снял два места в
        // SessionsController.Create, W3-B2 — места списков, W3-B1c — 10 тел read-путей,
        // W3-B1d — 14 тел write-путей в PersonasController, W3-B3b — UploadAvatar/RecropAvatar,
        // W3-B5a — AddBinding/UpdateBinding/DeleteBinding, W3-B5b — BindingTargets/Suggest/
        // Generate/ApplyDefault; остаток W3-B7 закрыт доработой d18fb86a — валидатор ключуется
        // tenantId). 4 — по текущему составу с запасом (5 «личных» allow-list мест, долга нет):
        // обнуление области всё ещё красное.
        found.Should().BeGreaterThan(4, "сверка не должна быть вакуумной: базовый состав инлайновых вызовов — десятки");
        offenders.Should().BeEmpty(
            "вызовы PersonaManager, ключованные вызывающим, допустимы только из «личных» мест и списка долга "
            + "(W3-B1b, разрез §5 п.2); новый вызов вне списков — через ResolvePersona");
    }

    // Аргументы вызова от открывающей скобки до парной закрывающей (вложенные скопки аргументов
    // учитываются; строковых скобок в этих вызовах нет).
    private static string BalancedArgs(string source, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[(openIndex + 1)..i];
        }
        return source[(openIndex + 1)..];
    }

    // Метод-контейнер вызова: последнее объявление класса+метода до позиции вызова.
    // Лямбда внутри действия относится к своему действию — ключ тот же.
    private static string EnclosingMethod(string source, int index)
    {
        var cls = "";
        foreach (Match c in ClassRe.Matches(source))
        {
            if (c.Index >= index) break;
            cls = c.Groups[1].Value;
        }
        var method = "";
        foreach (Match m in MethodRe.Matches(source))
        {
            if (m.Index >= index) break;
            method = m.Groups[1].Value;
        }
        return method.Length == 0 ? "<вне метода>" : cls.Length > 0 ? $"{cls}.{method}" : method;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }
}
