using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Чистые правила шаблона процесса (B1a, разрез ai-fabrika-workflow-cut-2026-11 §1):
// unit-тесты без стенда — WorkflowRules не трогает хранилище и HTTP.
public class WorkflowRulesTests
{
    private static WorkflowStage Stage(
        string role = TaskRoles.Step,
        string? presence = WorkflowStagePresence.On) =>
        new() { Role = role, Presence = presence! };

    private static List<string> ValidateOne(WorkflowStage stage) =>
        WorkflowRules.Validate(new WorkflowTemplate { Name = "тест", Stages = [stage] });

    [Fact]
    public void ВалидныйЭтап_БезОшибок()
    {
        var stage = Stage(); // step — Presence только on (несъёмная роль)
        stage.Specialty = PersonaSpecialty.BackendExecutor;
        stage.Prompt = "держи границы задачи";
        stage.TierStrong = "qwen3.8-27b"; // id модели — легальная лексика ячейки (ADR-007)
        stage.StallMinutes = 30;
        stage.StallPolicy = WorkflowStallPolicies.NudgeAlert;

        ValidateOne(stage).Should().BeEmpty();
    }

    // c2: несъёмные роли — ни off, ни optional (6 случаев). Мутация «убрать final из
    // NonSkippable» красит ровно два случая final.
    [Theory]
    [InlineData(TaskRoles.Step, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Step, WorkflowStagePresence.Optional)]
    [InlineData(TaskRoles.Merge, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Merge, WorkflowStagePresence.Optional)]
    [InlineData(TaskRoles.Final, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Final, WorkflowStagePresence.Optional)]
    public void НесъёмнаяРоль_СнятаяИлиОпциональная_Ошибка(string role, string presence)
    {
        var errors = ValidateOne(Stage(role, presence));

        errors.Should().ContainSingle()
            .Which.Should().Contain(role).And.Contain(presence);
    }

    // Съёмные роли — off и optional проходят: фаст-трек живёт именно на этом.
    [Theory]
    [InlineData(TaskRoles.Design, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Qa, WorkflowStagePresence.Optional)]
    public void СъёмнаяРоль_ПропускРазрешён(string role, string presence) =>
        ValidateOne(Stage(role, presence)).Should().BeEmpty();

    // Роли вне маршрута: rework берёт этап step, эпик и consult — не этап шаблона.
    [Theory]
    [InlineData(TaskRoles.Rework)]
    [InlineData(TaskRoles.Feature)]
    [InlineData(TaskRoles.Consult)]
    [InlineData("")]
    public void НеизвестнаяРольЭтапа_Ошибка(string role) =>
        ValidateOne(Stage(role)).Should().ContainSingle()
            .Which.Should().Contain("неизвестная роль");

    // Разрез 2 §1.6: роль может повторяться (у этапа свой id), дублем считается id.
    [Fact]
    public void ДубльId_Ошибка() =>
        WorkflowRules.Validate(new WorkflowTemplate
        {
            Name = "тест",
            Stages = [Stage(), Stage()],
        }).Should().ContainSingle().Which.Should().Contain("id этапа «step»").And.Contain("дважды");

    // G7 (a5d0c76e): два этапа одной роли без имени дали бы в графе две неотличимые подписи —
    // по правилу группы имён здесь обязателен Title у каждого и различный (имена добавлены).
    [Fact]
    public void ПовторяемаяРольСРазнымиId_Допустима() =>
        WorkflowRules.Validate(new WorkflowTemplate
        {
            Name = "тест",
            Stages =
            [
                WithId(TaskRoles.Plan, "plan", title: "План"),
                WithId(TaskRoles.Plan, "plan-qa", title: "Проверка плана"),
                Stage(TaskRoles.Step),
            ],
        }).Should().BeEmpty();

    [Theory]
    [InlineData(TaskRoles.Cut)]
    [InlineData(TaskRoles.Final)]
    [InlineData(TaskRoles.Merge)]
    public void ОдиночнаяРольДваждыСРазнымиId_Ошибка(string role) =>
        WorkflowRules.Validate(new WorkflowTemplate
        {
            Name = "тест",
            Stages = [WithId(role, "first"), WithId(role, "second")],
        }).Should().ContainSingle().Which.Should().Contain("одиночная").And.Contain(role);

