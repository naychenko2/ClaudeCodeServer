using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Чистые предикторы DossierCaptureService — две критичные развилки ADR-004, вынесенные из
// ProcessCommitAsync ради тестируемости без тяжёлых зависимостей (SessionManager в hosted-сервисе
// не мокается — проектный паттерн). Покрывают critical-правку Глеба №1 (guard принадлежности)
// и major-правку №2 (правило переякорения при squash).
public class DossierCaptureLogicTests
{
    // §1 guard: трейлер принимается ТОЛЬКО если сессия принадлежит тому же владельцу и проекту.
    // Без этого одна папка, подключённая двумя владельцами, дырявит per-owner изоляцию: оба
    // capture-сервиса видят один коммит с трейлером чата A, и сервис B затянул бы реплики чужого чата.
    public static IEnumerable<object[]> GuardCases() => new[]
    {
        new object[] { "ownerA", "proj1", "ownerA", "proj1", true,  "свой чат — паспорт создаётся" },
        new object[] { "ownerB", "proj1", "ownerA", "proj1", false, "общая папка: чужой владелец — паспорта нет" },
        new object[] { "ownerA", "proj2", "ownerA", "proj1", false, "чужой проект — паспорта нет" },
        new object[] { null!, "proj1", "ownerA", "proj1", false, "глобальный/личный чат (OwnerId пуст) — fail-closed" },
        new object[] { "ownerA", null!, "ownerA", "proj1", false, "сессия без проекта — fail-closed" },
    };

    [Theory]
    [MemberData(nameof(GuardCases))]
    public void GuardПринадлежности(string? sessionOwner, string? sessionProject,
        string projectOwner, string projectId, bool expected, string _)
    {
        DossierCaptureService.SessionBelongsToProject(sessionOwner, sessionProject, projectOwner, projectId)
            .Should().Be(expected);
    }

    // §6 opt-out: тумблер «Не сохранять решения из этого чата». Коммит из чата с opt-out → паспорта
    // нет; коммит из того же проекта без opt-out → паспорт есть. Композит с guard принадлежности.
    public static IEnumerable<object[]> OptOutCases() => new[]
    {
        new object[] { true,  false, true,  "свой чат, opt-out выкл — паспорт создаётся" },
        new object[] { true,  true,  false, "opt-out вкл — паспорта нет, даже если чат наш" },
        new object[] { false, true,  false, "чужой чат + opt-out — паспорта нет" },
        new object[] { false, false, false, "чужой чат без opt-out — паспорта нет (guard принадлежности)" },
    };

    [Theory]
    [MemberData(nameof(OptOutCases))]
    public void OptOutЧата_БлокируетЗахват(bool belongsToProject, bool optedOut, bool expected, string _)
    {
        DossierCaptureService.ShouldCaptureSession(belongsToProject, optedOut).Should().Be(expected);
    }

