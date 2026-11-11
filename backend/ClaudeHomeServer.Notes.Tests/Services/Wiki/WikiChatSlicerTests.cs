using System.Text;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Нарезка чата кодом (S6j, ADR-035 §12б этап 4): остаются реплики человека и ответы агента,
// всё остальное — вырезано; всё через SecretRedactor; номер m{N} — индекс записи истории;
// дельта режется на окна ≤35k токенов с перекрытием ровно 2 реплики; выдержка — ≤8 КБ целыми
// репликами; начало ветки — точка ветвления; записка-передача (первое пользовательское
// сообщение с серверной меткой) вырезана. Анти-петля реплик здесь нет: признака манифеста
// хода в истории нет (RecallManifestMessage — только клиенту и памяти SessionManager).
public class WikiChatSlicerTests
{
    private static List<StoredMessage> History(params StoredMessage[] messages) => [.. messages];

    // —— c1: состав выдержки и номера ——

    [Fact]
    public void Lines_режетВсёСлужебное_номераЭтоИндексыЗаписей()
    {
        var history = History(
            new StoredSessionStartedMessage("claude", "default"),                                     // m1 служебное
            new StoredUserMessage("Продолжаем разговор из чата «Прежний». Передаточная записка:\nитог прежнего"), // m2 записка-передача
            new StoredUserMessage("Решили: нарезку делает код."),                                     // m3 человек ✓
            new StoredTextMessage("Принято, режу по диапазону."),                                   // m4 агент ✓
            new StoredToolUseMessage { Id = "t1", Name = "BashToolCall", Result = "BashOutput" },   // m5 вызов инструмента
            new StoredThinkingMessage("ThinkingText"),                                              // m6 размышление
            new StoredFileChangedMessage("FileChangedPath", 1, 0),                                 // m7 file_changed
            new StoredUserMessage("AutoUtterance", auto: true),                                    // m8 auto-реплика
            new StoredUserMessage("SystemDirectiveUtterance", systemDirective: true),             // m9 директива
            new StoredUserMessage("ViaAgentUtterance", viaAgent: true),                           // m10 через агента
            new StoredUserMessage("StaffNoteUtterance", staffNote: "ответ на карточку"),          // m11 плашка штаба
            new StoredTextMessage("SubagentText", parentToolUseId: "t1"),                         // m12 текст сабагента
            new StoredResultMessage("success", 10, 1),                                            // m13 result
            new StoredUserMessage("   "),                                                         // m14 пустая
            new StoredUserMessage("Вторая реплика человека"));                                     // m15 человек ✓

        var lines = WikiChatSlicer.Lines(history, 1, history.Count);

        // Номера m{N} — те же индексы записей истории (1-based), как их читает TryParseSessionRef.
        lines.Select(l => l.Index).Should().Equal(3, 4, 15);
        lines.Select(l => l.IsHuman).Should().Equal(true, false, true);

        var joined = string.Join("\n", lines.Select(l => l.Text));
        joined.Should().NotContain("BashToolCall").And.NotContain("BashOutput", "0 вызовов инструментов и их вывода");
        joined.Should().NotContain("ThinkingText", "0 размышлений");
        joined.Should().NotContain("FileChangedPath", "0 file_changed");
        joined.Should().NotContain("AutoUtterance", "0 auto-реплик");
        joined.Should().NotContain("SystemDirectiveUtterance", "0 системных директив");
        joined.Should().NotContain("ViaAgentUtterance", "0 реплик через агента");
        joined.Should().NotContain("StaffNoteUtterance", "0 служебных плашек штаба");
        joined.Should().NotContain("SubagentText", "тексты сабагентов — вывод tool_use");
        joined.Should().NotContain("Передаточная", "записка-передача вырезана");
        joined.Should().NotContain("claude", "служебные события (session_started/result) вырезаны");
    }