    // Подпись этапа в ошибке — роль, а не id: фронтовый валидатор называет этап так же
    // («этап «merge»»), человек не должен видеть один этап под двумя именами.
    [Fact]
    public void ПодписьЭтапаВОшибке_Роль_НеId()
    {
        var errors = ValidateStages(WithId(TaskRoles.Merge, "first"), WithId(TaskRoles.Merge, "second"));

        errors.Should().ContainSingle().Which.Should().Be(
            "этап «merge» (second): роль «merge» одиночная — в маршруте она встречается дважды.");
    }

    // R-Ф-2.39: Title этапа — его метка в ошибке (перед ролью и id).
    [Fact]
    public void МеткаЭтапа_СTitle_ВОшибкеTitle()
    {
        var errors = ValidateStages(
            WithId(TaskRoles.Merge, "first", title: "Первый мерж"),
            WithId(TaskRoles.Merge, "second", title: "Второй мерж"));

        errors.Should().ContainSingle().Which.Should().Be(
            "этап «Второй мерж»: роль «merge» одиночная — в маршруте она встречается дважды.");
    }

    // R-Ф-2.39: две одинаковые по тексту ошибки с разными метками (роль + id) не схлопываются.
    [Fact]
    public void МеткаЭтапа_ПовторРолиБезTitle_РазныеМеткиВОшибках()
    {
        var errors = ValidateStages(
            WithId(TaskRoles.Qa, "qa-a"), WithId(TaskRoles.Qa, "qa-b", after: ["qa-a"]));

        errors.Should().Equal(
            "этап «qa» (qa-a): этап делит роль и линию с другим этапом — имя (Title) обязательно.",
            "этап «qa» (qa-b): этап делит роль и линию с другим этапом — имя (Title) обязательно.");
    }

    [Fact]
    public void ПодписьЭтапаВОшибкеИмён_Роль_НеId()
    {
        var errors = ValidateStages(
            WithId(TaskRoles.Qa, "qa-a"), WithId(TaskRoles.Qa, "qa-b", after: ["qa-a"]));

        errors.Should().HaveCount(2).And.OnlyContain(e =>
            e.StartsWith("этап «qa» (qa-", StringComparison.Ordinal) && e.Contains("): этап делит роль и линию"));
    }

    [Fact]
    public void ПодписьЭтапаВОшибкеГрафа_Роль_НеId()
    {
        var errors = ValidateStages(
            WithId(TaskRoles.Step, "first", after: []), WithId(TaskRoles.Merge, "last", after: []));

        errors.Should().ContainSingle().Which.Should().Be(
            "этап «step»: на него никто не ссылается — ветка не сходится в общий сток.");
    }

    private static WorkflowStage WithId(string role, string id, string[]? after = null, string? lane = null,
        string presence = WorkflowStagePresence.On, string? title = null) =>
        new() { Role = role, Id = id, After = after?.ToList(), Lane = lane, Presence = presence, Title = title };

    private static List<string> ValidateStages(params WorkflowStage[] stages) =>
        WorkflowRules.Validate(new WorkflowTemplate { Name = "тест", Stages = stages.ToList() });

    [Theory]
    [InlineData("Plan")]
    [InlineData("plan_1")]
    [InlineData("план")]
    public void IdНеSlug_Ошибка(string id) =>
        ValidateStages(WithId(TaskRoles.Plan, id)).Should().ContainSingle().Which.Should().Contain("не slug");

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void Id_ГраницаДлины(int length, bool expectError)
    {
        var errors = ValidateStages(WithId(TaskRoles.Plan, new string('a', length)));

        if (expectError) errors.Should().ContainSingle().Which.Should().Contain("не slug");
        else errors.Should().BeEmpty();
    }

    // After ссылается только на этапы выше по списку: себя, вперёд и в никуда — ошибка.
    [Fact]
    public void After_НаСебя_Ошибка() =>
        ValidateStages(WithId(TaskRoles.Plan, "a", after: ["a"])).Should().ContainSingle()
            .Which.Should().Contain("после самого себя");

    [Fact]
    public void After_НаЭтапНиже_Ошибка() =>
        ValidateStages(WithId(TaskRoles.Plan, "a", after: ["b"]), WithId(TaskRoles.Step, "b", after: []))
            .Should().ContainSingle().Which.Should().Contain("«b»").And.Contain("выше по списку");

