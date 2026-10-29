using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

public class SessionManagerCrudTests : SessionManagerTestBase
{
    // --- GetByProject ---

    [Fact]
    public void GetByProject_NewProject_ReturnsEmpty()
    {
        var dir = MkProjectDir("empty");
        var project = _projectManager.Create("Empty", dir, TestUserId, TestUsername);

        var result = _sut.GetByProject(project.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByProject_AfterCreate_ReturnsSessions()
    {
        var dir = MkProjectDir("a");
        var project = _projectManager.Create("A", dir, TestUserId, TestUsername);

        await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        await _sut.CreateAsync(project.Id, ClaudeMode.Plan);

        var result = _sut.GetByProject(project.Id);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(s => s.ProjectId.Should().Be(project.Id));
    }

    [Fact]
    public async Task GetByProject_FiltersByProjectId()
    {
        var dir1 = MkProjectDir("p1"); var p1 = _projectManager.Create("P1", dir1, TestUserId, TestUsername);
        var dir2 = MkProjectDir("p2"); var p2 = _projectManager.Create("P2", dir2, TestUserId, TestUsername);

        await _sut.CreateAsync(p1.Id, ClaudeMode.Auto);
        await _sut.CreateAsync(p1.Id, ClaudeMode.Auto);
        await _sut.CreateAsync(p2.Id, ClaudeMode.Auto);

        _sut.GetByProject(p1.Id).Should().HaveCount(2);
        _sut.GetByProject(p2.Id).Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByProject_OrderedByUpdatedAtDescending()
    {
        var dir = MkProjectDir("ord");
        var project = _projectManager.Create("Ord", dir, TestUserId, TestUsername);

        var s1 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var s2 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        // Симулируем что s1 — более свежая (например, пользователь только что в ней работал)
        s1.UpdatedAt = DateTime.UtcNow.AddMinutes(5);

        var result = _sut.GetByProject(project.Id).ToList();

        result[0].Id.Should().Be(s1.Id, "s1 имеет более поздний UpdatedAt");
        result[1].Id.Should().Be(s2.Id);
    }

    // --- GetById ---

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        _sut.GetById("does-not-exist").Should().BeNull();
    }

    [Fact]
    public async Task GetById_ExistingSession_ReturnsSession()
    {
        var dir = MkProjectDir("gb");
        var project = _projectManager.Create("GB", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var found = _sut.GetById(session.Id);

        found.Should().NotBeNull();
        found!.Id.Should().Be(session.Id);
    }

    // --- CreateAsync ---

    [Fact]
    public async Task CreateAsync_ValidProject_ReturnsSession()
    {
        var dir = MkProjectDir("cr");
        var project = _projectManager.Create("CR", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Plan);

        session.ProjectId.Should().Be(project.Id);
        session.Mode.Should().Be(ClaudeMode.Plan);
        session.Status.Should().Be(SessionStatus.Starting);
        session.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateAsync_WithName_SessionHasName()
    {
        var dir = MkProjectDir("nm");
        var project = _projectManager.Create("NM", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, null, "Мой чат");

        session.Name.Should().Be("Мой чат");
    }

    // --- Update: PATCH-семантика (null = не трогать) ---

    [Fact]
    public async Task Update_NullFields_KeepExistingNameModelEffort()
    {
        var dir = MkProjectDir("upd");
        var project = _projectManager.Create("UPD", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Имя", model: "opus", effort: "high");

        // Частичный апдейт (как togglePin/chats_update): все поля null — ничего не затирается
        var updated = await _sut.UpdateAsync(session.Id, TestUserId, name: null, model: null, effort: null);

        updated!.Name.Should().Be("Имя");
        updated.Model.Should().Be("opus");
        updated.Effort.Should().Be("high");
    }

    [Fact]
    public async Task Update_OnlyName_DoesNotWipeModel()
    {
        var dir = MkProjectDir("upd2");
        var project = _projectManager.Create("UPD2", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Старое", model: "opus", effort: "high");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, name: "Новое", model: null, effort: null);

        updated!.Name.Should().Be("Новое");
        updated.Model.Should().Be("opus", "модель не передавалась — не трогаем");
        updated.Effort.Should().Be("high");
    }

    // Владение проектной сессией — на нём держится гейт делегированных ходов
    [Fact]
    public async Task ПроектнаяСессия_ВладелецРезолвитсяЧерезПроект()
    {
        // У проектной сессии Session.OwnerId остаётся null — владелец живёт у проекта.
        // Кто сравнивает OwnerId напрямую, молча получает «чужую» сессию: так
        // GetActiveTurnDelegation сперва отключил запрет запуска исполнителя на
        // делегированном ходу (поймано live-тестом). Владение — только через GetOwned.
        var dir = MkProjectDir("owner");
        var project = _projectManager.Create("Owner", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        session.OwnerId.Should().BeNull("владелец проектной сессии хранится у проекта");
        _sut.GetOwned(session.Id, TestUserId).Should().NotBeNull();
        _sut.GetOwned(session.Id, "another-user").Should().BeNull();

        // Гейт спрашивает состояние хода через тот же резолв владельца: сессия без живого
        // процесса — обычный ход; к чужой сессии запрет не применяется
        _sut.GetActiveTurnDelegation(session.Id, TestUserId)
            .Should().Be(new ClaudeHomeServer.Services.Llm.TurnDelegationState(0, false));
        _sut.GetActiveTurnDelegation(session.Id, "another-user")
            .Should().Be(new ClaudeHomeServer.Services.Llm.TurnDelegationState(0, false));
    }

    // --- MarkRead: отметка прочтения (синк непрочитанности между устройствами) ---

    [Fact]
    public async Task MarkRead_УстанавливаетLastReadAt_НеТрогаяUpdatedAt()
    {
        var dir = MkProjectDir("mr");
        var project = _projectManager.Create("MR", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var updatedBefore = session.UpdatedAt;

        _sut.MarkRead(session.Id, TestUserId).Should().BeTrue();

        session.LastReadAt.Should().NotBeNull();
        // Инвариант conventions.md: отметка прочтения — настройка, не активность,
        // иначе чат прыгал бы в списке и сам метил себя непрочитанным
        session.UpdatedAt.Should().Be(updatedBefore, "MarkRead не должен двигать UpdatedAt");
        session.LastReadAt.Should().BeOnOrAfter(updatedBefore);
    }

    [Fact]
    public async Task MarkRead_ПовторнаяОтметка_НеПереписываетСвежую()
    {
        var dir = MkProjectDir("mr2");
        var project = _projectManager.Create("MR2", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.MarkRead(session.Id, TestUserId);
        var first = session.LastReadAt;

        // LastReadAt уже >= UpdatedAt → идемпотентный ранний выход без перезаписи
        _sut.MarkRead(session.Id, TestUserId).Should().BeTrue();
        session.LastReadAt.Should().Be(first, "повторная отметка при свежей — no-op");

        // Новая активность двигает UpdatedAt вперёд — следующая отметка снова пишет
        session.UpdatedAt = DateTime.UtcNow.AddMinutes(1);
        _sut.MarkRead(session.Id, TestUserId).Should().BeTrue();
        session.LastReadAt.Should().BeAfter(first!.Value);
    }

    // B10: штатная остановка (выкатка = перезапуск) не теряет отложенную запись MarkRead.
    // Дебаунс-таймер (0,2с) ещё не сработал, а KillAllProcesses сбрасывает запись синхронно:
    // sessions.json получает отметку, UpdatedAt при этом не сдвигается (служебная отметка).
    [Fact]
    public void MarkRead_ОтложеннаяЗапись_СбрасываетсяСинхронно_ПриОстановке()
    {
        var sut = BuildDebounceManager();
        var updatedBefore = DateTime.UtcNow.AddMinutes(-1);
        var session = new Session { ProjectId = null, OwnerId = TestUserId, UpdatedAt = updatedBefore };
        sut.AddSessionForTest(session);

        // MarkRead: in-memory + one-shot таймер, на диск запись ещё не ушла
        sut.MarkRead(session.Id, TestUserId).Should().BeTrue();

        // Немедленная штатная остановка: висящий таймер обязан сбросить запись синхронно
        sut.KillAllProcesses();

        var path = Path.Combine(_tempDir, "sessions.json");
        File.Exists(path).Should().BeTrue("отложенная запись MarkRead должна уехать на диск при остановке");
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var stored = JsonSerializer.Deserialize<List<Session>>(File.ReadAllText(path), opts)!
            .Single(s => s.Id == session.Id);
        stored.LastReadAt.Should().Be(session.LastReadAt, "sessions.json содержит отметку прочитанности");
        stored.UpdatedAt.Should().Be(updatedBefore, "отложенная запись MarkRead не двигает UpdatedAt");
    }

    // B10 (гонка): сброс при остановке в окне между срабатыванием one-shot таймера и взятием
    // им замка. Без фикса колбэк таймера гасил _pendingSaveTimer безусловно, затёрв таймер
    // НОВОЙ отметки; следующий FlushPendingSave его не находил, и вторая отметка терялась.
    // С фиксом колбэк гасит поле только если оно указывает на ЭТОТ таймер (ReferenceEquals),
    // и сработавший one-shot освобождает себя (Dispose) — чужой таймер переживает.
    //
    // Детерминированный сценарий (все шаги на main-потоке, порядок захвата замка фиксирован):
    //   1. MarkRead1 → T1 (200мс).
    //   2. Sleep(201) → T1 срабатывает, колбэк в thread pool (ждёт _debounceLock).
    //   3. KillAllProcesses → FlushPendingSave: main-поток забирает _debounceLock ПЕРВЫМ,
    //      Dispose T1, SaveSessions. Колбэк T1 (thread pool) ждёт освобождения замка.
    //   4. MarkRead2 → T2: main-поток снова забирает _debounceLock (колбэк T1 всё ещё ждёт),
    //      создаёт T2, _pendingSaveTimer = T2.
    //   5. Sleep: колбэк T1 (thread pool) забирает _debounceLock. Без фикса гасит поле
    //      (затёрв T2); с фиксом ReferenceEquals(T2, T1) = false → не трогает.
    //   6. KillAllProcesses → FlushPendingSave: с фиксом T2 жив → Dispose + SaveSessions
    //      (вторая отметка на диске). Без фикса T2 затёрт → no-op, на диске первая отметка.
    [Fact]
    public void MarkRead_ГонкаСбросаПриОстановке_ВтораяОтметкаНеТеряется()
    {
        var sut = BuildDebounceManager();
        var updatedBefore = DateTime.UtcNow.AddMinutes(-1);
        var session = new Session { ProjectId = null, OwnerId = TestUserId, UpdatedAt = updatedBefore };
        sut.AddSessionForTest(session);

        // 1. Первая отметка: one-shot таймер T1 (200мс)
        sut.MarkRead(session.Id, TestUserId).Should().BeTrue();

        // 2. T1 срабатывает; колбэк в thread pool, ждёт _debounceLock
        System.Threading.Thread.Sleep(210);

        // 3. Подготовка: обнуляем LastReadAt ДО сброса, чтобы шаг 4 был минимальным
        session.LastReadAt = DateTime.MinValue;

        // 4. Сброс: main-поток забирает _debounceLock, Dispose T1, SaveSessions
        sut.KillAllProcesses();

        // 5. Новая отметка: main-поток сразу забирает _debounceLock (колбэк T1 ждёт),
        //    создаёт T2. _pendingSaveTimer = T2.
        sut.MarkRead(session.Id, TestUserId).Should().BeTrue();
        var secondRead = session.LastReadAt;
        secondRead.Should().NotBe(session.UpdatedAt, "вторая отметка имеет новое время");

        // 6. Колбэк T1 (thread pool) забирает _debounceLock.
        //    Без фикса: _pendingSaveTimer = null (затёрв T2). С фиксом: ReferenceEquals → no-op.
        System.Threading.Thread.Sleep(100);

        // 7. Финальный сброс: FlushPendingSave. С фиксом T2 жив → Dispose + SaveSessions.
        sut.KillAllProcesses();

        // 7. Вторая отметка на диске
        var path = Path.Combine(_tempDir, "sessions.json");
        var stored = JsonSerializer.Deserialize<List<Session>>(File.ReadAllText(path),
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!
            .Single(s => s.Id == session.Id);
        stored.LastReadAt.Should().Be(secondRead, "вторая отметка прочитанности дописана, а не потеряна");
    }

    // --- CommittedFilePaths: пометки «правки чата уже зафиксированы в git» ---

    [Fact]
    public async Task MarkFilesCommitted_ПишетПути_ПереживаетПерезагрузку_НеТрогаетUpdatedAt()
    {
        var dir = MkProjectDir("cfp");
        var project = _projectManager.Create("CFP", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.UpdatedAt = DateTime.UtcNow.AddDays(-2);
        var updatedBefore = session.UpdatedAt;

        _sut.MarkFilesCommitted([(session.Id, ["src/a.ts"], ["src/a.ts", "src/b.ts"])]);

        session.CommittedFilePaths.Should().BeEquivalentTo(["src/a.ts"]);
        // Инвариант conventions.md: фиксация правок — не активность чата
        session.UpdatedAt.Should().Be(updatedBefore, "пометка не должна двигать UpdatedAt");
        // Значение доехало до стора (переживёт перезагрузку): sessions.json лежит рядом
        // с projects.json (DataPath) и сериализуется сразу в MarkFilesCommitted
        var sessionsJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "sessions.json"));
        sessionsJson.Should().Contain("src/a.ts");
    }

    // Bulk на несколько чатов — один вызов, каждый получает свои пути (а SaveSessions
    // структурно зовётся один раз на пачку — см. MarkFilesCommitted)
    [Fact]
    public async Task MarkFilesCommitted_Bulk_КаждыйЧатПолучаетСвоиПути()
    {
        var dir = MkProjectDir("cfp-bulk");
        var project = _projectManager.Create("CFPB", dir, TestUserId, TestUsername);
        var s1 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var s2 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.MarkFilesCommitted(
        [
            (s1.Id, ["src/a.ts"], ["src/a.ts"]),
            (s2.Id, ["src/b.ts"], ["src/b.ts", "src/c.ts"]),
        ]);

        s1.CommittedFilePaths.Should().BeEquivalentTo(["src/a.ts"]);
        s2.CommittedFilePaths.Should().BeEquivalentTo(["src/b.ts"]);
    }

    // Гигиена: пометки, выпавшие из сырого множества чата (история переписана),
    // вычищаются при следующей пометке — стор не копит мусор
    [Fact]
    public async Task MarkFilesCommitted_ЧиститПометкиВнеСырогоМножества()
    {
        var dir = MkProjectDir("cfp-prune");
        var project = _projectManager.Create("CFPP", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.MarkFilesCommitted([(session.Id, ["src/old.ts"], ["src/old.ts"])]);

        // Новый коммит: src/old.ts из сырого множества выпал — пометка уходит вместе с ним
        _sut.MarkFilesCommitted([(session.Id, ["src/new.ts"], ["src/new.ts"])]);

        session.CommittedFilePaths.Should().BeEquivalentTo(["src/new.ts"]);
    }

    // Инвариант «no-op не пишет стор»: повторная пометка тех же путей не меняет
    // sessions.json на диске (часть инварианта «один SaveSessions на пачку и ни одного
    // при no-op»; число вызовов SaveSessions на пачку снаружи не наблюдаемо — оно
    // обеспечено структурой MarkFilesCommitted: единственный вызов в конце)
    [Fact]
    public async Task MarkFilesCommitted_NoOp_НеПишетСтор()
    {
        var dir = MkProjectDir("cfp-noop");
        var project = _projectManager.Create("CFPN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.MarkFilesCommitted([(session.Id, ["src/a.ts"], ["src/a.ts"])]);
        var storePath = Path.Combine(_tempDir, "sessions.json");
        var before = await File.ReadAllTextAsync(storePath);

        _sut.MarkFilesCommitted([(session.Id, ["src/a.ts"], ["src/a.ts"])]);

        (await File.ReadAllTextAsync(storePath)).Should().Be(before, "no-op не должен переписывать стор");
        session.CommittedFilePaths.Should().BeEquivalentTo(["src/a.ts"]);
    }

    // --- Снятие пометки на живом ходе (OnMessageAsync): обе ветки источников Extract ---

    // Правка write-инструментом снимает пометку: file_changed НЕ хватает (не приходит при
    // неизменном содержимом, отсечке watcher'а или удалении файла)
    [Fact]
    public async Task OnMessage_ToolUseWriteTool_СнимаетПометкуCommitted()
    {
        var dir = MkProjectDir("cfp-tool");
        var project = _projectManager.Create("CFPT", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.MarkFilesCommitted([(session.Id, ["a.ts"], ["a.ts"])]);

        var input = JsonDocument.Parse(
            JsonSerializer.Serialize(new { file_path = Path.Combine(dir, "a.ts") })).RootElement;
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ToolUseMessage("t1", "Edit", input));

        session.CommittedFilePaths.Should().BeEmpty("новая правка возвращает путь в атрибуцию");
    }

    // Инструмент вне белого списка правок пометку не трогает
    [Fact]
    public async Task OnMessage_ToolUseНеWriteTool_НеСнимаетПометку()
    {
        var dir = MkProjectDir("cfp-read");
        var project = _projectManager.Create("CFPR", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.MarkFilesCommitted([(session.Id, ["a.ts"], ["a.ts"])]);

        var input = JsonDocument.Parse(
            JsonSerializer.Serialize(new { file_path = Path.Combine(dir, "a.ts") })).RootElement;
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ToolUseMessage("t1", "Read", input));

        session.CommittedFilePaths.Should().BeEquivalentTo(["a.ts"]);
    }

    // file_changed снимает пометку и для External=true: сырое множество индекса содержит
    // внешние правки (фильтр «только файлы чата»), значит и возврат пути работает для них
    [Fact]
    public async Task OnMessage_FileChangedExternal_СнимаетПометку()
    {
        var dir = MkProjectDir("cfp-fc");
        var project = _projectManager.Create("CFPF", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.MarkFilesCommitted([(session.Id, ["a.ts"], ["a.ts"])]);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new FileChangedMessage("a.ts", 1, 0, External: true));

        session.CommittedFilePaths.Should().BeEmpty();
    }

    // Гейт worktree-хода (зеркало SessionChangedPaths.Extract): правка хода в чужом дереве
    // пометку с путей КОРНЯ не снимает — watcher отдаёт пути относительно СВОЕГО корня,
    // и одноимённый файл в worktree воскресил бы атрибуцию файла корня
    [Fact]
    public async Task OnMessage_ХодВWorktree_НеСнимаетПометку()
    {
        var dir = MkProjectDir("cfp-wt");
        var project = _projectManager.Create("CFPW", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.MarkFilesCommitted([(session.Id, ["a.ts"], ["a.ts"])]);
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await InvokeOnMessageAsync(session.Id, acc,
            new SessionStartedMessage("cfp-wt-claude", false, "model", "auto",
                TurnWorktree: new TurnWorktreeInfo(Path.Combine(dir, ".claude", "wt"), "wt")));
        await InvokeOnMessageAsync(session.Id, acc, new FileChangedMessage("a.ts", 1, 0));
        session.CommittedFilePaths.Should().BeEquivalentTo(["a.ts"], "правка в worktree корень не трогает");

        // Следующий ход в основном дереве (session_started без worktree) снова снимает
        await InvokeOnMessageAsync(session.Id, acc,
            new SessionStartedMessage("cfp-wt-claude", false, "model", "auto"));
        await InvokeOnMessageAsync(session.Id, acc, new FileChangedMessage("a.ts", 1, 0));
        session.CommittedFilePaths.Should().BeEmpty();
    }

    [Fact]
    public async Task UnmarkFileCommitted_УбираетПуть_БезПометкиРаннийВыход()
    {
        var dir = MkProjectDir("cfp-unmark");
        var project = _projectManager.Create("CFPU", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.UpdatedAt = DateTime.UtcNow.AddDays(-1);
        var updatedBefore = session.UpdatedAt;
        _sut.MarkFilesCommitted([(session.Id, ["src/a.ts", "src/b.ts"], ["src/a.ts", "src/b.ts"])]);

        _sut.UnmarkFileCommitted(session.Id, "src/a.ts");
        session.CommittedFilePaths.Should().BeEquivalentTo(["src/b.ts"]);

        // Повтор без пометки — ранний выход (горячий путь): стор на диске не переписывается
        var storePath = Path.Combine(_tempDir, "sessions.json");
        var before = await File.ReadAllTextAsync(storePath);
        _sut.UnmarkFileCommitted(session.Id, "src/a.ts");
        (await File.ReadAllTextAsync(storePath)).Should().Be(before, "no-op не должен переписывать стор");
        session.UpdatedAt.Should().Be(updatedBefore, "снятие пометки не двигает UpdatedAt");
    }

    [Fact]
    public async Task MarkRead_ЧужойВладелецИлиНесуществующая_False()
    {
        var dir = MkProjectDir("mr3");
        var project = _projectManager.Create("MR3", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.MarkRead(session.Id, "another-user").Should().BeFalse();
        session.LastReadAt.Should().BeNull("чужая отметка не должна применяться");
        _sut.MarkRead("does-not-exist", TestUserId).Should().BeFalse();
    }

    // Перф-3 B10 п.6: PUT /api/chats/{id}/read ~0,9с в среднем (15 вызовов, замер прода
    // download5). Причина: MarkRead звал SaveSessions() синхронно — сериализация ВСЕГО
    // sessions.json (600 сессий) + fsync (flushToDisk) + sweep-terminus в каждом запросе.
    // Фикс: отметка пишет in-memory и ставит one-shot таймер (дебаунс) — запись одним пакетом,
    // вне запроса. Бенчмарк: «до» = синхронный SaveSessions на 600 сессиях (что делал старый
    // MarkRead), «после» = MarkRead с дебаунсом (in-memory, без полной записи).
    [Fact]
    public void MarkRead_Бенчмарк_ЗаписьНеСинхроннаВЗапросе()
    {
        var sut = BuildDebounceManager();
        try
        {
            // 600 project-less сессий одного владельца — как в проде (download5).
            for (var i = 0; i < 600; i++)
                sut.AddSessionForTest(new Session { ProjectId = null, OwnerId = TestUserId });

            // «До»: синхронный SaveSessions — то, что старый MarkRead делал в каждом запросе.
            var before = TimeMs(() => sut.SaveSessions());

            // «После»: MarkRead с дебаунсом — in-memory + one-shot таймер, без полной записи.
            var target = sut.GetAllOwnedBy(TestUserId).First();
            var after = TimeMs(() => sut.MarkRead(target.Id, TestUserId));

            Console.WriteLine(
                $"[MarkRead-бенчмарк] до (синхронный SaveSessions, 600 сессий): {before:F1} мс; " +
                $"после (MarkRead с дебаунсом): {after:F3} мс");
            after.Should().BeLessThan(before,
                "отметка прочитанности не должна писать весь sessions.json синхронно в запросе");
        }
        finally
        {
            // Снять one-shot таймер дебаунса, чтобы он не гонял SaveSessions после теста
            // (иначе запись ушла бы в уже удалённый temp-каталог).
            sut.Dispose();
        }
    }

    [Fact]
    public void LastReadAt_Сериализация_UtcСуффиксZ_И_RoundTrip()
    {
        // Клиент сравнивает Date.parse(lastReadAt) с Date.parse(updatedAt): без суффикса Z
        // (Kind=Unspecified) браузер прочитал бы время как локальное и непрочитанность
        // уехала бы на часовой пояс. Опции — как у SessionManager (_jsonOpts: только enum-конвертер)
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var session = new Session { Mode = ClaudeMode.Auto, LastReadAt = DateTime.UtcNow };

        var json = JsonSerializer.Serialize(session, opts);
        json.Should().MatchRegex("\"LastReadAt\":\"[^\"]+Z\"", "UTC обязан уезжать на диск с суффиксом Z");

        var restored = JsonSerializer.Deserialize<Session>(json, opts)!;
        restored.LastReadAt.Should().Be(session.LastReadAt);
        restored.LastReadAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Update_ExplicitValues_AreApplied()
    {
        var dir = MkProjectDir("upd3");
        var project = _projectManager.Create("UPD3", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "N", model: "opus", effort: "high");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, name: "N2", model: "sonnet", effort: "low");

        updated!.Name.Should().Be("N2");
        updated.Model.Should().Be("sonnet");
        updated.Effort.Should().Be("low");
    }

    [Fact]
    public async Task Update_СторонняяМодельНаClaude_ПровайдерВозвращаетсяВПул()
    {
        // Инцидент 14.08.2026 (прод, чат c20746b9): пересчёт Provider был только «в стороннего»,
        // и смена модели glm→opus[1m] (пилюля «Модель» в NewChatSetup до первого хода) оставляла
        // пару (Claude-модель, ключ glm) — CLI стартовал в профиле glm с моделью Anthropic →
        // мгновенный 401 «OAuth session expired».
        var dir = MkProjectDir("upd4");
        var project = _projectManager.Create("UPD4", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "N", model: "glm-5.2", effort: "high");
        session.Provider.Should().Be("glm", "сторонняя модель на создании → ключ её провайдера");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, name: null, model: "opus[1m]", effort: null);

        updated!.Model.Should().Be("opus", "родной Claude хранится семейством, окно — при запуске");
        updated.Provider.Should().Be("claude",
            "родная Claude-модель → ключ из пула (пустой пул → PrimaryKey), а не застрявший glm");
    }

    [Fact]
    public async Task Update_ClaudeМодельНаСтороннюю_ПровайдерСторонний()
    {
        // Обратное направление того же инварианта: модель — единственный источник правды,
        // Provider выводится из неё в обе стороны.
        var dir = MkProjectDir("upd5");
        var project = _projectManager.Create("UPD5", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "N", model: "opus", effort: "high");
        session.Provider.Should().Be("claude", "Claude-модель при пустом пуле → PrimaryKey");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, name: null, model: "glm-5.2", effort: null);

        updated!.Model.Should().Be("glm-5.2");
        updated.Provider.Should().Be("glm");
    }

    [Fact]
    public async Task Update_СменаПровайдераБезТранскрипта_Проходит()
    {
        // ClaudeSessionId выставлен (был служебный ход — kickoff онбординга, авто-приветствие),
        // но файла транскрипта нет: переносить нечего, смена модели обязана пройти
        var dir = MkProjectDir("prov1");
        var project = _projectManager.Create("PROV1", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");
        session.ClaudeSessionId = "fake-session-id";

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, null, "glm-5.2", null);

        updated!.Model.Should().Be("glm-5.2");
        updated.Provider.Should().Be("glm");
        Sent<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Label.Should().BeNull("переносить было нечего — разделитель «Продолжено на …» соврал бы");
    }

    [Fact]
    public async Task Update_СменаПровайдераБезХодов_Проходит()
    {
        // Совсем свежий чат: ClaudeSessionId ещё null — переносить нечего по определению
        var dir = MkProjectDir("prov0");
        var project = _projectManager.Create("PROV0", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, null, "glm-5.2", null);

        updated!.Model.Should().Be("glm-5.2");
        updated.Provider.Should().Be("glm");
    }

    [Fact]
    public async Task Update_СменаПровайдераСТранскриптом_ФайлПереезжаетВПрофильЦели()
    {
        // Разговор реальный: транскрипт лежит в профиле исходного провайдера и обязан
        // доехать в профиль целевого — иначе --resume начал бы с нуля
        var dir = MkProjectDir("prov4");
        var project = _projectManager.Create("PROV4", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");
        const string csid = "live-session-id";
        session.ClaudeSessionId = csid;
        PutTranscript(ClaudeProfileRoot, dir, csid, "{\"type\":\"user\"}\n");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, null, "glm-5.2", null);

        updated!.Provider.Should().Be("glm");
        var moved = Path.Combine(GlmProfileRoot, "projects",
            TranscriptMigrator.FlattenCwd(dir), csid + ".jsonl");
        File.Exists(moved).Should().BeTrue("транскрипт переносится в профиль целевого провайдера");
        File.ReadAllText(moved).Should().Be("{\"type\":\"user\"}\n");
        Sent<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Label.Should().NotBeNull("контекст переехал — разделитель «Продолжено на …» уместен");
    }

    [Fact]
    public async Task Update_СбойПереносаТранскрипта_Отказ()
    {
        // Транскрипт найден, но скопировать его не удалось — молча терять контекст нельзя
        // (тот же false отдаёт таймаут копирования живого файла). Ломаем приёмник: на месте
        // целевого файла — папка, FileStream(FileMode.Create) её не перезапишет.
        var dir = MkProjectDir("prov5");
        var project = _projectManager.Create("PROV5", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");
        const string csid = "broken-session-id";
        session.ClaudeSessionId = csid;
        PutTranscript(ClaudeProfileRoot, dir, csid, "{}\n");
        Directory.CreateDirectory(Path.Combine(GlmProfileRoot, "projects",
            TranscriptMigrator.FlattenCwd(dir), csid + ".jsonl"));

        var act = () => _sut.UpdateAsync(session.Id, TestUserId, null, "glm-5.2", null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Не удалось перенести транскрипт*");
        session.Provider.Should().Be("claude", "отказ переноса не меняет провайдера");
    }

    [Fact]
    public async Task Update_ХодВПолёте_ОтказПоЖивомуПрогону()
    {
        // Признак занятости — живой прогон адаптера, а не Status: у свежего чата он ещё
        // Starting, и окно между стартом хода и сменой статуса реально существует
        var dir = MkProjectDir("prov6");
        var project = _projectManager.Create("PROV6", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object); // HasLiveTurn = true
        session.Status.Should().NotBe(SessionStatus.Working, "статус тут ни при чём — смотрим на прогон");

        var act = () => _sut.UpdateAsync(session.Id, TestUserId, null, "glm-5.2", null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Идёт ответ ассистента — дождитесь его завершения, затем смените модель");
    }

    [Fact]
    public async Task Update_СбросНаПоУмолчанию_НеЗакрепляетМодель()
    {
        // «По умолчанию» при назначении места на чужого провайдера: переезд состоялся, но
        // модель НЕ закрепляется — иначе чат перестал бы следовать назначению места
        var dir = MkProjectDir("prov7");
        var project = _projectManager.Create("PROV7", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");
        _actionOverrides.Set(ClaudeHomeServer.Services.Llm.LocalActionCatalog.ChatNew, "glm-5.2");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, null, "", null);

        updated!.Model.Should().BeNull("выбран сброс — модель следует назначению места");
        updated.Provider.Should().Be("glm", "назначение места ведёт на glm — там же и профиль хода");
    }

    [Fact]
    public async Task Update_МигрирующийВызов_ПрименяетОстальныеПоля()
    {
        // Метод заявлен как PATCH: смена провайдера не должна съедать name/effort/tags
        var dir = MkProjectDir("prov8");
        var project = _projectManager.Create("PROV8", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Старое", model: "opus");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, "Новое", "glm-5.2", "low", ["t1"]);

        updated!.Provider.Should().Be("glm");
        updated.Name.Should().Be("Новое");
        updated.Effort.Should().Be("low");
        updated.Tags.Should().Equal("t1");
    }

    [Fact]
    public async Task Update_ЧужойВладелец_НеНаходитЧат()
    {
        // ownerId вызывающего доезжает до MigrateProviderAsync: чужая правка — 404, а не смена
        var dir = MkProjectDir("prov9");
        var project = _projectManager.Create("PROV9", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");

        var act = () => _sut.UpdateAsync(session.Id, "alien-user", null, "glm-5.2", null);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        session.Provider.Should().Be("claude");
    }

    [Fact]
    public async Task Update_ХодВПолёте_ОтказПоФолбэкОркестрации()
    {
        // Пауза между попытками цепочки фолбэка: прогона CLI прямо сейчас нет (HasLiveTurn=false),
        // но ход идёт — оркестратор в этот момент сам копирует транскрипт и по finally сверяет
        // Info.Model с применённой. Смена провайдера под ним разошлась бы с его restore, поэтому
        // занятость адаптера спрашиваем каноническим Busy, а не одним HasLiveTurn.
        var dir = MkProjectDir("prov10");
        var project = _projectManager.Create("PROV10", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(true);
        SetProcess(entry, adapter.Object);

        var act = () => _sut.UpdateAsync(session.Id, TestUserId, null, "glm-5.2", null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Идёт ответ ассистента*");
        session.Provider.Should().Be("claude", "отказ не двигает провайдера");
    }

    [Fact]
    public async Task Update_ПереноситьНечегоПоСырымПолям_ОстальныеПоляПрименяются()
    {
        // Провайдеров UpdateAsync считает по эффективным моделям, а миграция — по сырым
        // (Info.Model ?? Info.Provider). Чат на «По умолчанию», админ переставил назначение
        // места на glm после последнего хода: смена на opus выглядит как glm → claude, а для
        // миграции это claude → claude. Переносить нечего, и PATCH обязан примениться целиком,
        // а не упасть в 400 вместе с именем и усилием.
        var dir = MkProjectDir("prov12");
        var project = _projectManager.Create("PROV12", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Старое");
        session.Model.Should().BeNull("чат следует назначению места");
        _actionOverrides.Set(ClaudeHomeServer.Services.Llm.LocalActionCatalog.ChatNew, "glm-5.2");

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, "Новое", "opus", "low");

        updated!.Model.Should().Be("opus");
        updated.Provider.Should().Be("claude");
        updated.Name.Should().Be("Новое");
        updated.Effort.Should().Be("low");
    }

    [Fact]
    public async Task Update_НеизвестнаяМодельБезПулаПодписок_Отказ()
    {
        // Тот же расклад, что выше, но модель — мусорная (устаревший каталог /api/models во
        // фронте, провайдера убрали из конфига). Отказ обязан быть честным и на коробке БЕЗ
        // ClaudeSubscriptions: предикат IsNativeClaudeModel судит по ФОРМЕ id, а форма от
        // наличия пула не зависит. Пока проверка была под конъюнктом «текущий ключ — аккаунт
        // пула», на пустом пуле она выключалась целиком: Pick отдавал PrimaryKey = текущий
        // ключ, миграция отменялась как «переезжать некуда», и мусорный id молча ложился в
        // Info.Model — каждый следующий ход падал в CLI на несуществующей модели.
        var dir = MkProjectDir("prov13");
        var project = _projectManager.Create("PROV13", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Старое");
        session.Provider.Should().Be("claude", "пул подписок не настроен — родной Claude");
        _actionOverrides.Set(ClaudeHomeServer.Services.Llm.LocalActionCatalog.ChatNew, "glm-5.2");

        // Префикс чужого ключа брать нельзя: "glm-…" резолвится в провайдера glm по префиксу
        var act = () => _sut.UpdateAsync(session.Id, TestUserId, "Новое", "kimi-k3-нет-такой", null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Модель*не найдена среди настроенных провайдеров*");
        session.Model.Should().BeNull("отказ не закрепляет мусорную модель");
        session.Name.Should().Be("Старое", "PATCH не применился целиком — модель проверяется первой");
    }

    [Fact]
    public async Task CreateAsync_WithTaskId_SessionHasTaskIdAndTaskOrigin()
    {
        var dir = MkProjectDir("tid");
        var project = _projectManager.Create("TID", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits, taskExecution: true, taskId: "task-1");

        session.TaskId.Should().Be("task-1");
        session.Origin.Should().Be(ChatOrigin.Task);
    }

    [Fact]
    public async Task CreateAsync_NonExistentProject_ThrowsKeyNotFound()
    {
        var act = () => _sut.CreateAsync("nonexistent-project", ClaudeMode.Auto);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_SessionAppearsBInGetByProject()
    {
        var dir = MkProjectDir("ap");
        var project = _projectManager.Create("AP", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.GetByProject(project.Id).Should().ContainSingle(s => s.Id == session.Id);
    }

    // --- DeleteAsync ---

    [Fact]
    public async Task DeleteAsync_ExistingSession_RemovesFromStore()
    {
        var dir = MkProjectDir("del");
        var project = _projectManager.Create("Del", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        await _sut.DeleteAsync(session.Id);

        _sut.GetById(session.Id).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ExistingSession_DisappearsFromGetByProject()
    {
        var dir = MkProjectDir("dp");
        var project = _projectManager.Create("DP", dir, TestUserId, TestUsername);
        var s1 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var s2 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        await _sut.DeleteAsync(s1.Id);

        var remaining = _sut.GetByProject(project.Id);
        remaining.Should().HaveCount(1);
        remaining.Should().NotContain(s => s.Id == s1.Id);
        remaining.Should().Contain(s => s.Id == s2.Id);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentSession_DoesNotThrow()
    {
        var act = () => _sut.DeleteAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_SessionWithHistory_RemovesHistoryDir()
    {
        var dir = MkProjectDir("dh");
        var project = _projectManager.Create("DH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var claudeSessionId = "test-claude-session-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;
        await _historyService.SaveAsync(claudeSessionId,
            [new ClaudeHomeServer.Protocol.StoredTextMessage("будет удалено")]);

        var historyDir = Path.Combine(_tempDir, "sessions", claudeSessionId);
        Directory.Exists(historyDir).Should().BeTrue();

        await _sut.DeleteAsync(session.Id);

        Directory.Exists(historyDir).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_УдаляетТранскриптCliИНеТрогаетЧужой()
    {
        var dir = MkProjectDir("tr");
        var project = _projectManager.Create("TR", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var claudeSessionId = "cli-session-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;

        // Раскладка CLI: {профиль}/projects/{уплощенный cwd}/{csid}.jsonl. Рядом кладем
        // транскрипт другой сессии — в реальном ~/.claude в одной папке лежат чаты всех
        // инстансов сервера и интерактивные сессии пользователя, задеть их нельзя
        var transcriptDir = Path.Combine(_tempDir, "claude-profile", "projects",
            ClaudeHomeServer.Services.Llm.TranscriptMigrator.FlattenCwd(dir));
        Directory.CreateDirectory(transcriptDir);
        var mine = Path.Combine(transcriptDir, claudeSessionId + ".jsonl");
        var foreign = Path.Combine(transcriptDir, "someone-else.jsonl");
        File.WriteAllText(mine, "{\"type\":\"user\"}");
        File.WriteAllText(foreign, "{\"type\":\"user\"}");

        await _sut.DeleteAsync(session.Id);

        File.Exists(mine).Should().BeFalse();
        File.Exists(foreign).Should().BeTrue();
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"..\..\evil")]
    [InlineData("../../evil")]
    [InlineData(@"sub\dir")]
    [InlineData("with space")]
    public async Task CreateAsync_ПутьВResumeSessionId_Отклоняется(string badId)
    {
        // ClaudeSessionId становится именем папки в data/sessions и именем файла транскрипта,
        // а при удалении чата они удаляются рекурсивно. «..» здесь означал бы
        // Directory.Delete(data) — снос projects.json, users.json, историй и всех сторов
        var dir = MkProjectDir("bad");
        var project = _projectManager.Create("BAD", dir, TestUserId, TestUsername);

        var act = () => _sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: badId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).And.Message.Should().Contain("resumeSessionId");
        _sut.GetByProject(project.Id).Should().BeEmpty("негодная сессия не должна оседать в реестре");
        Directory.Exists(Path.Combine(_tempDir, "sessions")).Should().BeFalse("папка данных не тронута");
    }

    [Fact]
    public async Task CreateAsync_UuidВResumeSessionId_Принимается()
    {
        var dir = MkProjectDir("ok");
        var project = _projectManager.Create("OK", dir, TestUserId, TestUsername);
        var uuid = Guid.NewGuid().ToString();

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: uuid);

        session.ClaudeSessionId.Should().Be(uuid);
    }

    // --- Слоты тиров: новый чат идёт на слот «сильная» (назначение chat-new) ---

    [Fact]
    public async Task CreateAsync_БезModel_ПрименяетСлотСильной()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm");
        var project = _projectManager.Create("DCM", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task CreateAsync_ЯвныйModel_ПеребиваетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm-explicit");
        var project = _projectManager.Create("DCME", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");

        session.Model.Should().Be("opus");
    }

    [Fact]
    public async Task CreateAsync_МаркерУровня_РазворачиваетсяВМодельСлота()
    {
        // Уровень задачи/персоны приходит маркером «tier:*» — в сессии обязана осесть
        // конкретная модель: маркер не должен уйти ни в --model, ни на wire (шапка чата)
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2", ModelTierMedium = "sonnet" });
        var dir = MkProjectDir("dcm-tier");
        var project = _projectManager.Create("DCMT", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "tier:medium");

        session.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task CreateAsync_МаркерУровня_ПустойСлот_НеОседаетВСессии()
    {
        // Слот не настроен: место идёт своим назначением (chat-new → слот «сильная»),
        // а маркер не сохраняется
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm-tier-empty");
        var project = _projectManager.Create("DCMTE", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "tier:weak");

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task CreateAsync_Resume_НеПрименяетСлот()
    {
        // У resumed-сессии в транскрипте уже зафиксированы своя модель и провайдер —
        // подмена моделью слота сменила бы провайдер и упёрлась в guard (400)
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm-resume");
        var project = _projectManager.Create("DCMR", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto,
            resumeSessionId: Guid.NewGuid().ToString());

        session.Model.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ИсполнительЗадачи_ИдётПоНазначениюTasksExecutor()
    {
        // Назначение места «исполнитель задач» (оверрайд в сторе) сильнее слота chat-new
        _appSettings.Save(new AppSettings
        {
            ModelTierStrong = "glm-5.2",
            ModelTierMedium = "sonnet",
        });
        _actionOverrides.Set(ClaudeHomeServer.Services.Llm.LocalActionCatalog.TasksExecutor, "tier:medium");
        var dir = MkProjectDir("dcm-task");
        var project = _projectManager.Create("DCMT", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto,
            taskExecution: true, taskId: "task-1");

        session.Model.Should().Be("sonnet");
    }

    // B3 приёмки «Командной реализации» (дыра покрытия №14): смена ГЛОБАЛЬНОГО слота обязана
    // доезжать до дочерней executor-сессии — она стартует на том же провайдере, что и штаб,
    // а не на зашитом Claude-фолбэке. Назначения места нет, персона своей модели не задаёт.
    [Fact]
    public async Task CreateAsync_ИсполнительЗадачи_БезНазначения_ИдётГлобальнымСлотом()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "deepseek-v4-pro" });
        var dir = MkProjectDir("dcm-task-slot");
        var project = _projectManager.Create("DCMTS", dir, TestUserId, TestUsername);
        var executor = _personaManager.Create(TestUserId, "Денис-исполнитель", null, null, null,
            model: null, effort: null, scope: PersonaScope.Project, projectId: project.Id,
            color: null, greeting: null, memoryEnabled: false);

        // Модель приходит из TaskExecutionService.ResolveExecutorModel: у задачи нет уровня,
        // у персоны нет ни модели, ни уровня — значит null, решает место tasks-executor
        var model = TaskExecutionService.ResolveExecutorModel(
            new TaskItem { Title = "под-задача волны", PersonaId = executor.Id }, executor);
        model.Should().BeNull();

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits, model: model,
            personaId: executor.Id, taskExecution: true, taskId: "task-wave-1");

        session.Model.Should().Be("deepseek-v4-pro", "слот «сильная» — дефолтный тир места tasks-executor");
    }

    // Уровень персоны-исполнителя тоже разворачивается по глобальному слоту, а не в Claude
    [Fact]
    public async Task CreateAsync_ИсполнительЗадачи_УровеньПерсоны_ИдётГлобальнымСлотом()
    {
        _appSettings.Save(new AppSettings
        {
            ModelTierStrong = "deepseek-v4-pro",
            ModelTierWeak = "deepseek-v4-flash",
        });
        var dir = MkProjectDir("dcm-task-tier");
        var project = _projectManager.Create("DCMTT", dir, TestUserId, TestUsername);
        var executor = _personaManager.Create(TestUserId, "Клио-исполнитель", null, null, null,
            model: null, effort: null, scope: PersonaScope.Project, projectId: project.Id,
            color: null, greeting: null, memoryEnabled: false, modelTier: "weak");

        var model = TaskExecutionService.ResolveExecutorModel(
            new TaskItem { Title = "под-задача", PersonaId = executor.Id }, executor);
        model.Should().Be("tier:weak");

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits, model: model,
            personaId: executor.Id, taskExecution: true, taskId: "task-wave-2");

        session.Model.Should().Be("deepseek-v4-flash", "уровень разворачивается слотом инстанса");
    }

    [Fact]
    public async Task ЧатПерсоны_БезМоделиУПерсоны_ПрименяетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("persona-default");
        var project = _projectManager.Create("PD", dir, TestUserId, TestUsername);
        var persona = MkPersona("Без модели", null, project.Id);

        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task ЧатПерсоны_МодельПерсоны_ПеребиваетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("persona-explicit");
        var project = _projectManager.Create("PE", dir, TestUserId, TestUsername);
        var persona = MkPersona("С моделью", "opus", project.Id);

        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        session.Model.Should().Be("opus");
    }

    [Fact]
    public async Task ГрупповойЧат_ВедущаяБезМодели_ПрименяетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("group-default");
        var project = _projectManager.Create("GD", dir, TestUserId, TestUsername);
        var leader = MkPersona("Ведущая", null, project.Id);
        var second = MkPersona("Вторая", null, project.Id);

        var session = await _sut.CreateGroupChatAsync(TestUserId, [leader.Id, second.Id], ClaudeMode.Auto);

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task SetPersona_ПерсонаБезМодели_НеЗатираетМодельСлота()
    {
        // Раньше назначение персоны в неначатый чат безусловно писало persona.Model = null,
        // и подставленная при создании модель слота молча терялась
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("switch-speaker");
        var project = _projectManager.Create("SS", dir, TestUserId, TestUsername);
        var persona = MkPersona("Без модели", null, project.Id);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Model.Should().Be("glm-5.2");

        var updated = _sut.SetPersona(session.Id, TestUserId, persona.Id);

        updated!.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task SetPersona_ПроектнаяСессия_УровеньПерсоныИдётПоЛичномуСлотуВладельца()
    {
        // У проектной сессии Session.OwnerId всегда null — владелец живёт у проекта.
        // Если брать поле напрямую, личный слот молча подменяется глобальным: персона
        // с уровнем «сильная» уезжала бы в проектном чате на чужую модель
        _appSettings.Save(new AppSettings { ModelTierStrong = "global-sonnet" });
        var user = _userStore.Add("tier-owner", "password123", "user");
        _userStore.SetModelTiers(user.Id, strong: "personal-opus", medium: null, weak: null);
        var dir = MkProjectDir("tier-owner");
        var project = _projectManager.Create("TO", dir, user.Id, "tier-owner");
        var persona = _personaManager.Create(user.Id, "С уровнем", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: project.Id, color: null, greeting: null, memoryEnabled: false,
            modelTier: "strong");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "sonnet");
        session.OwnerId.Should().BeNull("владелец проектной сессии живёт у проекта");

        var updated = _sut.SetPersona(session.Id, user.Id, persona.Id);

        updated!.Model.Should().Be("personal-opus");
    }

    [Fact]
    public async Task SetPersona_ЧатВнеПроекта_УровеньПерсоныИдётПоТомуЖеЛичномуСлоту()
    {
        // Парная проверка: вне проекта владелец берётся из самой сессии — результат
        // обязан совпасть с проектным случаем, иначе слот «плавает» от места чата
        _appSettings.Save(new AppSettings { ModelTierStrong = "global-sonnet" });
        var user = _userStore.Add("tier-owner-chat", "password123", "user");
        _userStore.SetModelTiers(user.Id, strong: "personal-opus", medium: null, weak: null);
        var persona = _personaManager.Create(user.Id, "С уровнем", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Global,
            projectId: null, color: null, greeting: null, memoryEnabled: false,
            modelTier: "strong");
        var session = await _sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");

        var updated = _sut.SetPersona(session.Id, user.Id, persona.Id);

        updated!.Model.Should().Be("personal-opus");
    }

    [Fact]
    public async Task DeleteAsync_ОбщийТранскриптДругогоЧата_НеУдаляет()
    {
        // Сессия, созданная с resumeSessionId, несет тот же ClaudeSessionId — транскрипт у
        // двух чатов общий. Удаление одного не должно лишать второй памяти разговора
        var dir = MkProjectDir("shared");
        var project = _projectManager.Create("SHARED", dir, TestUserId, TestUsername);
        var claudeSessionId = "cli-session-" + Guid.NewGuid().ToString("N");

        var first = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        first.ClaudeSessionId = claudeSessionId;
        var resumed = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: claudeSessionId);
        resumed.ClaudeSessionId.Should().Be(claudeSessionId);

        var transcriptDir = Path.Combine(_tempDir, "claude-profile", "projects",
            ClaudeHomeServer.Services.Llm.TranscriptMigrator.FlattenCwd(dir));
        Directory.CreateDirectory(transcriptDir);
        var transcript = Path.Combine(transcriptDir, claudeSessionId + ".jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\"}");

        // История тоже общая — ее удаление обнулило бы ленту первого чата в UI
        await _historyService.SaveAsync(claudeSessionId,
            [new ClaudeHomeServer.Protocol.StoredTextMessage("общая история")]);
        var historyDir = Path.Combine(_tempDir, "sessions", claudeSessionId);

        await _sut.DeleteAsync(resumed.Id);
        File.Exists(transcript).Should().BeTrue("на транскрипт еще ссылается первый чат");
        Directory.Exists(historyDir).Should().BeTrue("история тоже общая");

        // Последний ссылающийся чат ушел — теперь убирать можно
        await _sut.DeleteAsync(first.Id);
        File.Exists(transcript).Should().BeFalse();
        Directory.Exists(historyDir).Should().BeFalse();
    }

    // --- SetExpiry ---

    [Fact]
    public async Task SetExpiry_ВключаетИВыключаетВременность()
    {
        var dir = MkProjectDir("ex");
        var project = _projectManager.Create("EX", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = _sut.SetExpiry(session.Id, 1440);
        updated!.ExpiresAfterMinutes.Should().Be(1440);

        updated = _sut.SetExpiry(session.Id, null);
        updated!.ExpiresAfterMinutes.Should().BeNull();
    }

    [Fact]
    public async Task SetExpiry_ПерезапускаетОтсчёт_ЯкоремАНеUpdatedAt()
    {
        // Смена срока — настройка, а не активность: UpdatedAt (сортировка списка и
        // непрочитанность) не двигается, отсчёт стартует от ExpiryAnchor
        var dir = MkProjectDir("ex2");
        var project = _projectManager.Create("EX2", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.UpdatedAt = DateTime.UtcNow.AddDays(-5);
        var updatedAtBefore = session.UpdatedAt;
        var before = DateTime.UtcNow;

        var updated = _sut.SetExpiry(session.Id, 60);

        updated!.UpdatedAt.Should().Be(updatedAtBefore);
        updated.ExpiryAnchor.Should().NotBeNull().And.Subject.As<DateTime>().Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task SetExpiry_Снятие_ОбнуляетЯкорь()
    {
        var dir = MkProjectDir("ex3");
        var project = _projectManager.Create("EX3", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.SetExpiry(session.Id, 60);
        var updated = _sut.SetExpiry(session.Id, null);

        updated!.ExpiryAnchor.Should().BeNull("без срока якорю нечего держать");
    }

    // --- FreezeExpiryForHold / RestoreFrozenExpiry ---

    // Q6-TAIL3 (хвост RV-K-Q6-TAIL): unit-страж на самой RestoreFrozenExpiry (SessionManager.cs:2455).
    // Сквозной QueueHold_25Hours_RestoredExpiryAnchoredAtClearingNotOldActivity ловит только
    // «срок без нового якоря»: регрессию «якорь = последняя активность» он не ловит — доставка
    // продолжения сама есть активность (SendDirectAsync безусловно ставит Working,
    // SessionManager.cs:5281 → :10890) и поднимает UpdatedAt раньше возврата срока. Здесь нет
    // ResumeFeatureTurnsAfterHold: чат со старой активностью (−25 ч) — и «якорь = UpdatedAt»
    // отличается от правильного «якорь = момент снятия». Мутация: в RestoreFrozenExpiry вместо
    // SetExpiry(sessionId, frozen) — прямая запись ExpiresAfterMinutes с
    // ExpiryAnchor = entry.Info.UpdatedAt — абзац якоря краснеет (он в прошлом).
    [Fact]
    public async Task RestoreFrozenExpiry_ЯкорьМоментСнятияАНеСтараяАктивность()
    {
        var dir = MkProjectDir("ex4");
        var project = _projectManager.Create("EX4", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.SetExpiry(session.Id, 10);
        _sut.FreezeExpiryForHold(session.Id);
        session.HoldFrozenExpiresAfterMinutes.Should().Be(10, "пауза сохранила срок самого чата");

        // Состояние после суток удержания: к снятию чат подходит с активностью 25-часовой давности.
        var updatedAtOld = session.UpdatedAt = DateTime.UtcNow.AddHours(-25);
        var beforeRestore = DateTime.UtcNow;

        var restored = _sut.RestoreFrozenExpiry(session.Id);

        restored!.ExpiresAfterMinutes.Should().Be(10, "замороженный срок вернулся");
        restored.HoldFrozenExpiresAfterMinutes.Should().BeNull("сохранённое значение погашено");
        restored.UpdatedAt.Should().Be(updatedAtOld,
            "возврат срока — настройка хранения, а не активность: старую активность не поднимаем");
        restored.ExpiryAnchor.Should().NotBeNull().And.Subject.As<DateTime>()
            .Should().BeOnOrAfter(beforeRestore,
            "якорь отсчёта — момент снятия удержания, а не замороженная активность");
    }

    // --- SetNotificationsMuted ---

    [Fact]
    public async Task SetNotificationsMuted_МеняетФлагИНеТрогаетUpdatedAt()
    {
        var dir = MkProjectDir("mute");
        var project = _projectManager.Create("MUTE", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.UpdatedAt = DateTime.UtcNow.AddDays(-2);
        var before = session.UpdatedAt;

        var muted = _sut.SetNotificationsMuted(session.Id, true);
        muted!.NotificationsMuted.Should().BeTrue();
        muted.UpdatedAt.Should().Be(before, "тумблер уведомлений не активность чата");

        _sut.SetNotificationsMuted(session.Id, false)!.NotificationsMuted.Should().BeFalse();
        _sut.SetNotificationsMuted("nonexistent", true).Should().BeNull();
    }

    // --- SetVoiceMode ---

    [Fact]
    public async Task SetVoiceMode_МеняетФлагИНеТрогаетUpdatedAt()
    {
        var dir = MkProjectDir("voice");
        var project = _projectManager.Create("VOICE", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.UpdatedAt = DateTime.UtcNow.AddDays(-2);
        var before = session.UpdatedAt;

        var updated = _sut.SetVoiceMode(session.Id, true);
        updated!.VoiceMode.Should().BeTrue();
        updated.UpdatedAt.Should().Be(before, "тумблер голосового режима не активность чата");

        _sut.SetVoiceMode(session.Id, false)!.VoiceMode.Should().BeFalse();
        _sut.SetVoiceMode("nonexistent", true).Should().BeNull();
    }

    [Fact]
    public async Task Update_БезПолей_НеТрогаетUpdatedAt()
    {
        // PUT приходит и с одними настройками (срок, мьют) — все поля тогда null.
        // Безусловная отметка времени поднимала бы чат в списке за смену настройки
        var dir = MkProjectDir("upd-none");
        var project = _projectManager.Create("UPDN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.UpdatedAt = DateTime.UtcNow.AddDays(-3);
        var before = session.UpdatedAt;

        (await _sut.UpdateAsync(session.Id, TestUserId, null, null, null))!.UpdatedAt.Should().Be(before);
        (await _sut.UpdateAsync(session.Id, TestUserId, "Имя", null, null))!.UpdatedAt.Should()
            .BeAfter(before, "правка имени — обычное изменение чата");
    }

    [Fact]
    public void SetExpiry_NonExistentSession_ReturnsNull()
    {
        _sut.SetExpiry("nonexistent", 60).Should().BeNull();
    }

    // --- Контекст чата (фича chat-context, A1–A2) ---

    [Fact]
    public async Task SetContext_ЗаменяетСостав_ПереживаетПерезагрузку_НеТрогаетUpdatedAt_ИШлётСобытие()
    {
        var dir = MkProjectDir("ctx");
        var project = _projectManager.Create("CTX", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.UpdatedAt = DateTime.UtcNow.AddDays(-4);
        var updatedAtBefore = session.UpdatedAt;
        ClearSent();

        var entries = new List<SessionContextEntry>
        {
            new() { Type = SessionContextTypes.File, Id = "docs/readme.md", Title = "README" },
            new() { Type = SessionContextTypes.Task, Id = "task-1" },
        };
        var updated = await _sut.SetContextAsync(session.Id, entries);

        // Замена состава целиком: второй PUT с одним файлом затирает прежние две записи
        updated!.Context.Should().HaveCount(2);
        await _sut.SetContextAsync(session.Id, [new SessionContextEntry { Type = SessionContextTypes.File, Id = "src/a.ts" }]);
        session.Context.Should().ContainSingle().Which.Id.Should().Be("src/a.ts");
        // Инвариант conventions.md: правка контекста — настройка, а не активность
        session.UpdatedAt.Should().Be(updatedAtBefore, "контекст не должен двигать UpdatedAt");
        // Событие ушло (полный снимок нового состава; SessionId ставит рассылка).
        // Два вызова — два события, проверяем последнее
        var msgs = Sent<ContextUpdatedMessage>();
        msgs.Should().HaveCount(2, "по событию на каждый SetContext");
        var msg = msgs.Last();
        msg.SessionId.Should().Be(session.Id);
        msg.Entries.Should().ContainSingle().Which.Id.Should().Be("src/a.ts");
        // Значение доехало до стора: sessions.json сериализуется в SetContextAsync явно
        var sessionsJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "sessions.json"));
        sessionsJson.Should().Contain("src/a.ts");
    }

    // Перезагрузка: новый SessionManager на том же DataPath читает состав из sessions.json.
    // Сборка — та же, что у основного _sut (по образцу BuildSut в SessionManagerSubscriptionMigrationTests)
    [Fact]
    public async Task SetContext_ПерезагрузкаМенеджера_СоставНаМесте()
    {
        var dir = MkProjectDir("ctx-reload");
        var project = _projectManager.Create("CTXRL", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        await _sut.SetContextAsync(session.Id,
        [
            new SessionContextEntry { Type = SessionContextTypes.Url, Id = "https://example.com", Title = "Статья" },
        ]);

        var reloaded = BuildSecondManager();

        var restored = reloaded.GetById(session.Id);
        restored.Should().NotBeNull();
        restored!.Context.Should().ContainSingle();
        restored.Context[0].Type.Should().Be(SessionContextTypes.Url);
        restored.Context[0].Id.Should().Be("https://example.com");
        restored.Context[0].Title.Should().Be("Статья");
    }

    [Fact]
    public async Task SetContext_NесуществующаяСессия_Null()
    {
        (await _sut.SetContextAsync("nonexistent", [])).Should().BeNull();
    }

    // A5: подсказку хода питает Func-провайдер, а не снимок при создании адаптера — материал,
    // добавленный кнопкой в ЖИВОМ чате, обязан попасть в промпт следующего хода без
    // пересоздания сессии. Провайдер берётся один раз (при создании LlmSessionContext),
    // поэтому проверяем именно повторный вызов ТОГО ЖЕ делегата.
    [Fact]
    public async Task ChatContextProvider_ВидитМатериалДобавленныйПослеСозданияПровайдера()
    {
        var dir = MkProjectDir("ctx-live");
        var project = _projectManager.Create("CTXLIVE", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var provider = _sut.BuildChatContextProvider(session.Id);
        provider().Should().BeEmpty("контекст пуст — секции промпта не будет");

        await _sut.SetContextAsync(session.Id,
            [new SessionContextEntry { Type = SessionContextTypes.File, Id = "docs/readme.md" }]);

        provider().Should().ContainSingle().Which.Id.Should().Be("docs/readme.md",
            "тот же делегат обязан видеть новый состав — иначе подсказка врала бы о материалах");
    }

    [Fact]
    public void ChatContextProvider_НесуществующаяСессия_ПустойСостав()
    {
        // Чат удалили посреди жизни адаптера: провайдер отдаёт пусто, а не роняет ход
        _sut.BuildChatContextProvider("nonexistent")().Should().BeEmpty();
    }
}