    // Записка-передача — только первое пользовательское сообщение (ContinueAsync шлёт её сразу
    // после создания чата). Метку проверяем по ней: реплика человека в середине чата, цитирующая
    // ту же строку, — обычная реплика и остаётся (ограниченный риск правила, не молчаливый).
    [Theory]
    [InlineData("Продолжаем разговор из чата «Прежний». Передаточная записка:\nитог")]
    [InlineData("Ключевое решение прежднего чата.\n\nПредыдущий чат: [Прежний](#/chats/abc)")]
    public void Lines_запискаПередача_первоеСообщениеСМеткой_Вырезана(string handoffText)
    {
        var history = History(
            new StoredUserMessage(handoffText),
            new StoredUserMessage("первая настоящая реплика"));

        var lines = WikiChatSlicer.Lines(history, 1, history.Count);
        lines.Select(l => l.Index).Should().Equal(2);

        var midChat = History(
            new StoredUserMessage("обычная первая реплика"),
            new StoredUserMessage("Предыдущий чат: [Прежний](#/chats/abc) — вспомни тот разбор"));
        WikiChatSlicer.Lines(midChat, 1, midChat.Count).Select(l => l.Index).Should().Equal(1, 2);
    }

    // —— границы диапазона и пустой ввод ——

    [Fact]
    public void Lines_пустаяИсторияИПеревёрнутыйДиапазон_пустоБезИсключения()
    {
        WikiChatSlicer.Lines(null, 1, 5).Should().BeEmpty();
        WikiChatSlicer.Lines(new List<StoredMessage>(), 1, 5).Should().BeEmpty();
        var history = History(new StoredUserMessage("реплика"));
        WikiChatSlicer.Lines(history, 2, 1).Should().BeEmpty();
        WikiChatSlicer.Lines(history, 5, 9).Should().BeEmpty("диапазон за концом истории");
        // from ниже 1 поднимается до 1: m{0} не существует, молчаливой ошибки нет
        WikiChatSlicer.Lines(history, 0, 1).Select(l => l.Index).Should().Equal(1);
        // одиночный m{a}..m{a} — рабочий случай
        WikiChatSlicer.Lines(history, 1, 1).Select(l => l.Index).Should().Equal(1);
    }

    // —— начало ветки — точка ветвления (§12б этап 3) ——

    [Fact]
    public void Lines_началоВетки_точкаВетвления_префиксРодителяНеИдёт()
    {
        var history = History(
            new StoredUserMessage("РепликаПрефикса"),
            new StoredTextMessage("ОтветПрефикса"),
            new StoredBranchedFromMessage { SourceSessionId = "p", SourceName = "Родитель" },
            new StoredUserMessage("РепликаПослеВетвления"));

        WikiChatSlicer.BranchPoint(history).Should().Be(3, "номер плашки — 1-based индекс её записи");
        WikiChatSlicer.BranchPoint(History(new StoredUserMessage("обычный чат"))).Should().Be(0);

        // from, попавший в префикс, поднимается за плашку: префикс — ответственность родителя.
        WikiChatSlicer.Lines(history, 1, history.Count).Select(l => l.Index).Should().Equal(4);
        // явный диапазон внутри ветки — честно как дан.
        WikiChatSlicer.Lines(history, 4, 4).Select(l => l.Index).Should().Equal(4);
        // диапазон целиком в префиксе — пусто: слайсер не цитирует чужую половину.
        WikiChatSlicer.Lines(history, 1, 2).Should().BeEmpty();
    }

    // —— c2: секреты не попадают в выдержку ——

    [Fact]
    public void Excerpt_секретФорматаИТочныйСекрет_НеПопадаютВВыдержку()
    {
        var key = "sk-ant-api03-" + new string('a', 30);                 // формат из тестов SecretRedactor
        var exact = "точное-значение-секрета-инстанса-9876543210";       // длиннее MinExactSecretLength

        var history = History(
            new StoredUserMessage($"Ключ провайдера {key}, второй ключ {exact}."));

        var excerpt = WikiChatSlicer.Excerpt(history, 1, 1, new[] { exact });

        excerpt.Should().NotContain(key);
        excerpt.Should().NotContain(exact);
        excerpt.Should().Contain("[REDACTED:token]");
        excerpt.Should().Contain("[REDACTED:instance-secret]");
        // реплика при этом остаётся: редация маскирует секрет, а не выбрасывает реплику
        excerpt.Should().Contain("Ключ провайдера");
    }

    // —— c2: выдержка обрезана до 8 КБ на границе целой реплики ——