    // §7: переякорение — ОБА условия вместе (subject старого в новом сообщении И старый sha
    // недостижим). Одной недостижимости мало (коммит в невлитой ветке), одного subject-матча
    // мало (amend без переписи предков).
    [Fact]
    public void Переякорение_SubjectМатчитИНедостижим_True()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: true, oldReachable: false)
            .Should().BeTrue("squash: subject старого в новом, старый sha недостижим");
    }

    [Fact]
    public void Переякорение_СтарыйДостижим_False_ОбычныйКоммит()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: true, oldReachable: true)
            .Should().BeFalse("старый sha достижим — это обычный второй коммит той же сессии, не перепись");
    }

    [Fact]
    public void Переякорение_SubjectНеМатчит_False_ДажеЕслиНедостижим()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: false, oldReachable: false)
            .Should().BeFalse("недостижим, но subject не упоминается — коммит в невлитой ветке, не squash");
    }

    [Fact]
    public void Переякорение_ОбаНет_False()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: false, oldReachable: true).Should().BeFalse();
    }

    // §7 + MAJOR-фикс ревью: несколько похожих subject'ов при squash не должны дать несколько
    // записей на одном commitSha (инвариант «один коммит — один паспорт»). Subject матчится как
    // ОТДЕЛЬНАЯ строка тела (не подстрока), и среди сматченных выбирается один — самый длинный
    // (специфичный). На нём ProcessCommit и переякоривает ровно одно досье (break/return).
    [Fact]
    public void Переякорение_НесколькоПохожихSubject_ВыбираетСамоеУзкое()
    {
        // Все три subject-а после squash выложены в теле отдельными строками — line-match всех
        // трёх, но переякорение уходит на самый специфичный (длиннейший).
        var msg = "squash: fix bug in x/y\n\nfix bug\nfix bug in x\nfix bug in x/y";

        var best = DossierCaptureService.PickReanchorSubject(
            ["fix bug", "fix bug in x", "fix bug in x/y"], msg);

        best.Should().Be("fix bug in x/y");
    }

    // Сердце MAJOR-фикса: Contains без границ ловил бы «fix bug» внутри «fix bug in x/y». Line-match
    // (отдельная строка) эту ложную подстроку отбрасывает — subject, присутствующий только как часть
    // более длинной строки, не матчится.
    [Fact]
    public void Переякорение_SubjectТолькоПодстрока_НеМатчится()
    {
        var msg = "fix bug in x/y\n\nтут тело без нужной строки";

        DossierCaptureService.PickReanchorSubject(["fix bug"], msg).Should().BeNull(
            "«fix bug» присутствует лишь внутри «fix bug in x/y», не отдельной строкой");
        DossierCaptureService.PickReanchorSubject(["fix bug in x"], msg).Should().BeNull();
    }

    [Fact]
    public void Переякорение_ОдинSubjectСтрокой_Матчится()
    {
        var msg = "новый коммит\n\nfix bug\nчто-то ещё";

        DossierCaptureService.PickReanchorSubject(["fix bug"], msg).Should().Be("fix bug");
    }

    [Fact]
    public void Переякорение_ПустойСписокИлиСообщение_Null()
    {
        DossierCaptureService.PickReanchorSubject([], "любое сообщение").Should().BeNull();
        DossierCaptureService.PickReanchorSubject(["fix bug"], "").Should().BeNull();
    }

    // --- Окно реплик по времени коммита (блокер «зачем про чужое дело») ---

    private const long Min = 60_000L;
    private const long Hr = 3_600_000L;
    private static readonly DossierCaptureService.CommitWindowOptions WOpts =
        new(2 * Hr, 15 * Min, 30 * Min);   // before=2ч, after=15мин, gap=30мин — как в сервисе

    private static StoredUserMessage User(string text, long ts) => new(text, timestamp: ts);
    private static StoredTextMessage Ai(string text, long ts) => new(text, timestamp: ts);

    // ПРОД-БАГ: после коммита (дело A) чат продолжился другим делом (B), и «последние N реплик»
    // подобрали свежее B. Окно вокруг коммита должно взять только A. Здесь коммит в конце дела A,
    // а дело B — спустя 3 часа, заведомо за пределами after-окна.
    [Fact]
    public void Окно_КоммитВДелеА_НеБерётБолееСвежееДелоВ()
    {
        var t0 = 1_700_000_000_000L;
        var msgs = new List<StoredMessage>
        {
            User("сделать выбор персоны", t0),            // дело A
            Ai("добавил экран выбора", t0 + Min),
            User("починить профиль CLI", t0 + 3 * Hr),    // дело B (спустя 3 ч)
            Ai("починил профиль", t0 + 3 * Hr + Min),
        };

        var window = DossierCaptureService.SelectCommitWindow(msgs, t0 + Min + 30_000, WOpts);

        window.Should().HaveCount(2, "коммит в деле A — берём только его кластер, не свежее дело B");
        window.OfType<StoredTextMessage>().Should().NotContain(m => m.Text.Contains("профиль"));
    }

    // Обратная сторона: коммит в деле B не должен затянуть предшествующее дело A, даже если оно
    // уложилось в before-окно по времени. Расширение назад останавливается на паузе > gap.
    [Fact]
    public void Окно_КоммитВДелеВ_НеБерётПредыдущееДелоА_ПаузаБольшеСGap()
    {
        var t0 = 1_700_000_000_000L;
        var msgs = new List<StoredMessage>
        {
            User("выбор персоны", t0),                     // дело A
            Ai("экран выбора", t0 + Min),
            User("профиль CLI", t0 + 3 * Hr),              // дело B (пауза 3 ч > gap)
            Ai("починил профиль", t0 + 3 * Hr + Min),
        };

        var window = DossierCaptureService.SelectCommitWindow(msgs, t0 + 3 * Hr + 90_000, WOpts);

        window.Should().HaveCount(2, "кластер дела B; пауза между делами > gap отсекает дело A");
        window.OfType<StoredTextMessage>().Should().NotContain(m => m.Text.Contains("выбора"));
    }

    // Реплики одного хода идут подряд (паузы < gap) — все входят в кластер.
    [Fact]
    public void Окно_РепликиОдногоХодаВместе()
    {
        var t0 = 1_700_000_000_000L;
        var msgs = new List<StoredMessage>
        {
            User("v1", t0),
            Ai("a1", t0 + Min),
            User("v2", t0 + 5 * Min),
            Ai("a2", t0 + 6 * Min),
        };

        var window = DossierCaptureService.SelectCommitWindow(msgs, t0 + 6 * Min + 30_000, WOpts);

        window.Should().HaveCount(4, "все реплики хода в пределах gap — один кластер");
    }

    // Нетаймштампные сообщения (tool_use, file_changed) между репликами кластера входят в выжимку
    // как контекст — у них нет Timestamp, но они попадают в диапазон индексов кластера.
    [Fact]
    public void Окно_ВключаетНетаймштампныеСообщенияМеждуРепликами()
    {
        var t0 = 1_700_000_000_000L;
        var tool = new StoredToolUseMessage { Name = "Read" };
        var msgs = new List<StoredMessage>
        {
            User("поехали", t0),
            tool,
            Ai("готово", t0 + Min),
        };

        var window = DossierCaptureService.SelectCommitWindow(msgs, t0 + 30_000, WOpts);

        window.Should().Contain(tool, "tool_use между репликами — контекст хода, входит в кластер");
    }

    [Fact]
    public void Окно_НетTimestamp_ВозвращеноПусто()
    {
        var msgs = new List<StoredMessage>
        {
            new StoredToolUseMessage { Name = "Read" },   // ни у кого нет Timestamp — не привязаться
        };

        DossierCaptureService.SelectCommitWindow(msgs, 1_700_000_000_000L, WOpts)
            .Should().BeEmpty("без временных меток привязка к коммиту невозможна — лучше пусто, чем весь чат");
    }

    [Fact]
    public void Окно_КоммитДалекоОтРеплик_ВозвращеноПусто()
    {
        var t0 = 1_700_000_000_000L;
        var msgs = new List<StoredMessage> { User("старое", t0) };

        // Коммит через 10 часов — все реплики вне окна.
        DossierCaptureService.SelectCommitWindow(msgs, t0 + 10 * Hr, WOpts)
            .Should().BeEmpty();
    }

    // --- Фильтр захвата (объём/стоимость) ---

    private static readonly HashSet<string> DefSkip = ["style", "chore", "build", "ci"];

    [Theory]
    [InlineData("chore: обновить зависимости", true,  "тип chore — пропуск")]
    [InlineData("style: форматирование", true,  "тип style — пропуск")]
    [InlineData("build(ci): пайплайн", true,  "тип build — пропуск")]
    [InlineData("ci: линтер", true,  "тип ci — пропуск")]
    [InlineData("feat: новая фича", false, "тип feat — снимаем паспорт")]
    [InlineData("fix: баг", false, "тип fix — снимаем")]
    [InlineData("refactor: переименовать", false, "тип refactor — снимаем")]
    [InlineData("merge: ветка", false, "merge не в skip-списке")]
    public void Фильтр_ПоТипуКоммита(string subject, bool skip, string _)
    {
        // filesCount большой — чтобы правило однофайловости не вмешивалось
        DossierCaptureService.ShouldSkipCommit(subject, "", 5, DefSkip, 100).Should().Be(skip);
    }

    [Fact]
    public void Фильтр_ОднофайловоеКороткоеСообщение_Пропуск()
    {
        DossierCaptureService.ShouldSkipCommit("fix: опечатка", "", 1, DefSkip, 100)
            .Should().BeTrue("однофайловая правка с коротким сообщением — выжимать нечего");
    }

    [Fact]
    public void Фильтр_ОднофайловоеДлинноеСообщение_Снимаем()
    {
        var body = new string('x', 120);   // сообщение длиннее порога — содержательное
        DossierCaptureService.ShouldSkipCommit("fix: поправить опечатку и обновить ссылку", body, 1, DefSkip, 100)
            .Should().BeFalse("однофайлово, но сообщение содержательное — паспорт нужен");
    }

    [Fact]
    public void Фильтр_МногофайловоеКороткоеСообщение_Снимаем()
    {
        DossierCaptureService.ShouldSkipCommit("fix: баг", "", 3, DefSkip, 100)
            .Should().BeFalse("несколько файлов — правило однофайловости не действует, тип fix не в skip");
    }

    // Служебные трейлеры не должны спасать короткое сообщение от фильтра: их длина — не содержание.
    [Fact]
    public void Фильтр_ТрейлерыНеСчитаютсяВДлину()
    {
        var body = "CCS-Session: a7d10551-abcd-1234-5678-aaaaaaaaaaaa\nCo-Authored-By: Claude <noreply@anthropic.com>";
        DossierCaptureService.ShouldSkipCommit("fix: опечатка", body, 1, DefSkip, 100)
            .Should().BeTrue("без трейлеров сообщение короче порога — однофайловое короткое пропускается");
    }

    [Theory]
    [InlineData("feat(scope): добавить X", "feat")]
    [InlineData("feat!: breaking change", "feat")]
    [InlineData("feat: добавить", "feat")]
    [InlineData("chore: bump", "chore")]
    [InlineData("merge: ветка", "merge")]
    [InlineData("без конвенции", null)]
    [InlineData("", null)]
    public void ПарсингТипа(string subject, string? expected)
    {
        DossierCaptureService.ParseConventionalType(subject).Should().Be(expected);
    }
}
