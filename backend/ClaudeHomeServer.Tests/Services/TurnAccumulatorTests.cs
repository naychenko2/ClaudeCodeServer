using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

public class TurnAccumulatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChatHistoryService _histSvc;

    public TurnAccumulatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "acc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _histSvc = new ChatHistoryService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build());
    }

    [Fact]
    public void SetPromptSnapshot_ПривязываетСнимокКСообщениюХода()
    {
        var acc = new TurnAccumulator([]);
        acc.OnUserMessage("почини сборку", []);

        acc.SetPromptSnapshot("1700000000000-abcd");

        acc.GetAll().OfType<StoredUserMessage>().Single()
            .PromptSnapshotId.Should().Be("1700000000000-abcd");
    }

    [Fact]
    public void SetPromptSnapshot_БезСообщенияХода_НеПадает()
    {
        // Продолжение цикла «до готово» идёт без нового сообщения человека: снимок
        // остаётся на диске, но вешать его в ленте не на что
        var acc = new TurnAccumulator([]);

        var act = () => acc.SetPromptSnapshot("1700000000000-abcd");

        act.Should().NotThrow();
    }

    [Fact]
    public void GetAll_EmptyAccumulator_ReturnsEmpty()
    {
        var acc = new TurnAccumulator([]);
        acc.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void GetAll_WithPreloadedHistory_ReturnsHistory()
    {
        var history = new List<StoredMessage> { new StoredTextMessage("old") };
        var acc = new TurnAccumulator(history);
        acc.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public void OnUserMessage_AddsToCurrentTurn()
    {
        var acc = new TurnAccumulator([]);
        acc.OnUserMessage("hello", []);
        var all = acc.GetAll();
        all.Should().HaveCount(1);
        all.OfType<StoredUserMessage>().Single().Text.Should().Be("hello");
    }

    [Fact]
    public void OnUserMessage_WithAttachments_StoresAttachments()
    {
        var acc = new TurnAccumulator([]);
        acc.OnUserMessage("text", ["file.txt", "other.cs"]);
        var msg = acc.GetAll().Single() as StoredUserMessage;
        msg!.AttachedPaths.Should().BeEquivalentTo(["file.txt", "other.cs"]);
    }

    [Fact]
    public void OnSessionStarted_AddsSessionStartedMessage()
    {
        var acc = new TurnAccumulator([]);
        acc.OnSessionStarted("claude-3", "auto");
        var started = acc.GetAll().OfType<StoredSessionStartedMessage>().Single();
        started.Model.Should().Be("claude-3");
        started.Mode.Should().Be("auto");
        started.TurnWorktree.Should().BeNull();
    }

    // Пометка автоподмены модели пишется в историю, чтобы после F5/рестарта человек
    // видел, что отвечала не та модель. PreviousModel — модель последнего session_started
    // этого хода (провалившаяся попытка успела его прислать).
    [Fact]
    public void OnModelSwitched_ПишетПометкуВИсторию()
    {
        var acc = new TurnAccumulator([]);
        acc.OnSessionStarted("claude-opus-4-8", "auto");
        acc.OnModelSwitched("deepseek-chat", acc.LastStartedModel(), "rate_limit");

        var all = acc.GetAll();
        all.OfType<StoredModelSwitchedMessage>().Should().ContainSingle()
            .Which.Should().Match<StoredModelSwitchedMessage>(m =>
                m.Model == "deepseek-chat"
                && m.PreviousModel == "claude-opus-4-8"
                && m.Reason == "rate_limit");
    }

    // Подмена гасит промежуточную ошибку провайдера: красной карточки в ленте нет, но
    // сырой текст едет в пометке под «Подробностями» и обязан пережить F5 — значит лежит
    // в истории вместе с самой пометкой.
    [Fact]
    public async Task OnModelSwitched_СДеталями_ПишетИхВИсторию()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnSessionStarted("claude-opus-4-8", "auto");
        acc.OnModelSwitched("glm-5.2", acc.LastStartedModel(), "provider_error",
            "API Error: 529 Overloaded");
        await acc.OnResultAsync("success", 10, 1, null, null, null, null, _histSvc);

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.OfType<StoredModelSwitchedMessage>().Should().ContainSingle()
            .Which.Should().Match<StoredModelSwitchedMessage>(m =>
                m.Model == "glm-5.2"
                && m.PreviousModel == "claude-opus-4-8"
                && m.Reason == "provider_error"
                && m.Details == "API Error: 529 Overloaded");
    }

    // Подмена без погашенной ошибки (ротация по лимиту) — прежнее поведение: деталей нет
    [Fact]
    public void OnModelSwitched_БезДеталей_ПрежнееПоведение()
    {
        var acc = new TurnAccumulator([]);
        acc.OnSessionStarted("claude-opus-4-8", "auto");
        acc.OnModelSwitched("deepseek-chat", acc.LastStartedModel(), "rate_limit");

        acc.GetAll().OfType<StoredModelSwitchedMessage>().Should().ContainSingle()
            .Which.Details.Should().BeNull();
    }

    // Без PreviousModel (в начале чата ещё не было session_started) пилюля не пишется —
    // иначе в истории «Ответила X — была Y», а Y неизвестна
    [Fact]
    public void OnModelSwitched_БезSessionStarted_НеПишетПометку()
    {
        var acc = new TurnAccumulator([]);
        acc.OnModelSwitched("deepseek-chat", acc.LastStartedModel(), "rate_limit");

        acc.GetAll().OfType<StoredModelSwitchedMessage>().Should().BeEmpty();
    }

    // LastStartedModel: обход с хвоста текущего хода вглубь истории — модель
    // предыдущего хода остаётся «последней известной» к моменту подмены в новом
    [Fact]
    public void LastStartedModel_ОбходОтХвостаВглубьИстории()
    {
        var history = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("gpt-4", "auto"),
            new StoredTextMessage("старый ответ"),
        };
        var acc = new TurnAccumulator(history);
        acc.OnSessionStarted("claude-opus-4-8", "auto");

        acc.LastStartedModel().Should().Be("claude-opus-4-8",
            "свежий session_started в текущем ходу — приоритет у него");
    }

    [Fact]
    public void LastStartedModel_НетВТекущемХоду_ИзИстории()
    {
        var history = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("claude-opus-4-8", "auto"),
            new StoredTextMessage("прошлый ответ"),
        };
        var acc = new TurnAccumulator(history);
        // Нового session_started в этом ходу нет

        acc.LastStartedModel().Should().Be("claude-opus-4-8",
            "для подмены в начале нового хода берётся модель прошлого");
    }

    // Признак «ход идёт в чужом дереве» переживает перезагрузку истории — попадает в
    // снимок хода наравне с Model/Mode, а не теряется между сериализацией и десериализацией
    [Fact]
    public void OnSessionStarted_WithWorktree_ПерсистируетПризнак()
    {
        var acc = new TurnAccumulator([]);
        var worktree = new TurnWorktreeInfo("/projects/demo/.claude/worktrees/feature-x", "feature-x");
        acc.OnSessionStarted("claude-3", "auto", worktree);
        var started = acc.GetAll().OfType<StoredSessionStartedMessage>().Single();
        started.TurnWorktree.Should().Be(worktree);
    }

    [Fact]
    public void OnTextDelta_Accumulates_FlushedByOnToolUse()
    {
        var acc = new TurnAccumulator([]);
        acc.OnTextDelta("hello ");
        acc.OnTextDelta("world");
        // буфер ещё не зафиксирован в ход, но виден в снимке как единый текст
        acc.GetAll().Should().ContainSingle()
            .Which.Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("hello world");

        // OnToolUse триггерит FlushBuffers
        acc.OnToolUse("t1", "bash", new { });
        var all = acc.GetAll();
        all.Should().HaveCount(2);
        all[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("hello world");
        all[1].Should().BeOfType<StoredToolUseMessage>();
    }

    [Fact]
    public void OnThinkingDelta_Accumulates_FlushedByOnToolUse()
    {
        var acc = new TurnAccumulator([]);
        acc.OnThinkingDelta("I think ");
        acc.OnThinkingDelta("therefore");
        acc.OnToolUse("t1", "read", new { });

        var all = acc.GetAll();
        all[0].Should().BeOfType<StoredThinkingMessage>().Which.Text.Should().Be("I think therefore");
    }

    [Fact]
    public void OnAgentText_AddsRecordWithParent_AndFlushesMainText()
    {
        var acc = new TurnAccumulator([]);
        acc.OnTextDelta("main "); // накапливаемый текст основного агента
        acc.OnAgentText("task1", "реплика сабагента");

        var all = acc.GetAll();
        all.Should().HaveCount(2);
        // буфер разрезан ПЕРЕД текстом сабагента — порядок совпадает с live-лентой
        all[0].Should().BeOfType<StoredTextMessage>()
            .Which.Should().Match<StoredTextMessage>(t => t.Text == "main " && t.ParentToolUseId == null);
        all[1].Should().BeOfType<StoredTextMessage>()
            .Which.Should().Match<StoredTextMessage>(t =>
                t.Text == "реплика сабагента" && t.ParentToolUseId == "task1");
    }

    [Fact]
    public void OnAgentThinking_AddsRecordWithParent()
    {
        var acc = new TurnAccumulator([]);
        acc.OnAgentThinking("task1", "мысль сабагента");
        acc.OnAgentText("task1", "текст после");

        var all = acc.GetAll();
        all.Should().HaveCount(2);
        all[0].Should().BeOfType<StoredThinkingMessage>()
            .Which.Should().Match<StoredThinkingMessage>(t =>
                t.Text == "мысль сабагента" && t.ParentToolUseId == "task1");
        all[1].Should().BeOfType<StoredTextMessage>();
    }

    [Fact]
    public async Task AgentText_SurvivesRoundtripToDisk()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnToolUse("task1", "Task", new { });
        acc.OnAgentThinking("task1", "думаю");
        acc.OnAgentText("task1", "пишу");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc);

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().HaveCount(4);
        loaded[1].Should().BeOfType<StoredThinkingMessage>().Which.ParentToolUseId.Should().Be("task1");
        loaded[2].Should().BeOfType<StoredTextMessage>().Which.ParentToolUseId.Should().Be("task1");
    }

    [Fact]
    public void OnToolResult_UpdatesPendingToolUse()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("t1", "bash", new { });
        acc.OnToolResult("t1", "output here", false);

        var tool = acc.GetAll().OfType<StoredToolUseMessage>().Single();
        tool.Result.Should().Be("output here");
        tool.IsError.Should().BeFalse();
    }

    [Fact]
    public void OnToolResult_ErrorFlag_SetsIsError()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("t1", "bash", null);
        acc.OnToolResult("t1", "error message", true);

        var tool = acc.GetAll().OfType<StoredToolUseMessage>().Single();
        tool.IsError.Should().BeTrue();
    }

    [Fact]
    public void OnFileChanged_AddsFileChangedMessage()
    {
        var acc = new TurnAccumulator([]);
        acc.OnFileChanged("src/file.cs", 10, 3);

        var changed = acc.GetAll().OfType<StoredFileChangedMessage>().Single();
        changed.Path.Should().Be("src/file.cs");
        changed.Added.Should().Be(10);
        changed.Removed.Should().Be(3);
    }

    [Fact]
    public void OnFileChanged_ПовторнаяПравкаТогоЖеФайла_СуммируетДельтуОднойСтрокой()
    {
        var acc = new TurnAccumulator([]);
        acc.OnFileChanged("src/file.cs", 10, 3);
        acc.OnFileChanged("src/other.cs", 1, 0);
        acc.OnFileChanged("src/file.cs", 5, 2);

        var all = acc.GetAll().OfType<StoredFileChangedMessage>().ToList();
        all.Should().HaveCount(2); // file.cs схлопнут в одну строку
        var file = all.Single(m => m.Path == "src/file.cs");
        file.Added.Should().Be(15);
        file.Removed.Should().Be(5);
    }

    [Fact]
    public void OnFileChanged_External_СохраняетПометку()
    {
        var acc = new TurnAccumulator([]);
        acc.OnFileChanged("src/file.cs", 10, 3, external: true);

        var changed = acc.GetAll().OfType<StoredFileChangedMessage>().Single();
        changed.External.Should().BeTrue();
    }

    [Fact]
    public void OnFileChanged_ОдинВкладОтМодели_СнимаетExternalСоВсейСтроки()
    {
        var acc = new TurnAccumulator([]);
        acc.OnFileChanged("src/file.cs", 10, 3, external: true);
        acc.OnFileChanged("src/file.cs", 5, 2, external: false);

        var changed = acc.GetAll().OfType<StoredFileChangedMessage>().Single();
        changed.External.Should().BeFalse();
    }

    // Модель хода приходит только с result, а посты к тому моменту уже созданы —
    // проверяем, что она проставляется им задним числом (подпись модели у поста)
    [Fact]
    public async Task OnResultAsync_BackfillsModelToTurnTexts()
    {
        var acc = new TurnAccumulator([]);
        acc.OnTextDelta("ответ");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc, usageModel: "claude-opus-4-8");

        acc.GetAll().OfType<StoredTextMessage>().Single().Model.Should().Be("claude-opus-4-8");
    }

    // Текст сабагента мог идти другой моделью, чем главная модель хода — UsageModel
    // про него ничего не говорит, поэтому его помечать нельзя
    [Fact]
    public async Task OnResultAsync_DoesNotBackfillModelToSubagentText()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("task1", "Task", new { });
        acc.OnAgentText("task1", "текст сабагента");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc, usageModel: "claude-opus-4-8");

        acc.GetAll().OfType<StoredTextMessage>()
            .Single(m => m.ParentToolUseId == "task1").Model.Should().BeNull();
    }

    // Модель предыдущего хода не должна перебиваться моделью следующего: посты,
    // уже помеченные, второй backfill не трогает
    [Fact]
    public async Task OnResultAsync_KeepsModelOfEarlierTurn()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta("первый ход");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc, usageModel: "claude-haiku-4-5");
        acc.OnTextDelta("второй ход");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc, usageModel: "claude-opus-4-8");

        var texts = (await _histSvc.LoadAsync(sessionId)).OfType<StoredTextMessage>().ToList();
        texts.Should().HaveCount(2);
        texts[0].Model.Should().Be("claude-haiku-4-5");
        texts[1].Model.Should().Be("claude-opus-4-8");
    }

    // Время пишется и сообщению человека, и посту ассистента — без него панель поста
    // не сможет показать, когда он написан
    [Fact]
    public async Task OnResultAsync_StampsTimestampOnUserAndText()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var acc = new TurnAccumulator([]);
        acc.OnUserMessage("вопрос", []);
        acc.OnTextDelta("ответ");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var all = acc.GetAll();
        all.OfType<StoredUserMessage>().Single().Timestamp.Should().BeInRange(before, after);
        all.OfType<StoredTextMessage>().Single().Timestamp.Should().BeInRange(before, after);
    }

    [Fact]
    public async Task OnResultAsync_FlushesBuffersAndSavesToHistory()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnUserMessage("hi", []);
        acc.OnTextDelta("response text");

        await acc.OnResultAsync("success", 1000, 1, null, null, null, null, _histSvc);

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().HaveCount(3); // user + text + result
        loaded[0].Should().BeOfType<StoredUserMessage>();
        loaded[1].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("response text");
        loaded[2].Should().BeOfType<StoredResultMessage>().Which.Subtype.Should().Be("success");
    }

    [Fact]
    public async Task OnErrorAsync_FlushesBuffersAndSavesToHistory()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta("partial");

        await acc.OnErrorAsync("something went wrong", _histSvc);

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().HaveCount(2);
        loaded[0].Should().BeOfType<StoredTextMessage>();
        loaded[1].Should().BeOfType<StoredErrorMessage>().Which.Text.Should().Be("something went wrong");
    }

    // Настоящий провал хода: человеку — формулировка из TurnFailureText, сырой техтекст
    // остаётся в карточке под «Подробностями» и переживает перезагрузку истории
    [Fact]
    public async Task OnErrorAsync_СДеталями_ХранитСыройТекстОтдельно()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);

        await acc.OnErrorAsync("Ход прервался — что-то пошло не так на стороне сервера. Отправьте сообщение ещё раз.",
            _histSvc, "System.IO.IOException: Идет закрытие канала.");

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().ContainSingle()
            .Which.Should().BeOfType<StoredErrorMessage>()
            .Which.Details.Should().Be("System.IO.IOException: Идет закрытие канала.");
    }

    // Ошибка без деталей — прежнее поведение (в т.ч. старые записи истории)
    [Fact]
    public async Task OnErrorAsync_БезДеталей_ПрежнееПоведение()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);

        await acc.OnErrorAsync("Ход прерван", _histSvc);

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().ContainSingle()
            .Which.Should().BeOfType<StoredErrorMessage>()
            .Which.Details.Should().BeNull();
    }

    [Fact]
    public void GetAll_CombinesOldHistoryAndCurrentTurn()
    {
        var history = new List<StoredMessage> { new StoredTextMessage("old") };
        var acc = new TurnAccumulator(history);
        acc.OnUserMessage("new", []);

        var all = acc.GetAll();
        all.Should().HaveCount(2);
        all[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("old");
        all[1].Should().BeOfType<StoredUserMessage>().Which.Text.Should().Be("new");
    }

    [Fact]
    public async Task OnResultAsync_AfterFlush_CurrentTurnCleared()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnUserMessage("msg1", []);
        await acc.OnResultAsync("done", 500, 1, null, null, null, null, _histSvc);

        // второй тёрн
        acc.OnUserMessage("msg2", []);
        var all = acc.GetAll();
        // история (1 user + 1 result) + текущий (1 user) = 3
        all.Should().HaveCount(3);
    }

    // Прод 2026-08-02 (находка Веры): в длинном ответе координатора вызов инструмента между
    // открывающим и закрывающим тегом маркера дёргает FlushBuffers ДО того, как закрытие
    // пришло следующей дельтой — раньше пара искалась в каждом куске между flush'ами
    // независимо, и половина маркера (то открывающий тег, то осиротевший закрывающий)
    // утекала в сохранённую историю буквально.
    [Fact]
    public async Task Маркер_РазъехалсяПоFlushBuffers_НеПротекаетИВырезаетсяЦеликом()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta("Договорились. <team:work>сделать экспорт");
        acc.OnToolUse("t1", "bash", new { }); // FlushBuffers посреди маркера
        acc.OnTextDelta("</team> Готово.");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc);

        var texts = acc.GetAll().OfType<StoredTextMessage>().Select(m => m.Text).ToList();
        texts.Should().OnlyContain(t => !t.Contains("<team:work>") && !t.Contains("</team"));
        string.Concat(texts).Should().Be("Договорились.  Готово.");
    }

    // Симметрично живой трансляции (ЖиваяТрансляция_ХодОборванПослеНезавершённогоХвоста в
    // SessionManagerTests): маркер, который так и не закрылся к концу хода (обрыв), не должен
    // теряться молча — конец хода довешивает его как обычный текст, а не съедает навсегда.
    [Fact]
    public async Task Маркер_НеЗакрылсяККонцуХода_ДовешиваетсяКакОбычныйТекстВИстории()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta("Собираю план, минуту <team:wo");
        await acc.OnErrorAsync("оборвался", _histSvc);

        var texts = acc.GetAll().OfType<StoredTextMessage>().Select(m => m.Text).ToList();
        string.Concat(texts).Should().Be("Собираю план, минуту <team:wo");
    }

    // B4 «Доклада о завершении задачи»: постановщику нечего решать — ход отвечает ровно
    // маркером молчания. В истории от такого хода не должно остаться ни одного поста:
    // после стрижки текста нет, а пустой (пробельный) пост дал бы призрачный пузырь в ленте.
    [Theory]
    [InlineData("<no-reply/>")]
    [InlineData("\n<no-reply/>\n")]
    [InlineData("  <no-reply />  ")]
    public async Task МаркерМолчания_ХодИзОдногоМаркера_НеПишетПостВИсторию(string turn)
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta(turn);
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc);

        acc.GetAll().OfType<StoredTextMessage>().Should().BeEmpty();
        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().ContainSingle().Which.Should().BeOfType<StoredResultMessage>(
            "от пустого хода в истории остаётся только служебный итог хода, но не реплика");
    }

    // Снимок посреди хода (SaveSnapshotAsync после каждого tool_result) идёт тем же путём —
    // маркер молчания не должен просочиться в историю до конца хода
    [Fact]
    public async Task МаркерМолчания_СнимокПосредиХода_НеПишетПостВИсторию()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta("<no-reply/>");
        await acc.SaveSnapshotAsync(_histSvc);

        acc.GetAll().OfType<StoredTextMessage>().Should().BeEmpty();
    }

    // Решение по делу есть — реплика сохраняется целиком, вырезан только маркер
    [Fact]
    public async Task МаркерМолчания_ПослеТекста_ТекстСохраняетсяЦеликом()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta("Ставлю новую задачу на Дениса.");
        acc.OnTextDelta("<no-reply/>");
        await acc.OnResultAsync("success", 100, 1, null, null, null, null, _histSvc);

        acc.GetAll().OfType<StoredTextMessage>().Select(m => m.Text)
            .Should().ContainSingle().Which.Should().Be("Ставлю новую задачу на Дениса.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