    [Fact]
    public void Excerpt_обрезанДо8КБ_ТолькоЦелымиРепликами()
    {
        var longText = new string('к', 3000); // 6000 байт UTF-8 на реплику
        var history = History(
            new StoredUserMessage(longText),
            new StoredTextMessage(longText),
            new StoredUserMessage("РепликаНеПомещается"));

        var excerpt = WikiChatSlicer.Excerpt(history, 1, 3);

        Encoding.UTF8.GetByteCount(excerpt).Should().BeLessThanOrEqualTo(WikiChatSlicer.MaxExcerptBytes);
        excerpt.Should().Contain(longText);
        excerpt.Should().NotContain("РепликаНеПомещается");
        // оборванной реплики нет: обрезка — на границе, дословность QuoteMatcher по целым
        excerpt.Should().NotContain("Ассистент:");
    }

    // Пустой диапазон — пустая выдержка (не исключение): вызывающий отвергает с причиной.
    [Fact]
    public void Excerpt_НечегоЦитировать_пустаяСтрока()
    {
        var history = History(
            new StoredResultMessage("success", 10, 1),
            new StoredThinkingMessage("думаю"));
        WikiChatSlicer.Excerpt(history, 1, 2).Should().BeEmpty();
    }

    // —— c5: окна ≤35k токенов с перекрытием ровно 2 реплики ——

    [Fact]
    public void Windows_Дельта70kТокенов_РежетсяНаОкна_СПерекрытиемРовно2()
    {
        // 70 реплик по ~3000 симв. ≈ 70k токенов по мере проекта (симв./3).
        var messages = new StoredMessage[70];
        for (var i = 0; i < 70; i++)
            messages[i] = new StoredUserMessage($"{i:D4} " + new string('к', 2996));

        var windows = WikiChatSlicer.Windows(History(messages), 1, messages.Length);

        windows.Count.Should().BeGreaterThan(1, "70k токенов не помещаются в одно окно");
        foreach (var w in windows)
        {
            w.Lines.Should().NotBeEmpty();
            // та же мера, что у алгоритма: сумма оценок строк окна
            w.Lines.Sum(l => l.Text.Length / WikiChatSlicer.CharsPerTokenEstimate)
                .Should().BeLessThanOrEqualTo(WikiChatSlicer.WindowTokens);
        }

        for (var i = 1; i < windows.Count; i++)
        {
            var prev = windows[i - 1].Lines.Select(l => l.Index).ToList();
            var cur = windows[i].Lines.Select(l => l.Index).ToList();
            cur.Take(2).Should().Equal(prev.Skip(prev.Count - 2),
                "перекрытие — ровно последние 2 реплики предыдущего окна");
            cur.Skip(2).Intersect(prev).Should().BeEmpty("ничего сверх двух реплик окна не повторяют");
        }

        // каждая реплика дельты вошла хотя бы в одно окно
        windows.SelectMany(w => w.Lines).Select(l => l.Index).Distinct()
            .Should().HaveCount(70);
    }

    // Одна реплика длиннее окна не режется: окно из неё одной превышает меру честно —
    // цитата из оборванной реплики не дословна (тот же отказ, что у обрезки выдержки).
    [Fact]
    public void Windows_ОднаРепликаБольшеОкна_НеРежетсяЦеликом()
    {
        var huge = new string('к', WikiChatSlicer.WindowTokens * WikiChatSlicer.CharsPerTokenEstimate + 100);
        var windows = WikiChatSlicer.Windows(History(new StoredUserMessage(huge)), 1, 1);

        windows.Should().HaveCount(1);
        windows[0].Lines.Should().HaveCount(1);
        windows[0].Lines[0].Text.Length.Should().Be(huge.Length, "реплика не резается: окно превышает меру честно");
    }

    [Fact]
    public void Windows_ПустаяДельта_ПустоБезИсключения()
    {
        WikiChatSlicer.Windows(null, 1, 5).Should().BeEmpty();
        WikiChatSlicer.Windows(History(new StoredResultMessage("success", 1, 1)), 1, 1).Should().BeEmpty();
    }

    // —— S2-5 (ADR-039, решение 1): реплики процесса понижаются в тексте окна ——

