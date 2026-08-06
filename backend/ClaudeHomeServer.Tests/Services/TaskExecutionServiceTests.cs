using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты чистой логики Claude-исполнителя: маппинг result → итог задачи и уведомления.
// Полный пайплайн (запуск сессии) требует claude.exe и здесь не гоняется.
public class TaskExecutionServiceTests
{
    private static ResultMessage Result(string subtype) =>
        new(subtype, DurationMs: 100, NumTurns: 1, Usage: null, TotalCostUsd: null);

    // ─── result → success/error ──────────────────────────────────────────────

    [Theory]
    [InlineData("success", true)]
    [InlineData("error", false)]
    [InlineData("error_max_turns", true)] // не "error" буквально — считается успехом хода
    public void IsSuccess_ПоSubtype(string subtype, bool expected)
    {
        TaskExecutionService.IsSuccess(Result(subtype)).Should().Be(expected);
    }

    // ─── Модель исполнителя: задача → персона → место ────────────────────────

    [Fact]
    public void ResolveExecutorModel_УровеньЗадачи_СильнееМоделиПерсоны()
    {
        var task = new TaskItem { Title = "t", ModelTier = ModelTier.Strong };
        var persona = new Persona { Model = "glm-5.2", ModelTier = ModelTier.Weak };

        // Маркер, а не конкретная модель: разворачивает его ModelAssignmentResolver
        TaskExecutionService.ResolveExecutorModel(task, persona).Should().Be("tier:strong");
    }

    [Fact]
    public void ResolveExecutorModel_БезУровняЗадачи_МодельПерсоны()
    {
        var task = new TaskItem { Title = "t" };
        var persona = new Persona { Model = "glm-5.2", ModelTier = ModelTier.Weak };

        TaskExecutionService.ResolveExecutorModel(task, persona).Should().Be("glm-5.2");
    }

    [Fact]
    public void ResolveExecutorModel_БезМоделиПерсоны_ЕёУровень()
    {
        var task = new TaskItem { Title = "t" };
        var persona = new Persona { ModelTier = ModelTier.Weak };

        TaskExecutionService.ResolveExecutorModel(task, persona).Should().Be("tier:weak");
    }

    [Fact]
    public void ResolveExecutorModel_НичегоНеЗадано_ПрежнееПоведение()
    {
        // Пусто → сессия резолвит место tasks-executor, как и до появления уровней
        TaskExecutionService.ResolveExecutorModel(new TaskItem { Title = "t" }, persona: null)
            .Should().BeNull();
        TaskExecutionService.ResolveExecutorModel(new TaskItem { Title = "t" }, new Persona())
            .Should().BeNull();
    }

    // Примечание: специальность персоны в резолве исполнителя теперь идёт через матрицы
    // (ADR-007 §2) и тестируется на уровне ModelAssignmentResolver.ExecutorModel/PersonaModel
    // — см. LocalActionRoutingTests (порядок «персона → специальность → владелец», DefaultTier).

    // ─── Отслеживание сессии ─────────────────────────────────────────────────

    [Fact]
    public void IsAwaitingResult_ЗапущенаИБезИтога_True()
    {
        var task = new TaskItem { Title = "t", ClaudeStartedAt = DateTime.UtcNow };
        TaskExecutionService.IsAwaitingResult(task).Should().BeTrue();
    }

    [Fact]
    public void IsAwaitingResult_НеЗапускалась_False()
    {
        TaskExecutionService.IsAwaitingResult(new TaskItem { Title = "t" }).Should().BeFalse();
    }

    [Fact]
    public void IsAwaitingResult_ИтогУжеЕсть_False()
    {
        var task = new TaskItem
        {
            Title = "t",
            ClaudeStartedAt = DateTime.UtcNow,
            ClaudeResult = "success",
        };
        TaskExecutionService.IsAwaitingResult(task).Should().BeFalse();
    }