    [Fact]
    public void After_НаНесуществующий_Ошибка() =>
        ValidateStages(WithId(TaskRoles.Plan, "a"), WithId(TaskRoles.Step, "b", after: ["нет"]))
            .Should().ContainSingle().Which.Should().Contain("«нет»");

    // Висящая ветка: step-1 никто не ждёт, а последний этап — review.
    [Fact]
    public void ВисящаяВетка_Ошибка() =>
        ValidateStages(
            WithId(TaskRoles.Plan, "plan"),
            WithId(TaskRoles.Step, "step-1", after: ["plan"], lane: "back"),
            WithId(TaskRoles.Review, "review", after: ["plan"]))
            .Should().ContainSingle().Which.Should().Be(
                "этап «step»: на него никто не ссылается — ветка не сходится в общий сток.");

    // Явный After: [] у не первого этапа — ещё один корень; ветки сходятся в review — допустимо.
    [Fact]
    public void НесколькоКорнейСоСтоком_Допустимо() =>
        ValidateStages(
            WithId(TaskRoles.Plan, "plan"),
            WithId(TaskRoles.Qa, "qa-plan", after: []),
            WithId(TaskRoles.Review, "review", after: ["plan", "qa-plan"])).Should().BeEmpty();

    // Снятие необязательного этапа не должно оставлять ветку без стока: X и W сходятся только в Y.
    // Проверка «все optional сняты разом» это пропустила бы — нужен перебор подмножеств.
    [Fact]
    public void СнятиеOptionalОставляетВисящуюВетку_Ошибка()
    {
        // Имена двух plan-этапов даны (G7): иначе ошибка имени заглушила бы проверяемую здесь
        // графовую.
        var errors = ValidateStages(
            WithId(TaskRoles.Plan, "a", title: "План"),
            WithId(TaskRoles.Design, "x", after: ["a"], presence: WorkflowStagePresence.Optional),
            WithId(TaskRoles.Plan, "w", after: ["a"], title: "План второй"),
            WithId(TaskRoles.Qa, "y", after: ["x", "w"], presence: WorkflowStagePresence.Optional));

        errors.Should().ContainSingle().Which.Should().Contain("при снятии").And.Contain("этап «qa»").And.Contain("не сходится");
    }

    [Fact]
    public void ОффЭтапОставляетВисящуюВетку_Ошибка()
    {
        // x выключен (off): w и z остаются без стока, а x был единственным их потомком.
        // Имена трёх plan-этапов даны (G7) — чтобы проверялась именно графовая ошибка.
        var errors = ValidateStages(
            WithId(TaskRoles.Plan, "a", title: "План"),
            WithId(TaskRoles.Plan, "w", after: ["a"], title: "План второй"),
            WithId(TaskRoles.Plan, "z", after: ["a"], title: "План третий"),
            WithId(TaskRoles.Design, "x", after: ["w", "z"], presence: WorkflowStagePresence.Off));

        errors.Should().ContainSingle().Which.Should().Contain("off").And.Contain("не сходится");
    }

    [Theory]
    [InlineData(TaskRoles.Step, "back", true)]
    [InlineData(TaskRoles.Step, "front", true)]
    [InlineData(TaskRoles.Step, "other", true)]
    [InlineData(TaskRoles.Step, "mobile", false)]
    [InlineData(TaskRoles.Review, "back", false)]
    public void Линия_ТолькоУStepИзБелогоСписка(string role, string lane, bool valid)
    {
        var errors = ValidateStages(WithId(role, role, lane: lane));

        if (valid) errors.Should().BeEmpty();
        else errors.Should().ContainSingle().Which.Should().ContainAny("неизвестная линия", "только у этапа step");
    }

    [Fact]
    public void ДвеStepСОднойЛинией_Ошибка() =>
        ValidateStages(
            WithId(TaskRoles.Step, "s1", lane: "back"),
            WithId(TaskRoles.Step, "s2", after: ["s1"], lane: "back"))
            .Should().ContainSingle().Which.Should().Contain("два этапа step").And.Contain("back");