    // Фикстура: 12 реплик — 4 человека (m1, m6, m9, m12), четыре агентских процесса
    // (m3 «Сверка: чисто», m4 коммит-отчёт, m5 id карточки в строке-статусе, m8 вердикт),
    // двухстрочный процесс (m10), смешанная (m11) и содержательная (m7) агентские.
    // Значения Lines/Excerpt зафиксированы на этой базе: фильтр процесса их не трогает;
    // в тексте окна каждая процессная реплика — одна строка-маркер без исходника (S2-5).
    private static List<StoredMessage> ProcessFixture() => History(
        new StoredUserMessage("Решили: нарезку делает код."),                                       // m1
        new StoredTextMessage("Принято, режу по диапазону."),                                      // m2
        new StoredTextMessage("Сверка: чисто"),                                                  // m3 процесс
        new StoredTextMessage("Коммит: 161c49c81"),                                              // m4 процесс
        new StoredTextMessage("Задача 599005ea заверена"),                                      // m5 процесс
        new StoredUserMessage("Сверка: чисто"),                                                  // m6 человек — та же фраза
        new StoredTextMessage("Правку внёс в ClaudeSession.cs:859, тесты 17/17 зелёные."),       // m7 содержательная
        new StoredTextMessage("Вердикт: accepted"),                                             // m8 процесс
        new StoredUserMessage("Что дальше с окном?"),                                           // m9 человек
        new StoredTextMessage("Сверка: чисто\nКоммит: d341879bf"),                             // m10 процесс, две строки
        new StoredTextMessage("Сверка: чисто\nСодержательная позиция по делу."),               // m11 смешанная
        new StoredUserMessage("Голос обновлён? — нет, это вопрос человека"));                   // m12 человек

    [Fact]
    public void Lines_и_Excerpt_процесс_не_трогают_фикстура_12()
    {
        var history = ProcessFixture();
        var lines = WikiChatSlicer.Lines(history, 1, history.Count);

        // Состав и порядок Lines — те же, что на базе до фильтра: процесс остаётся строкой.
        lines.Select(l => l.Index).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
        lines.Count(l => l.IsHuman).Should().Be(4);
        lines.Select(l => l.IsHuman).Should().Equal(true, false, false, false, false, true, false, false, true, false, false, true);

        // Выдержка — дословная, с несжатыми строками процесса (на ней стоит QuoteMatcher):
        var excerpt = WikiChatSlicer.Excerpt(history, 1, history.Count);
        excerpt.Should().Contain("Сверка: чисто");
        excerpt.Should().Contain("Коммит: 161c49c81");
        excerpt.Should().Contain("Задача 599005ea заверена");
        excerpt.Should().Contain("Вердикт: accepted");
        excerpt.Should().Contain("Сверка: чисто\nКоммит: d341879bf", "двухстрочный процесс в выдержке не сжимается");
        excerpt.Should().Contain("Содержательная позиция по делу.");
        excerpt.Should().NotContain("[процесс]", "выдержка понижения не знает");
    }

    [Fact]
    public void Windows_репликаПроцесса_сжимаетсяОднойСтрокой_нумерацияТаЖе()
    {
        var history = ProcessFixture();
        var windows = WikiChatSlicer.Windows(history, 1, history.Count);

        windows.Should().HaveCount(1, "фикстура меньше окна: одно окно");
        var w = windows[0].Lines;
        // порядок и нумерация реплик окна сохраняются
        w.Select(l => l.Index).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
        w.Select(l => l.IsHuman).Should().Equal(true, false, false, false, false, true, false, false, true, false, false, true);

        // Каждая процессная реплика — одна фиксированная строка-маркер без исходного текста.
        w[2].Text.Should().Be("[процесс] служебная реплика");
        w[3].Text.Should().Be("[процесс] служебная реплика");
        w[4].Text.Should().Be("[процесс] служебная реплика");
        w[7].Text.Should().Be("[процесс] служебная реплика");
        // Двухстрочный процесс — та же одна строка: без выдержки и без перевода строки (S2-5).
        w[9].Text.Should().Be("[процесс] служебная реплика");

        // Та же фраза от человека — без изменений, никогда (м12 — тоже человек).
        w[5].Text.Should().Be("Сверка: чисто");
        w[11].Text.Should().Be("Голос обновлён? — нет, это вопрос человека");
        // Смешанная реплика (одна строка процесса + одна содержательная) — без изменений.
        w[6].Text.Should().Be("Правку внёс в ClaudeSession.cs:859, тесты 17/17 зелёные.");
        w[10].Text.Should().Be("Сверка: чисто\nСодержательная позиция по делу.");
    }