    // ─── Уведомления ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildResultNotification_УспехИЗадачаDone_ЧистыйЗаголовок()
    {
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.Done };

        var n = TaskExecutionService.BuildResultNotification(task, ok: true);

        // Title чистый (без вклейки исполнителя — персона идёт структурно через PersonaId)
        n.Title.Should().Be("Завершил работу над задачей");
        n.Body.Should().Be("Задача");
        n.Kind.Should().Be("success");
        n.PersonaId.Should().BeNull();
        n.Url.Should().Be(TaskSchedulerService.TaskUrl(task));
    }

    [Fact]
    public void BuildResultNotification_УспехНоЗадачаНеDone_ПроситПроверить()
    {
        // Claude завершил ход, но не вызвал tasks_complete — нужен взгляд пользователя
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.InProgress };

        var n = TaskExecutionService.BuildResultNotification(task, ok: true);

        n.Body.Should().Be("Задача — проверь результат в чате");
    }

    [Fact]
    public void BuildResultNotification_Ошибка_ЗаголовокПроНеудачу()
    {
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.InProgress };

        var n = TaskExecutionService.BuildResultNotification(task, ok: false);

        n.Title.Should().Be("Не смог выполнить задачу");
        n.Kind.Should().Be("claude");
    }

    [Fact]
    public void BuildDelegatorNotification_Успех_ВедётВИсходныйЧат()
    {
        // Единственное уведомление о завершении делегированной задачи — ссылка ведёт туда,
        // где следом появится доклад-реплика исполнителя
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.Done, ProjectId = "p1" };
        var delegator = new Persona { Name = "Тимлид" };
        var source = new Session { Id = "s1", ProjectId = "p1" };

        var n = TaskExecutionService.BuildDelegatorNotification(task, ok: true, delegator, source);

        n.Title.Should().Be("Делегированная задача выполнена");
        n.Url.Should().Be("/project/p1/chat/s1");
        n.Tag.Should().Be("Постановщик");
    }

    [Fact]
    public void BuildDelegatorNotification_Провал_ВедётНаКарточкуЗадачи()
    {
        // При провале доклада в исходном чате не будет — вести туда некуда, разбираются
        // в карточке задачи (оттуда виден чат исполнителя)
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.InProgress, ProjectId = "p1" };
        var delegator = new Persona { Name = "Тимлид" };
        var source = new Session { Id = "s1", ProjectId = "p1" };

        var n = TaskExecutionService.BuildDelegatorNotification(task, ok: false, delegator, source);

        n.Title.Should().Be("Делегированная задача не выполнена");
        n.Url.Should().Be(TaskSchedulerService.TaskUrl(task));
    }

    [Fact]
    public void BuildWaitingNotification_permission_request_ЖдётОтвета()
    {
        var task = new TaskItem { Title = "Задача", ProjectId = "p1" };

        var n = TaskExecutionService.BuildWaitingNotification(task);

        n.Title.Should().Be("Ждёт ответа по задаче");
        n.Body.Should().Be("Задача");
        n.Kind.Should().Be("claude");
        n.ProjectId.Should().Be("p1");
        n.Url.Should().Be($"/project/p1/task/{task.Id}");
    }

    // ─── Промпт постановки ───────────────────────────────────────────────────

    [Fact]
    public void BuildPrompt_СодержитКонтекстЗадачиИПравила()
    {
        var task = new TaskItem
        {
            Title = "Починить сборку",
            Description = "Падает на CI",
            LinkedFiles = ["src/Program.cs"],
            Subtasks = [new TaskSubtask { Title = "Найти причину" }],
        };

        var prompt = TaskExecutionService.BuildPrompt(task);

        prompt.Should().Contain(task.Id);
        prompt.Should().Contain("# Починить сборку");
        prompt.Should().Contain("Падает на CI");
        prompt.Should().Contain("Найти причину").And.Contain(task.Subtasks[0].Id);
        prompt.Should().Contain("src/Program.cs");
        prompt.Should().Contain("tasks_complete");
        prompt.Should().Contain("tasks_toggle_subtask");
    }

    [Fact]
    public void BuildPrompt_БезПерсоны_ПрежнийФормат()
    {
        var prompt = TaskExecutionService.BuildPrompt(new TaskItem { Title = "t" });

        // Обратная совместимость: без персоны — прежний формат с блоком «Правила», без секций
        prompt.Should().Contain("Правила:");
        prompt.Should().NotContain("## ЗАДАЧА");
    }

    [Fact]
    public void BuildPrompt_СПерсоной_ШестьСекцийКонтракта()
    {
        var task = new TaskItem
        {
            Title = "Починить сборку",
            Description = "Падает на CI",
            LinkedFiles = ["src/Program.cs"],
            Subtasks = [new TaskSubtask { Title = "Найти причину" }],
        };
        var persona = new Persona { Name = "Вера", Role = "Планировщик" };

        var prompt = TaskExecutionService.BuildPrompt(task, persona);

        // Все 6 секций контракта, в порядке следования (КОНТЕКСТ — последняя:
        // блок заметок дописывается после и попадает в неё)
        string[] sections = ["## ЗАДАЧА", "## ОЖИДАЕМЫЙ РЕЗУЛЬТАТ", "## ИНСТРУМЕНТЫ",
            "## ОБЯЗАТЕЛЬНО", "## НЕЛЬЗЯ", "## КОНТЕКСТ"];
        var positions = sections.Select(s => prompt.IndexOf(s, StringComparison.Ordinal)).ToList();
        positions.Should().OnlyContain(p => p >= 0);
        positions.Should().BeInAscendingOrder();

        prompt.Should().Contain(task.Id);
        prompt.Should().Contain("Падает на CI");
        prompt.Should().Contain("Найти причину").And.Contain(task.Subtasks[0].Id);
        prompt.Should().Contain("src/Program.cs");
        prompt.Should().Contain("tasks_complete").And.Contain("tasks_toggle_subtask");
        // Дисциплина: не выходить за рамки, при невозможности — не завершать
        prompt.Should().Contain("Не выходи за рамки задачи");
        prompt.Should().Contain("не завершай задачу");
    }

    [Fact]
    public void BuildPrompt_СПерсоной_ДелегированиеБезПолныхПрофилей()
    {
        // Путь к справочнику — абсолютный: Read относительный не принимает
        var profiles = Path.Combine(Path.GetTempPath(), "cwd", ".claude", "delegation-categories.md");
        var prompt = TaskExecutionService.BuildPrompt(
            new TaskItem { Title = "t" }, new Persona { Name = "Вера" },
            categoryProfilesPath: profiles);

        // Короткая таблица категорий — в промпте
        prompt.Should().Contain("ultrabrain").And.Contain("visual-engineering");
        // Полные профили — только файлом на диске, в промпт не вставляются
        prompt.Should().NotContain("Ворота выбора");
        prompt.Should().Contain(profiles);
        // Правило выбора канала
        prompt.Should().Contain("tasks_run_executor").And.Contain("model=");
        // Разгрузка постановки: раньше секция делегирования одна весила ~30 КБ
        prompt.Length.Should().BeLessThan(8000);
    }

    [Fact]
    public void BuildPrompt_СправочникНеОбеспечен_СсылкиНет()
    {
        // Файла на диске нет (чужой файл, нерезолвимая папка) — гнать исполнителя
        // в несуществующий путь незачем: блок ссылки просто не выводим
        var prompt = TaskExecutionService.BuildPrompt(
            new TaskItem { Title = "t" }, new Persona { Name = "Вера" });

        prompt.Should().NotContain(PersonaAgentFileSync.CategoryProfilesFileName);
        prompt.Should().Contain("ultrabrain", "короткая таблица категорий остаётся");
    }

    // ─── Путь справочника в среде исполнения владельца ────────────────────────

    [Fact]
    public void ToRuntimeOrNull_ВладелецВПесочнице_ПутьПереводитсяВКонтейнерный()
    {
        // Читать файл будет процесс в cc-sandbox: там рабочая папка смонтирована
        // под /projects, и хостовый адрес не существует
        var (paths, projectsRoot) = PathsForOwner(ExecutionEnvironments.Container);
        var host = Path.Combine(projectsRoot, "anna", "app", ".claude",
            PersonaAgentFileSync.CategoryProfilesFileName);

        var runtime = TaskExecutionService.ToRuntimeOrNull(paths, host);

        runtime.Should().Be("/projects/anna/app/.claude/" + PersonaAgentFileSync.CategoryProfilesFileName);
        var prompt = TaskExecutionService.BuildPrompt(new TaskItem { Title = "t" },
            new Persona { Name = "Вера" }, categoryProfilesPath: runtime);
        prompt.Should().Contain(runtime).And.NotContain(host);
    }

    [Fact]
    public void ToRuntimeOrNull_ЛокальныйВладелец_ПутьНеМеняется()
    {
        var (paths, _) = PathsForOwner(ExecutionEnvironments.Local);
        var host = Path.Combine(Path.GetTempPath(), "proj", ".claude",
            PersonaAgentFileSync.CategoryProfilesFileName);

        TaskExecutionService.ToRuntimeOrNull(paths, host).Should().Be(host);
    }

    [Fact]
    public void ToRuntimeOrNull_ПутьВнеМонтированийПесочницы_СсылкиВПостановкеНет()
    {
        var (paths, _) = PathsForOwner(ExecutionEnvironments.Container);
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere_" + Guid.NewGuid().ToString("N"),
            ".claude", PersonaAgentFileSync.CategoryProfilesFileName);

        var runtime = TaskExecutionService.ToRuntimeOrNull(paths, outside);

        runtime.Should().BeNull("перевод невозможен — хостовый путь подставлять нельзя");
        var prompt = TaskExecutionService.BuildPrompt(new TaskItem { Title = "t" },
            new Persona { Name = "Вера" }, categoryProfilesPath: runtime);
        prompt.Should().NotContain(PersonaAgentFileSync.CategoryProfilesFileName);
        prompt.Should().Contain("ultrabrain", "короткая таблица категорий остаётся");
    }

    // Маппер путей владельца ровно той цепочкой, что в проде: ILauncherFactory.ForOwner().Paths
    private static (IPathMapper Paths, string ProjectsRoot) PathsForOwner(string environment)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "tes_paths_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            ["Sandbox:ProjectsRoot"] = Path.Combine(tmp, "ClaudeSandbox"),
        }).Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var user = users.Add("owner_" + Guid.NewGuid().ToString("N")[..8], "pwd", "user", environment);
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        var paths = new LauncherFactory(users, sandbox).ForOwner(user.Id).Paths;
        return (paths, Path.Combine(tmp, "ClaudeSandbox"));
    }

    [Fact]
    public void BuildPrompt_БезАлиасовТиров_ТаблицаУровнейНеВыводится()
    {
        // Слоты владельца — модели стороннего провайдера: алиас не определён, врать нечем
        var prompt = TaskExecutionService.BuildPrompt(
            new TaskItem { Title = "t" }, new Persona { Name = "Вера" }, ModelTierAliases.None);

        // «сильная/средняя/слабая» есть и в таблице категорий — проверяем по колонке уровней
        prompt.Should().NotContain("`strong`").And.NotContain("`weak`");
        // Канал делегирования остаётся — он от алиасов не зависит
        prompt.Should().Contain("tasks_create(personaId, modelTier)");
    }

    [Fact]
    public void BuildPrompt_САлиасамиТиров_ПодставляетИхВТаблицу()
    {
        var prompt = TaskExecutionService.BuildPrompt(
            new TaskItem { Title = "t" }, new Persona { Name = "Вера" },
            new ModelTierAliases("opus", "sonnet", null));

        prompt.Should().Contain("| сильная | `opus` | `strong` |");
        prompt.Should().Contain("| средняя | `sonnet` | `medium` |");
        prompt.Should().NotContain("`weak`", "у слабого слота алиас не определился");
    }

    // ─── Уведомления от лица персоны ─────────────────────────────────────────
    // Персона теперь передаётся структурно (PersonaId), а не вклеивается в заголовок:
    // имя/роль/аватар денормализует NotificationService и показывает мета-строкой.

    [Fact]
    public void BuildResultNotification_СПерсоной_ПробрасываетPersonaId()
    {
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.Done };
        var persona = new Persona { Name = "Вера", Role = "Планировщик" };

        var n = TaskExecutionService.BuildResultNotification(task, ok: true, persona);

        n.Title.Should().Be("Завершил работу над задачей");   // без имени в тексте
        n.PersonaId.Should().Be(persona.Id);
    }

    [Fact]
    public void BuildWaitingNotification_СПерсоной_ПробрасываетPersonaId()
    {
        var task = new TaskItem { Title = "Задача" };
        var persona = new Persona { Name = "Вера" };

        var n = TaskExecutionService.BuildWaitingNotification(task, persona);

        n.Title.Should().Be("Ждёт ответа по задаче");
        n.PersonaId.Should().Be(persona.Id);
    }

    // ─── Модель Z: доклад делегированной задачи в чат ────────────────────────

    [Fact]
    public void BuildDelegationReportText_СИтогом_МаркерПлюсResultMarkdown()
    {
        var task = new TaskItem { Title = "Починить сборку", ResultMarkdown = "Собрал, тесты зелёные." };

        var text = TaskExecutionService.BuildDelegationReportText(task);

        text.Should().StartWith(TaskExecutionService.DelegationReportMarker + "Починить сборку");
        text.Should().Contain("Собрал, тесты зелёные.");
    }

    [Fact]
    public void BuildDelegationReportText_БезИтога_Фолбэк()
    {
        var task = new TaskItem { Title = "Задача без итога", ResultMarkdown = null };

        var text = TaskExecutionService.BuildDelegationReportText(task);

        text.Should().Contain("(итог не указан)");
    }

    [Fact]
    public void BuildDelegatorReactionPrompt_СодержитИсполнителяИЗадачуБезДубляТела()
    {
        // MINOR 1: полный resultMarkdown в промпт реакции не дублируем — его уже видно
        // выше в ленте гостевой репликой B (ШАГ 1); здесь только выжимка (id/название)
        var task = new TaskItem { Title = "Починить сборку", ResultMarkdown = "Готово, собрал и прогнал тесты." };
        var executor = new Persona { Name = "Вера", Role = "Тестировщик" };

        var prompt = TaskExecutionService.BuildDelegatorReactionPrompt(task, executor);

        prompt.Should().Contain("Тестировщик (Вера)");
        prompt.Should().Contain("Починить сборку");
        prompt.Should().Contain(task.Id);
        prompt.Should().Contain("Отреагируй");
        prompt.Should().NotContain("Готово, собрал и прогнал тесты.");
    }

    [Fact]
    public void BuildDelegatorReactionPrompt_БезПерсоныИсполнителя_НеПадаетИНейтральнаяПодпись()
    {
        // Исполнитель — Claude без персоны (assignee=Claude, PersonaId=null), постановщик есть:
        // ШАГ 2 идёт, в промпте раньше PersonaLabel(null) ронял бы NRE. Теперь — нейтральная
        // подпись; задача/реакция присутствуют, падения нет.
        var task = new TaskItem { Title = "Починить сборку" };

        var prompt = TaskExecutionService.BuildDelegatorReactionPrompt(task, executor: null);

        prompt.Should().Contain("Исполнитель-Claude (без персоны)");
        prompt.Should().Contain("Починить сборку");
        prompt.Should().Contain(task.Id);
        prompt.Should().Contain("Отреагируй");
    }

    // ─── MAJOR 1: гейт запуска исполнителя — на бэкенде, а не в составе инструментов ──
    // Раньше он жил в env TASKS_EXECUTE, то есть в СОСТАВЕ tools/list, а состав входит в
    // сигнатуру запуска CLI: чередование обычного и делегированного хода убивало процесс со
    // всеми MCP-серверами. Теперь состав постоянен, а решение принимает DenyOnDelegatedTurn.

    [Theory]
    // (глубина, подавление, учитывать подавление) → запретить?
    [InlineData(0, false, true, false)]   // обычный ход пользователя — можно
    [InlineData(1, false, true, true)]    // делегированный ход (chats_send) — нельзя
    [InlineData(2, false, true, true)]    // глубже по цепочке — тем более нельзя
    [InlineData(0, true, true, true)]     // реакция на доклад исполнителя — нельзя (цикл A↔B)
    [InlineData(0, true, false, false)]   // подавление учитывают только запуск исполнителя
    public void ЗапретДействияНаХоду(int depth, bool suppressed, bool alsoWhenSuppressed, bool expected)
    {
        var turn = new TurnDelegationState(depth, suppressed);

        DenyOnDelegatedTurnAttribute.ShouldDeny(turn, alsoWhenSuppressed).Should().Be(expected);
    }

    // ─── MAJOR 2: регулярная задача без SourceSessionId — доклад не заводит чат ─

    [Fact]
    public void ShouldReportToDelegator_БезSourceSessionId_Неприменимо()
    {
        // 2-й+ экземпляр регулярной делегированной задачи: CreatedByPersonaId перенесён
        // SpawnNextOccurrence, а SourceSessionId — нет (сессия начинается заново). Без него
        // ReportToDelegatorAsync должен выйти сразу же, не создавая fallback-чат на повтор.
        var executor = new Persona { Name = "Вера" };
        var task = new TaskItem
        {
            Title = "Ежедневная сводка",
            Status = TaskItemStatus.Done,
            CreatedByPersonaId = Guid.NewGuid().ToString(),
            PersonaId = executor.Id,
            SourceSessionId = null,
        };

        TaskExecutionService.ShouldReportToDelegator(task, executor).Should().BeFalse();
    }

    [Fact]
    public void ShouldReportToDelegator_СSourceSessionIdИЧужимИсполнителем_Применимо()
    {
        var executor = new Persona { Name = "Вера" };
        var task = new TaskItem
        {
            Title = "Ежедневная сводка",
            Status = TaskItemStatus.Done,
            CreatedByPersonaId = Guid.NewGuid().ToString(),
            PersonaId = executor.Id,
            SourceSessionId = Guid.NewGuid().ToString(),
        };

        TaskExecutionService.ShouldReportToDelegator(task, executor).Should().BeTrue();
    }

    [Fact]
    public void ShouldReportToDelegator_ИсполнительСамСебеПостановщик_Неприменимо()
    {
        // Дубль тоста «Завершил работу» — докладывать самому себе незачем
        var executor = new Persona { Name = "Вера" };
        var task = new TaskItem
        {
            Title = "t",
            CreatedByPersonaId = executor.Id,
            SourceSessionId = Guid.NewGuid().ToString(),
        };

        TaskExecutionService.ShouldReportToDelegator(task, executor).Should().BeFalse();
    }

    [Fact]
    public void ShouldReportToDelegator_ЗадачуПоставилЧеловек_ТеперьПрименимо()
    {
        // Раньше без постановщика-персоны доклад не отправлялся вовсе, и задача, поставленная
        // человеком, тихо завершалась без следа в родительском чате. Лицо для карточки есть
        // всегда: персона исполнителя либо нейтральная карточка с именем его чата.
        var executor = new Persona { Name = "Вера" };
        var task = new TaskItem
        {
            Title = "t",
            CreatedByPersonaId = null,
            SourceSessionId = Guid.NewGuid().ToString(),
        };

        TaskExecutionService.ShouldReportToDelegator(task, executor).Should().BeTrue();
    }

    [Fact]
    public void ShouldReportToDelegator_ИсполнительНеПерсона_Применимо()
    {
        // Обычный Claude-исполнитель: карточка пойдёт с именем его чата
        var task = new TaskItem
        {
            Title = "t",
            CreatedByPersonaId = Guid.NewGuid().ToString(),
            SourceSessionId = Guid.NewGuid().ToString(),
        };

        TaskExecutionService.ShouldReportToDelegator(task, executor: null).Should().BeTrue();
    }

    // ─── Адресат доклада: ручная группировка чатов побеждает связь по задаче ────

    [Fact]
    public void ResolveReportTarget_БезЧатаИсполнителя_ПадаетНаSourceSessionId()
    {
        // Задача закрыта без запуска исполнителя — эффективного родителя взять неоткуда
        var source = Guid.NewGuid().ToString();

        TaskExecutionService.ResolveReportTarget(null, source).Should().Be(source);
    }

    [Fact]
    public void ResolveReportTarget_РучнойРодитель_ПеребиваетSourceSessionId()
    {
        // Пользователь перетащил чат-исполнитель к другому родителю — доклад идёт туда,
        // а не в чат, из которого задача была создана
        var source = Guid.NewGuid().ToString();
        var manual = Guid.NewGuid().ToString();
        var executorSession = new Session { TaskId = "t-1", ParentOverrideId = manual };

        TaskExecutionService.ResolveReportTarget(executorSession, source).Should().Be(manual);
    }

    [Fact]
    public void ResolveReportTarget_ЧатВынесенВКорень_ДокладыватьНекуда()
    {
        // «Вынес из группы» значит «не докладывай туда» — доклад гасится целиком
        var executorSession = new Session { TaskId = "t-1", ParentDetached = true };

        TaskExecutionService.ResolveReportTarget(executorSession, Guid.NewGuid().ToString())
            .Should().BeNull();
    }

    // ─── MINOR 2: групповой чат — реакция только от лица постановщика ───────────

    [Fact]
    public void CanSendDelegatorReaction_НеГрупповойЧат_Можно()
    {
        TaskExecutionService.CanSendDelegatorReaction(null, "a").Should().BeTrue();
        TaskExecutionService.CanSendDelegatorReaction(["a"], "a").Should().BeTrue();
    }

    [Fact]
    public void CanSendDelegatorReaction_ГрупповойЧатСПостановщикомСредиУчастников_Можно()
    {
        TaskExecutionService.CanSendDelegatorReaction(["a", "b", "c"], "b").Should().BeTrue();
    }

    [Fact]
    public void CanSendDelegatorReaction_ГрупповойЧатБезПостановщика_Нельзя()
    {
        // Переключить спикера не на кого — реагировать в группе некому,
        // ограничиваемся гостевой репликой B + L0-тостом
        TaskExecutionService.CanSendDelegatorReaction(["b", "c"], "a").Should().BeFalse();
    }

    // ─── ШАГ 2 без постановщика-персоны: авто-ход не запускаем ──────────────────
    // Задачу мог поставить человек из обычного чата (без персоны). ШАГ 1 (гостевая реплика)
    // при этом идёт — но платный авто-ход постановщика запускать не от чьего лица нельзя:
    // иначе рабочий чат оживал бы сам на каждое завершение задачи.

    [Fact]
    public void ShouldSendDelegatorReaction_БезПостановщика_False()
    {
        // CreatedByPersonaId=null → delegator null → ШАГ 2 не пускаем
        TaskExecutionService.ShouldSendDelegatorReaction(delegator: null).Should().BeFalse();
    }

    [Fact]
    public void ShouldSendDelegatorReaction_СПостановщиком_True()
    {
        var delegator = new Persona { Name = "Артём" };
        TaskExecutionService.ShouldSendDelegatorReaction(delegator).Should().BeTrue();
    }
}