    [Fact]
    public void ДвеStepСРазнымиЛиниями_БезОшибок() =>
        ValidateStages(
            WithId(TaskRoles.Plan, "plan"),
            WithId(TaskRoles.Step, "s-back", after: ["plan"], lane: "back"),
            WithId(TaskRoles.Step, "s-front", after: ["plan"], lane: "front"),
            WithId(TaskRoles.Review, "review", after: ["s-back", "s-front"])).Should().BeEmpty();

    // Потолок 16 этапов: граница запинена с обеих сторон.
    [Theory]
    [InlineData(16, false)]
    [InlineData(17, true)]
    public void ПотолокЭтапов(int count, bool expectError)
    {
        // Имена даны (G7: все этапы здесь одной роли): проверяется именно потолок.
        var errors = ValidateStages(Enumerable.Range(1, count)
            .Select(i => WithId(TaskRoles.Plan, $"p{i}", title: $"План {i}")).ToArray());

        if (expectError) errors.Should().ContainSingle().Which.Should().Contain("предел — 16");
        else errors.Should().BeEmpty();
    }

    // Форма parallel-cycle (разрез 2 §1.10) проходит валидатор целиком, включая перебор снятий.
    // qa-plan и qa делят роль (линии у qa нет) — по правилу G7 (a5d0c76e) обоим обязателен
    // Title, тот же, что в пресете стора.
    [Fact]
    public void ParallelCycle_Валиден() =>
        ValidateStages(
            Stage(TaskRoles.Cut),
            WithId(TaskRoles.Design, "design", presence: WorkflowStagePresence.Optional),
            WithId(TaskRoles.Plan, "plan", ["design"]),
            WithId(TaskRoles.Qa, "qa-plan", ["design"], presence: WorkflowStagePresence.Optional, title: "Проверка плана"),
            WithId(TaskRoles.Step, "step-back", ["plan"], "back"),
            WithId(TaskRoles.Step, "step-front", ["plan"], "front"),
            WithId(TaskRoles.Review, "review", ["step-back", "step-front"]),
            WithId(TaskRoles.Qa, "qa", ["review", "qa-plan"], title: "Проверка"),
            Stage(TaskRoles.Final),
            Stage(TaskRoles.Merge)).Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("always")]
    public void НеизвестныйPresence_Ошибка(string? presence) =>
        ValidateOne(Stage(TaskRoles.Review, presence)).Should().ContainSingle()
            .Which.Should().Contain("неизвестное Presence");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void СпециальностьИлиПерсона_ПоОдной_Проходит(bool withSpecialty, bool withPersonaId)
    {
        var stage = Stage();
        if (withSpecialty) stage.Specialty = PersonaSpecialty.Tester;
        if (withPersonaId) stage.PersonaId = "persona-1";

        ValidateOne(stage).Should().BeEmpty();
    }

    [Fact]
    public void СпециальностьИПерсонаОдновременно_Ошибка()
    {
        var stage = Stage();
        stage.Specialty = PersonaSpecialty.Tester;
        stage.PersonaId = "persona-1";

        ValidateOne(stage).Should().ContainSingle().Which.Should().Contain("взаимоисключающи");
    }

    // c4: лексика ячейки — как у специальности: "tier:*" в ячейке этапа запрещён
    // (общая проверка ModelCellRules из Core, доработка B1a по R-B1a п.1).
    // R-B1a п.5: проверяется ПОЛНЫЙ текст ошибки — с меткой этапа из label, а не фрагмент.
    [Theory]
    [InlineData("TierStrong")]
    [InlineData("TierMedium")]
    [InlineData("TierWeak")]
    public void ЯчейкаЭтапаTierМаршрут_Ошибка(string cellName)
    {
        var stage = Stage();
        typeof(WorkflowStage).GetProperty(cellName)!.SetValue(stage, "tier:strong");

        ValidateOne(stage).Should().ContainSingle().Which.Should().Be(
            "этап «step»: в ячейке уровня не может быть tier:* — уровень уже выбран строкой");
    }

    // c4: белый список политик простоя (строки, не enum).
    [Theory]
    [InlineData("ignore")]
    [InlineData("stop")]
    public void НеизвестнаяПолитикаПростоя_Ошибка(string policy)
    {
        var stage = Stage();
        stage.StallPolicy = policy;

        ValidateOne(stage).Should().ContainSingle().Which.Should().Contain("политика простоя");
    }

    [Theory]
    [InlineData(WorkflowStallPolicies.NudgeAlert)]
    [InlineData(WorkflowStallPolicies.Alert)]
    [InlineData(WorkflowStallPolicies.StopAlert)]
    public void БелыйСписокПолитикПростоя_Проходит(string policy)
    {
        var stage = Stage();
        stage.StallPolicy = policy;

        ValidateOne(stage).Should().BeEmpty();
    }

    // c4: промпт этапа — строго за 4000. R-B1a п.4: граница запинена — 4000 проходит,
    // 4001 краснеет (мутация `>` → `>=` без случая 4000 не красит ни один тест).
    [Theory]
    [InlineData(4000, false)]
    [InlineData(4001, true)]
    public void Промпт_ГраницаЛимита(int length, bool expectError)
    {
        var stage = Stage();
        stage.Prompt = new string('а', length);

        var errors = ValidateOne(stage);
        if (expectError)
            errors.Should().ContainSingle().Which.Should().Contain("длиннее 4000");
        else
            errors.Should().BeEmpty();
    }

    // c3 (И3 «таймаут нельзя обойти»): null/0/отрицательное — дефолт конфига (null),
    // ниже пола — пол, выше потолка — кламп. Мутация «снять верхний кламп» красит
    // ровно случай 999.
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, 5)]
    [InlineData(3, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    [InlineData(240, 240)]
    [InlineData(241, 240)]
    [InlineData(999, 240)]
    public void NormalizeStall_КлампПол(int? minutes, int? expected) =>
        WorkflowRules.NormalizeStall(minutes).Should().Be(expected);

    // R-B1a п.2: контракт валидатора — собрать все ошибки, стор отдаёт их как 400.
    // null-вход (шаблон, список этапов, элемент списка) — ошибка в списке, а не
    // NullReferenceException: исключение уронило бы ручку 500-м вместо валидного 400.
    [Fact]
    public void NullШаблон_Ошибка() =>
        WorkflowRules.Validate(null).Should().ContainSingle().Which.Should().Be("шаблон не задан.");

    [Fact]
    public void NullСписокЭтапов_Ошибка() =>
        WorkflowRules.Validate(new WorkflowTemplate { Name = "тест", Stages = null! })
            .Should().ContainSingle().Which.Should().Contain("список этапов не задан");

    [Fact]
    public void NullЭлементЭтапов_Ошибка_СНомеромИБезИсключения()
    {
        var errors = WorkflowRules.Validate(new WorkflowTemplate
        {
            Name = "тест",
            Stages = [Stage(), null!, Stage(TaskRoles.Merge)],
        });

        // Пустая запись не останавливает разбор: остальные этапы проверяются дальше,
        // метка — порядковый номер (роли у пустой записи нет).
        errors.Should().ContainSingle().Which.Should().Be("этап #2: пустая запись");
    }

    // R-B1a п.3: Presence и StallPolicy триммятся симметрично роли — строка из UI
    // с крающими пробелами — та же запись, а не «неизвестное значение».
    [Theory]
    [InlineData(" on")]
    [InlineData("optional ")]
    [InlineData(" off")]
    public void PresenceСПробелами_Проходит(string presence) =>
        ValidateOne(Stage(TaskRoles.Review, presence)).Should().BeEmpty();

    [Fact]
    public void НесъёмнаяРольPresenceСПробелом_ОшибкаСНормализованнымЗначением() =>
        ValidateOne(Stage(TaskRoles.Final, " off")).Should().ContainSingle().Which
            .Should().Contain("роль несъёмная, Presence «off» запрещён");

    [Theory]
    [InlineData(" nudge-alert")]
    [InlineData("alert ")]
    public void ПолитикаПростояСПробелами_Проходит(string policy)
    {
        var stage = Stage();
        stage.StallPolicy = policy;

        ValidateOne(stage).Should().BeEmpty();
    }

    // ─── G7 (решение контракта a5d0c76e по возврату R-C3a): имена этапов общей роли ──────────
    // Исходная проблема: qa-plan и qa в «Параллельном цикле» делят роль → граф показывал две
    // неотличимые «Проверки». Правило: у этапов с общей парой (Role, Lane) Title обязателен
    // у каждого и различен в группе без учёта регистра; вне группы — необязателен.

    [Fact]
    public void ДваЭтапаОднойРолиБезИмени_ОшибкаУКаждого()
    {
        var errors = ValidateStages(WithId(TaskRoles.Qa, "qa1"), WithId(TaskRoles.Qa, "qa2", after: ["qa1"]));

        // Роль повторяется, Title нет — метка «роль (id)»: ошибок две, по одной на этап, различимы.
        errors.Should().Equal(
            "этап «qa» (qa1): этап делит роль и линию с другим этапом — имя (Title) обязательно.",
            "этап «qa» (qa2): этап делит роль и линию с другим этапом — имя (Title) обязательно.");
    }

    [Fact]
    public void ДваЭтапаОднойРолиСОдинаковымиИменами_Ошибка()
    {
        // Без учёта регистра и с обрезкой краев: «Проверка» и « проверка » — одно имя.
        var errors = ValidateStages(
            WithId(TaskRoles.Qa, "qa1", title: "Проверка"),
            WithId(TaskRoles.Qa, "qa2", after: ["qa1"], title: " проверка "));

        // Ошибка адресована второму этапу и называет его имя после обрезки («проверка» —
        // трим делает Normalize).
        errors.Should().ContainSingle().Which.Should().Be(
            "этап «проверка»: имя «проверка» повторяет имя другого этапа той же роли и линии.");
    }

    [Fact]
    public void ИмяСЗакрывающейКавычкой_УдваиваетсяВМеткеОшибки()
    {
        // «»» внутри Title удваивается — фронт разбирает метку однозначно (одно правило на обе стороны).
        var errors = ValidateStages(
            WithId(TaskRoles.Qa, "qa1", title: "a»b"),
            WithId(TaskRoles.Qa, "qa2", after: ["qa1"], title: " A»B "));

        errors.Should().ContainSingle().Which.Should().Be(
            "этап «A»»B»: имя «A»B» повторяет имя другого этапа той же роли и линии.");
    }

    [Fact]
    public void ДваЭтапаОднойРолиСРазнымиИменами_Проходит()
    {
        ValidateStages(
            WithId(TaskRoles.Qa, "qa-plan", title: "Проверка плана"),
            WithId(TaskRoles.Qa, "qa", after: ["qa-plan"], title: "Проверка")).Should().BeEmpty();
    }

    [Fact]
    public void ИмяТолькоИзПробелов_КакБезИмени_Ошибка()
    {
        var errors = ValidateStages(
            WithId(TaskRoles.Qa, "qa1", title: "  "),
            WithId(TaskRoles.Qa, "qa2", after: ["qa1"], title: "Проверка"));

        errors.Should().ContainSingle().Which.Should().Be(
            "этап «qa» (qa1): этап делит роль и линию с другим этапом — имя (Title) обязательно.");
    }

    // Вне группы Title не обязателен и не мешает: одиночный этап той же роли проходит без него.
    [Fact]
    public void ОдиночныйЭтапБезИмени_TitleНеТребует() =>
        ValidateStages(WithId(TaskRoles.Qa, "qa")).Should().BeEmpty();

    // Этапы одной роли, но разных линий (step back/front) — разные группы: имена не нужны.
    [Fact]
    public void ОднаРольРазныеЛинии_ГруппыНет_ИмяНеТребует() =>
        ValidateStages(
            WithId(TaskRoles.Plan, "plan"),
            WithId(TaskRoles.Step, "s-back", after: ["plan"], lane: "back"),
            WithId(TaskRoles.Step, "s-front", after: ["plan"], lane: "front"),
            WithId(TaskRoles.Review, "review", after: ["s-back", "s-front"])).Should().BeEmpty();

    // Граница длины имени (тот же порядок, что у промпта): 40 проходит, 41 краснеет.
    [Theory]
    [InlineData(40, false)]
    [InlineData(41, true)]
    public void ИмяЭтапа_ГраницаЛимита(int length, bool expectError)
    {
        var stage = Stage();
        stage.Title = new string('а', length);

        var errors = ValidateOne(stage);
        if (expectError)
            errors.Should().ContainSingle().Which.Should().Contain("длиннее 40");
        else
            errors.Should().BeEmpty();
    }
}