    // Инвариант S2-5 (Major 1-2 ревью 6cbc5e0a): ни одна строка реплики процесса не может
    // стать дословной цитатой разбора. Двухстрочная процессная реплика пониждается в одну
    // строку-маркер: ни первая, ни вторая строка исходника не находится в тексте окна
    // через QuoteMatcher.ContainsVerbatim.
    [Fact]
    public void Windows_двухстрочныйПроцесс_неСтановитсяЦитатой()
    {
        var twoLine = "Сверка: чисто\nКоммит: d341879bf";
        var windows = WikiChatSlicer.Windows(History(new StoredTextMessage(twoLine)), 1, 1);

        var text = windows[0].Lines[0].Text;
        text.Should().StartWith("[процесс] ");
        text.Should().NotContain("\n", "пометка покрывает всю реплику: одна строка окна");

        var windowText = string.Join("\n", windows[0].Lines.Select(l => l.Text));
        QuoteMatcher.ContainsVerbatim(windowText, "Сверка: чисто").Should().BeFalse();
        QuoteMatcher.ContainsVerbatim(windowText, "Коммит: d341879bf").Should().BeFalse();
    }

    // Мера окна — по исходным строкам: сжатие после нарезки не двигает границы окон.
    [Fact]
    public void Windows_мераОкна_поИсходнымСтрокам_сжатиеГраницыНеСдвигает()
    {
        // 36×3000 симв. ≈ 36k токенов по мере проекта — за границу одного окна (35k).
        // Если бы мера считалась по сжатым строкам (по ~130 симв.), всё влезло бы в одно.
        var messages = new StoredMessage[36];
        for (var i = 0; i < 36; i++)
            messages[i] = i % 2 == 0
                ? new StoredTextMessage($"Коммит: {i:x8} " + new string('к', 2987))
                : new StoredUserMessage($"{i:D4} " + new string('к', 2996));

        var windows = WikiChatSlicer.Windows(History(messages), 1, messages.Length);

        windows.Count.Should().BeGreaterThan(1, "мера по исходным строкам, а не по сжатым");
        var indices = windows.SelectMany(w => w.Lines.Select(l => l.Index)).ToList();
        // все 36 реплик покрыты, повтор — только в пределах перекрытия ровно в 2 реплики
        indices.Distinct().Should().HaveCount(36);
        indices.GroupBy(i => i).Where(g => g.Count() > 2).Should().BeEmpty("повтор за пределами перекрытия");
        for (var i = 1; i < windows.Count; i++)
        {
            var prev = windows[i - 1].Lines.Select(l => l.Index).ToList();
            windows[i].Lines.Take(2).Select(l => l.Index)
                .Should().Equal(prev.Skip(prev.Count - 2));
        }
        // понижение дошло до текста окна
        windows.Should().Contain(w => w.Lines.Any(l => l.Text.StartsWith("[процесс] ")));
    }

    // —— WikiProcessPatterns: мера «каждая непустая строка» ——

    [Theory]
    [InlineData("Сверка: чисто")]
    [InlineData("Реши, что дальше: продолжаем работу")]
    [InlineData("Голос обновлён")]
    [InlineData("Персона-исполнитель Код-ревьюер (Глеб) завершила делегированную тобой задачу")]
    [InlineData("Вердикт: accepted")]
    [InlineData("Вердикт: returned")]
    [InlineData("Вердикт: accepted_with_remarks")]
    [InlineData("Коммит: 161c49c81")]
    [InlineData("коммит d341879bf починен")]
    [InlineData("Задача 599005ea заверена")]
    [InlineData("Карточка 2ff5b90d принята")]
    [InlineData("/home/an/Sources/ClaudeCodeServer-wiki-synth — работаю здесь")]
    [InlineData("Сверка: чисто\nКоммит: d341879bf")]
    [InlineData("  Сверка: чисто  \n\n")]
    public void IsProcessReply_шаблонныеСтроки_истинно(string text)
    {
        WikiProcessPatterns.IsProcessReply(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Сверка: чисто\nСодержательная позиция по делу.")]
    [InlineData("Правку внёс в ClaudeSession.cs:859, тесты 17/17 зелёные.")]
    [InlineData("Сверка: заявления субагента совпадают с реальным кодом — разбор ниже")]
    [InlineData("Задача про фильтр процесса — её делает Кузьма")]
    [InlineData("Вердикт: что-то ещё")]
    [InlineData("Сверка")]
    [InlineData("Задача 599005ea")]
    public void IsProcessReply_содержательныеИПустые_ложь(string? text)
    {
        WikiProcessPatterns.IsProcessReply(text).Should().BeFalse();
    }
}
