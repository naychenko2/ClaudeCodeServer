using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S6e-1 (ADR-035, решения 12а/12б): стор разбора чатов keeper/chat-triage.json — курсор
// «разобрано до записи N» + хэш записи N, зона и причина исхода, счётчик «нечего добавить»
// подряд. Запись расширяемая: чужие поля (будущих шагов) при перезаписи сохраняются.
// Битый/пустой файл — «невалиден» с причиной, не исключение и не «пустой стор»; отсутствие
// файла — норма. С S6f (тесты 3a/3b): score/signals — известные поля (round-trip равный,
// чужая форма — Invalid). Доработка S6f (ревью M1/M2): у стора два писателя, владение
// разведено — UpdateScoring правит score/signals и stage1Zone/stage1Reason и не трогает
// поля разбора (курсор, zone, reason, nothingNewStreak), а Upsert разбора переносит поля
// скорера из прежней записи.
// J-P1b: ResetCursors — сброс полей разбора по всем записям (после чистки старой вики);
// балл, ступени 1–2 и чужие поля (в т.ч. пометка человека) сохраняются; гонка сброса с
// апдейтом одной записи — под тем же замком корня.
public class WikiChatTriageStoreTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Date = "2026-10-20T12:00:00.0000000+00:00";

    private readonly string _root;

    public WikiChatTriageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wiki_triage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ─── 1. Отсутствие файла — норма; первая запись создаёт файл ───────────

    [Fact]
    public void ФайлаНет_GetПустоValid_ПервыйUpsertСоздаётФайлПодКорнем()
    {
        var res = WikiChatTriageStore.Get(_root);
        res.State.Should().Be(WikiTriageFileState.Valid, "стора до первого разбора — это пустой стор, не поломка");
        res.Entries.Should().BeEmpty();
        File.Exists(TriageFile()).Should().BeFalse();

        var up = WikiChatTriageStore.Upsert(_root, Entry("chat-a", 3, Hash, "nothing_new", "нет новых утверждений"));
        up.Ok.Should().BeTrue(up.Reason);
        File.Exists(TriageFile()).Should().BeTrue();

        var disk = WikiChatTriageStore.Get(_root);
        disk.State.Should().Be(WikiTriageFileState.Valid);
        disk.Entries.Should().BeEquivalentTo([up.Entry]);
    }

    // ─── 2. Запись со всеми полями читается с диска равной (c1) ────────────

    [Fact]
    public void ДваЧата_ОбеЗаписиСДискаСоВсемиПолями_РавныЗаписанным()
    {
        var a = Entry("chat-a", 12, Hash, "nothing_new", "нет новых утверждений", 1);
        var b = Entry("chat-b", 7, null, "triage", null, 0);
        var ra = WikiChatTriageStore.Upsert(_root, a);
        var rb = WikiChatTriageStore.Upsert(_root, b);
        ra.Ok.Should().BeTrue(ra.Reason);
        rb.Ok.Should().BeTrue(rb.Reason);

        var disk = WikiChatTriageStore.Get(_root);
        disk.State.Should().Be(WikiTriageFileState.Valid);
        // Равенство всей записи, включая дату сервера из возврата: повторный читатель стора
        // (S6e-2) должен видеть ровно то, что поставил разбирающий.
        disk.Entries.Should().BeEquivalentTo([ra.Entry, rb.Entry]);
        var readA = disk.Entries.Single(e => e.SessionId == "chat-a");
        readA.Cursor.Should().Be(12);
        readA.CursorHash.Should().Be(Hash);
        readA.Zone.Should().Be("nothing_new");
        readA.Reason.Should().Be("нет новых утверждений");
        readA.NothingNewStreak.Should().Be(1);

        // Причина читается человеком в файле: \u-escapes сделали бы её нечитаемой.
        File.ReadAllText(TriageFile(), new UTF8Encoding(false)).Should().Contain("нет новых утверждений");
    }

    // ─── 3. Расширяемая запись: чужие поля не теряются при перезаписи (c1) ─

    [Fact]
    public void НеизвестноеПолеЗаписи_СохраняетсяПриПравке_ИПриЧужомUpsert()
    {
        // Поля будущих шагов («future», «futureObj»): стор их не знает, но правка курсора не
        // должна стирать то, что написал другой шаг. (score/signals с S6f — известные поля,
        // их round-trip — в тесте 3a.)
        WriteRaw("""
            [{"sessionId":"chat-a","cursor":4,"cursorHash":null,"zone":"nothing_new","reason":null,
              "nothingNewStreak":2,"triagedAt":"2026-10-20T12:00:00+00:00",
              "future":{"k":1},"futureObj":"значение"}]
            """);

        var up = WikiChatTriageStore.Upsert(_root, Entry("chat-a", 9, Hash, "triage", "есть утверждения"));
        up.Ok.Should().BeTrue(up.Reason);

        var raw = File.ReadAllText(TriageFile(), new UTF8Encoding(false));
        raw.Should().Contain("\"future\"");
        raw.Should().Contain("значение");

        var disk = WikiChatTriageStore.Get(_root);
        disk.State.Should().Be(WikiTriageFileState.Valid);
        disk.Entries.Should().ContainSingle(e =>
            e.SessionId == "chat-a" && e.Cursor == 9 && e.CursorHash == Hash && e.NothingNewStreak == 0,
            "известные поля обновлены, чужие — нет (их модель не читает и не трогает)");

        // Правка чужого чата тоже не задевает чужих полей первой записи.
        WikiChatTriageStore.Upsert(_root, Entry("chat-b", 1)).Ok.Should().BeTrue();
        File.ReadAllText(TriageFile()).Should().Contain("\"future\"");
    }

    // ─── 3a. Балл S6f: score/signals — известные поля, round-trip равный; чужая форма — Invalid ───

    [Fact]
    public void БаллИСигналы_ЧитываютсяРавноЗаписанному_ШтрафОтрицательнымВесом()
    {
        var up = WikiChatTriageStore.Upsert(_root, Entry("chat-a", 3, Hash, "triage", "балл 17", 0,
            score: 17, signals: [new("decision_words", 8), new("human_turns", 6), new("nothing_new_penalty", -10)]));
        up.Ok.Should().BeTrue(up.Reason);

        var disk = WikiChatTriageStore.Get(_root);
        disk.State.Should().Be(WikiTriageFileState.Valid);
        var read = disk.Entries.Single(e => e.SessionId == "chat-a");
        read.Score.Should().Be(17);
        read.Signals.Should().Equal(up.Entry!.Signals, "порядок и веса сигналов — часть причины, они не переставляются");

        // Запись S6e (без балла) читается с null-полем — adдитивность поля не ломает старый файл.
        WriteRaw("""[{"sessionId":"old","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00"}]""");
        var old = WikiChatTriageStore.Get(_root).Entries.Single();
        old.Score.Should().BeNull();
        old.Signals.Should().BeNull();
        old.Stage1Zone.Should().BeNull();
        old.Stage1Reason.Should().BeNull();

        // Запись до разведения владения (балл и общий zone написаны пересчётом, stage1Zone
        // нет) читается равной: новые поля необязательны, старый файл не пересобирается.
        WriteRaw("""[{"sessionId":"old2","cursor":2,"zone":"triage","triagedAt":"2026-10-20T12:00:00+00:00","score":17,"signals":[{"name":"human_turns","weight":6}]}]""");
        var legacy = WikiChatTriageStore.Get(_root).Entries.Single();
        legacy.Zone.Should().Be("triage");
        legacy.Score.Should().Be(17);
        legacy.Stage1Zone.Should().BeNull("своей зоны ступени 1 у старой записи нет");
    }

    [Theory]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","score":-1}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","score":"много"}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","signals":"решения"}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","signals":["решения","факты"]}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","signals":[{"name":"x"}]}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","signals":[{"name":"","weight":3}]}]""")]
    public void ИспорченныйБаллИлиСигналы_Invalid_БезИсключения(string json)
    {
        WriteRaw(json);
        WikiChatTriageStore.Get(_root).State.Should().Be(WikiTriageFileState.Invalid,
            "известное поле испорченной формы — тот же класс повреждения файла, что испорченный курсор");
    }

    // ─── 3b. UpdateScoring (доработка S6f): правит только свои поля, поля разбора — нет ───

    [Fact]
    public void UpdateScoring_ИсходРазбораСохранён_БаллИЗонаСтупени1Записаны()
    {
        // Существующая запись с продвинутым курсором и исходом «нечего добавить»: пересчёт
        // балла не владеет ни курсором, ни исходом разбора — своя зона у него в своём поле.
        WikiChatTriageStore.Upsert(_root, Entry("chat-a", 42, Hash, "nothing_new", "нет новых", 2)).Ok.Should().BeTrue();
        var up = WikiChatTriageStore.UpdateScoring(_root, "chat-a", 17,
            [new("decision_words", 8), new("human_turns", 9)], "triage", "балл 17: …");
        up.Ok.Should().BeTrue(up.Reason);

        var read = WikiChatTriageStore.Get(_root).Entries.Single();
        read.Cursor.Should().Be(42, "пересчёт не двигает курсор разбора");
        read.CursorHash.Should().Be(Hash);
        read.NothingNewStreak.Should().Be(2, "счётчик ведёт исход разбора, не пересчёт");
        read.Zone.Should().Be("nothing_new", "зону исхода ставит разбор: пересчёт её не перекрывает");
        read.Reason.Should().Be("нет новых", "причина исхода — тоже поле разбора");
        read.Score.Should().Be(17);
        read.Signals.Should().Equal(new List<WikiTriageSignal> { new("decision_words", 8), new("human_turns", 9) });
        read.Stage1Zone.Should().Be("triage", "своя зона скорера — в отдельном поле (M1)");
        read.Stage1Reason.Should().Be("балл 17: …");
        read.TriagedAt.Should().BeOnOrAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void РазборПослеПересчёта_БаллСигналыИЗонаСтупени1Сохранены()
    {
        // Порядок «пересчёт → ночной разбор» на одном чате: разбор двигает курсор и ставит
        // свой исход, но не имеет права стирать посчитанный балл и зону ступени 1 (M2).
        var scored = WikiChatTriageStore.UpdateScoring(_root, "chat-a", 17,
            [new("decision_words", 8)], "triage", "балл 17: …").Entry!;
        var up = WikiChatTriageStore.Upsert(_root, Entry("chat-a", 44, Hash, "nothing_new", "нет новых", 1));
        up.Ok.Should().BeTrue(up.Reason);

        var read = WikiChatTriageStore.Get(_root).Entries.Single();
        read.Cursor.Should().Be(44, "курсор — поле разбора: он двигается");
        read.Zone.Should().Be("nothing_new");
        read.Reason.Should().Be("нет новых");
        read.NothingNewStreak.Should().Be(1);
        read.Score.Should().Be(17, "балл — поле скорера: разбор его не стирает");
        read.Signals.Should().Equal(scored.Signals);
        read.Stage1Zone.Should().Be("triage");
        read.Stage1Reason.Should().Be("балл 17: …");
    }

    [Fact]
    public void UpdateScoring_ЗаписиНет_НоваяСКурсоромНоль_ПлохаяФормыВхода_ОтказShape()
    {
        var up = WikiChatTriageStore.UpdateScoring(_root, "chat-new", 4, [], "gray", "балл 4");
        up.Ok.Should().BeTrue(up.Reason);
        var read = WikiChatTriageStore.Get(_root).Entries.Single();
        read.Cursor.Should().Be(0);
        read.Signals.Should().BeEmpty("пустой список сигналов — законен (балл без сигналов), null — то, что балла нет");

        WikiChatTriageStore.UpdateScoring(_root, "", 1, null, null, null).Code.Should().Be(WikiChatTriageStore.CodeShape);
        WikiChatTriageStore.UpdateScoring(_root, "chat-b", -3, null, null, null).Code.Should().Be(WikiChatTriageStore.CodeShape);
        File.ReadAllText(TriageFile()).Should().NotContain("chat-b");
    }

    // ─── 4. Битый/пустой файл: Invalid, Get не бросает, Upsert отказ и не пишет (c3) ───

    [Theory]
    [InlineData("{ не json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"не массив\"")]
    [InlineData("{}")]
    public void БитыйИлиПустойФайл_Invalid_GetБезИсключения_UpsertОтказывает_ФайлНеПерезаписан(string broken)
    {
        WriteRaw(broken);

        var read = WikiChatTriageStore.Get(_root);
        read.State.Should().Be(WikiTriageFileState.Invalid);
        read.Entries.Should().BeEmpty("невалидный стор не притворяется пустым: его записи неизвестны");

        var up = WikiChatTriageStore.Upsert(_root, Entry("chat-a", 1));
        up.Ok.Should().BeFalse();
        up.Code.Should().Be(WikiChatTriageStore.CodeInvalid);
        File.ReadAllText(TriageFile()).Should().Be(broken,
            "битый файл не перезаписывается молча: его записи были бы стёрты без разбора (ADR-035:325)");
    }

    [Fact]
    public void ПустойМассив_ВалидныйПустойСтор_ЧислоВместоДаты_Invalid()
    {
        WriteRaw("[]");
        var read = WikiChatTriageStore.Get(_root);
        read.State.Should().Be(WikiTriageFileState.Valid, "пустой список — штатное состояние после удаления всех чатов");
        read.Entries.Should().BeEmpty();

        WriteRaw($$"""[{"sessionId":"a","cursor":1,"triagedAt":1730000000}]""");
        WikiChatTriageStore.Get(_root).State.Should().Be(WikiTriageFileState.Invalid,
            "дата числом — испорченное поле, а не дата");
    }

    // ─── 5. null-поля внутри валидного JSON: Invalid, не NRE (c3) ──────────

    [Theory]
    [InlineData("""[{"sessionId":null,"cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00"}]""")]
    [InlineData("[null]")]
    [InlineData("""[{"sessionId":"a","cursor":null,"triagedAt":"2026-10-20T12:00:00+00:00"}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":null}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1}]""")]
    [InlineData("""[{"sessionId":"a","cursor":-1,"triagedAt":"2026-10-20T12:00:00+00:00"}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","nothingNewStreak":-2}]""")]
    [InlineData("""[{"sessionId":"a","cursor":"abc","triagedAt":"2026-10-20T12:00:00+00:00"}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"не дата"}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00","zone":5}]""")]
    [InlineData("""[{"sessionId":"a","cursor":1,"triagedAt":"2026-10-20T12:00:00+00:00"},{"sessionId":"a","cursor":2,"triagedAt":"2026-10-20T12:00:00+00:00"}]""")]
    public void NullИлиИспорченноеПолеВВалидномJson_Invalid_БезИсключения_UpsertОтказ(string json)
    {
        WriteRaw(json);

        var read = WikiChatTriageStore.Get(_root);
        read.State.Should().Be(WikiTriageFileState.Invalid);
        read.Entries.Should().BeEmpty();

        var up = WikiChatTriageStore.Upsert(_root, Entry("chat-new", 1));
        up.Ok.Should().BeFalse();
        up.Code.Should().Be(WikiChatTriageStore.CodeInvalid);
        File.ReadAllText(TriageFile()).Should().Be(json);
    }

    // ─── 6. Форма входа: отказ до обращения к диску ────────────────────────

    [Theory]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("chat-a", -1)]
    public void ПлохаяФормаВхода_ОтказShape_ФайлНеСоздан(string sessionId, int cursor)
    {
        var up = WikiChatTriageStore.Upsert(_root, new WikiChatTriageEntry(sessionId, cursor, null, null, null, 0, default));
        up.Ok.Should().BeFalse();
        up.Code.Should().Be(WikiChatTriageStore.CodeShape);
        File.Exists(TriageFile()).Should().BeFalse("отказанная по форме запись не оставляется на диске");

        var negStreak = WikiChatTriageStore.Upsert(_root,
            new WikiChatTriageEntry("chat-a", 1, null, null, null, -1, default));
        negStreak.Code.Should().Be(WikiChatTriageStore.CodeShape);
    }

    // ─── 7. Дата ставит сервер; порядок записей фиксирован по ключу ───────

    [Fact]
    public void ДатуСтавитСервер_ЗначениеИзВходаИгнорируется()
    {
        var future = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var start = DateTimeOffset.UtcNow;
        var up = WikiChatTriageStore.Upsert(_root,
            new WikiChatTriageEntry("chat-a", 1, null, null, null, 0, future));
        up.Ok.Should().BeTrue(up.Reason);

        up.Entry!.TriagedAt.Should().NotBe(future).And.BeOnOrAfter(start);
        var disk = WikiChatTriageStore.Get(_root).Entries.Single();
        disk.TriagedAt.Should().Be(up.Entry.TriagedAt);
    }

    [Fact]
    public void ЗаписиУпорядоченыПоКлючу_ПерепостановкаПорядкаНеМеняет()
    {
        // Стор — данные в бэкапе: его diff между разборами должен быть читаемым, а не
        // зависеть от порядка вставки.
        foreach (var id in new[] { "c3", "c1", "c2" })
            WikiChatTriageStore.Upsert(_root, Entry(id, 1)).Ok.Should().BeTrue();
        Order().Should().Equal("c1", "c2", "c3");

        WikiChatTriageStore.Upsert(_root, Entry("c2", 2)).Ok.Should().BeTrue();
        Order().Should().Equal("c1", "c2", "c3");
    }

    // ─── 8. Гонка записи: цикл «прочитал → изменил → записал» атомарен (ревью S4a, MAJOR 1) ───

    [Fact]
    public async Task ДвадцатьПараллельныхUpsert_ВсеЗаписиНаДиске()
    {
        // Без блокировки каждый из двадцати пишет своё прочтение одного файла:
        // «последний» затёр бы остальных. Окно гонки широкое: стор заранее наполнен
        // 400 записями — чтение, правка и перезапись занимают миллисекунды, и все
        // двадцать, стартовавшие от барьера, пересекаются не случайно (с маленьким
        // файлом окно — микросекунды, и тест оставался бы зелёным даже без блокировки).
        for (var i = 0; i < 400; i++)
            WikiChatTriageStore.Upsert(_root, Entry($"seed{i:D4}", i)).Ok.Should().BeTrue($"seed {i}");

        var newIds = Enumerable.Range(0, 20).Select(i => $"new{i:D2}").ToArray();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = newIds.Select(id => Task.Run(async () =>
        {
            await barrier.Task;
            return WikiChatTriageStore.Upsert(_root, Entry(id, 5, Hash, "triage", null, 0));
        })).ToArray();
        barrier.SetResult();

        foreach (var t in tasks)
        {
            var r = await t.WaitAsync(TimeSpan.FromSeconds(30));
            r.Ok.Should().BeTrue(r.Reason);
        }

        var disk = WikiChatTriageStore.Get(_root);
        disk.State.Should().Be(WikiTriageFileState.Valid);
        disk.Entries.Should().HaveCount(420, "ни одна из двадцати параллельных постановок не затёрта другой");
        disk.Entries.Select(e => e.SessionId).Should().Contain(newIds);
        disk.Entries.Where(e => e.SessionId.StartsWith("seed")).Should().HaveCount(400);
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    // ─── C2R-Sh2: RejectStreak ─────────────────────────────────────────────

    [Fact]
    public void RejectStreak_СтарыйФайлБезПоля_ЧитаетсяБезСерии()
    {
        WriteRaw($$"""[{"sessionId":"old","cursor":3,"cursorHash":"{{Hash}}","zone":null,"reason":null,"nothingNewStreak":0,"triagedAt":"{{Date}}"}]""");
        var read = WikiChatTriageStore.Get(_root);
        read.State.Should().Be(WikiTriageFileState.Valid, "аддитивное поле: файл до Sh2 читается строго");
        var e = read.Entries.Single();
        e.RejectStreak.Should().Be(0);
        e.Cursor.Should().Be(3);
        e.CursorHash.Should().Be(Hash);
        e.NothingNewStreak.Should().Be(0);
    }

    [Fact]
    public void RejectStreak_СохраняетсяПриПересчётеИСтупениДва()
    {
        WikiChatTriageStore.Upsert(_root, Entry("chat-a", 3, Hash) with { RejectStreak = 2 }).Ok.Should().BeTrue();

        WikiChatTriageStore.UpdateScoring(_root, "chat-a", 5, null, "triage", "балл").Ok.Should().BeTrue();
        WikiChatTriageStore.Get(_root).Entries.Single().RejectStreak.Should().Be(2, "пересчёт серией не владеет");

        WikiChatTriageStore.UpdateStage2(_root, "chat-a", "gray", "серый", 4).Ok.Should().BeTrue();
        WikiChatTriageStore.Get(_root).Entries.Single().RejectStreak.Should().Be(2, "ступень 2 серией не владеет");
    }

    // Гонка: «разбор» (Update с приращением серии) и «пересчёт» одной сессии одновременно.
    // Первый вызов держит блокировку корня внутри build, пока второй приращение и пересчёт
    // не пытаются войти; ожидание — через события, без Task.Delay. Мутация «читать запись
    // вне блокировки» → второе приращение берёт устаревшее значение, серия 1 вместо 2.
    [Fact]
    public async Task TriageEntry_ConcurrentIngestAndRecalc_KeepsBothFields()
    {
        WikiChatTriageStore.Upsert(_root, Entry("chat-a", 1, Hash)).Ok.Should().BeTrue();
        using var firstInside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondInside = new ManualResetEventSlim();

        WikiChatTriageEntry Inc(WikiChatTriageEntry? cur) =>
            (cur ?? Entry("chat-a")) with { RejectStreak = (cur?.RejectStreak ?? 0) + 1 };

        var first = Task.Run(() => WikiChatTriageStore.Update(_root, "chat-a", cur =>
        {
            firstInside.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Inc(cur);
        }));
        firstInside.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var second = Task.Run(() => WikiChatTriageStore.Update(_root, "chat-a", cur =>
        {
            secondInside.Set();
            return Inc(cur);
        }));
        var recalc = Task.Run(() => WikiChatTriageStore.UpdateScoring(_root, "chat-a", 7, null, "triage", "балл"));

        // Под блокировкой второй build не стартует, пока первый держит корень: ждём ограниченно.
        secondInside.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("второй ждёт блокировку корня");
        release.Set();

        (await first).Ok.Should().BeTrue();
        (await second).Ok.Should().BeTrue();
        (await recalc).Ok.Should().BeTrue();

        var e = WikiChatTriageStore.Get(_root).Entries.Single();
        e.RejectStreak.Should().Be(2, "оба приращения учтены");
        e.Score.Should().Be(7, "поля пересчёта не потеряны");
        e.Stage1Zone.Should().Be("triage");
    }

    // ─── J-P1b: ResetCursors — сброс курсоров после чистки старой вики ────────

    [Fact]
    public void СбросКурсоров_КурсорыИИсходыОбнулены_БаллИСтупениСохранены()
    {
        // Три записи со всеми полями: поля разбора (курсор, хэш границы, исход, серии) +
        // score/signals, stage1*, вердикт ступени 2 и «mark» — пометка человека как чужое
        // поле записи (сама пометка живёт на чате, не в сторе: стор её не создаёт, но и не
        // теряет — общее правило чужих полей, как «future» в тесте 3).
        WriteRaw($$"""
            [{"sessionId":"chat-a","cursor":7,"cursorHash":"{{Hash}}","zone":"nothing_new","reason":"нет новых",
              "nothingNewStreak":3,"rejectStreak":2,"triagedAt":"{{Date}}","score":17,
              "signals":[{"name":"decision_words","weight":8},{"name":"nothing_new_penalty","weight":-10}],
              "stage1Zone":"triage","stage1Reason":"балл 17",
              "stage2Zone":"gray","stage2Reason":"серый чат","stage2HistoryCount":4,"mark":"important"},
             {"sessionId":"chat-b","cursor":7,"cursorHash":"{{Hash}}","zone":"triage","reason":"есть утверждения",
              "nothingNewStreak":1,"rejectStreak":1,"triagedAt":"{{Date}}","score":9,
              "signals":[{"name":"human_turns","weight":6}],"stage1Zone":"gray","stage1Reason":"балл 9",
              "mark":"skip"},
             {"sessionId":"chat-c","cursor":7,"cursorHash":null,"zone":null,"reason":null,
              "nothingNewStreak":0,"rejectStreak":0,"triagedAt":"{{Date}}"}]
            """);
        var before = WikiChatTriageStore.Get(_root);
        before.State.Should().Be(WikiTriageFileState.Valid);
        before.Entries.Should().HaveCount(3);

        var res = WikiChatTriageStore.ResetCursors(_root);
        res.Ok.Should().BeTrue(res.Reason);
        res.ResetCount.Should().Be(3, "сбросились все записи стора");

        var after = WikiChatTriageStore.Get(_root);
        after.State.Should().Be(WikiTriageFileState.Valid);
        after.Entries.Should().HaveCount(3, "сброс не удаляет записи: только их состояние разбора");
        foreach (var e in after.Entries)
        {
            e.Cursor.Should().Be(0, "разбор чатов начинается заново");
            e.CursorHash.Should().BeNull("нулевой курсор не имеет хэша границы (то же, что откат окна: back==0 → null)");
            e.Zone.Should().BeNull();
            e.Reason.Should().BeNull();
            e.NothingNewStreak.Should().Be(0);
            e.RejectStreak.Should().Be(0);
            e.TriagedAt.Should().BeOnOrAfter(DateTimeOffset.UtcNow.AddMinutes(-1), "запись изменена — дату ставит сервер");
        }

        // Всё, что не поля разбора, остаётся равным исходному прочтению.
        var a = after.Entries.Single(e => e.SessionId == "chat-a");
        var a0 = before.Entries.Single(e => e.SessionId == "chat-a");
        a.Score.Should().Be(17, "балл посчитан с причиной: после сброса его пересчитывать не нужно");
        a.Signals.Should().Equal(a0.Signals, "порядок и веса сигналов — часть причины");
        a.Stage1Zone.Should().Be("triage");
        a.Stage1Reason.Should().Be("балл 17");
        a.Stage2Zone.Should().Be("gray", "вердикт ступени 2 тоже не пересчитывается");
        a.Stage2Reason.Should().Be("серый чат");
        a.Stage2HistoryCount.Should().Be(4);

        var b = after.Entries.Single(e => e.SessionId == "chat-b");
        var b0 = before.Entries.Single(e => e.SessionId == "chat-b");
        b.Score.Should().Be(9);
        b.Signals.Should().Equal(b0.Signals);
        b.Stage1Zone.Should().Be("gray");
        b.Stage1Reason.Should().Be("балл 9");

        // Пометка человека — чужое поле записи: оно остаётся в файле как было.
        var raw = File.ReadAllText(TriageFile(), new UTF8Encoding(false));
        raw.Should().Contain("\"important\"");
        raw.Should().Contain("\"skip\"");
    }

    [Fact]
    public void СбросКурсоров_ПустогоСтораИлиФайлаНет_НольБезСозданияФайла_БитыйФайлОтказБезПерезаписи()
    {
        var none = WikiChatTriageStore.ResetCursors(_root);
        none.Ok.Should().BeTrue(none.Reason);
        none.ResetCount.Should().Be(0);
        File.Exists(TriageFile()).Should().BeFalse("сбрасывать нечего — пустой стор не создаётся");

        WriteRaw("[]");
        WikiChatTriageStore.ResetCursors(_root).ResetCount.Should().Be(0);

        // Битый файл — тот же fail-closed, что у писателей: Ok=false с кодом, байты файла
        // целые, исключение наружу не выходит.
        const string broken = "{ не json";
        WriteRaw(broken);
        var res = WikiChatTriageStore.ResetCursors(_root);
        res.Ok.Should().BeFalse();
        res.Code.Should().Be(WikiChatTriageStore.CodeInvalid);
        File.ReadAllText(TriageFile()).Should().Be(broken,
            "битый файл не перезаписывается молча: его записи были бы стёрты без разбора");
    }

    [Fact]
    public async Task СбросИАпдейтОднойЗаписи_ПодОднимЗамкомКорня_КаждаяИтерацияОднуЦелуюВерсию()
    {
        // 50 итераций параллельных ResetCursors и Update(cursor=5) одной записи. Под одним
        // замком корня владельца (тот же WithRootLock, что у Update/UpdateScoring/UpdateStage2)
        // операции сериализованы: итог — либо «сброс, потом апдейт», либо «апдейт, потом
        // сброс» — одна целая версия записи, не смесь полей двух версий; файл парсится.
        // На итерациях с удержанием (каждая десятая) Update держит замок внутри build
        // (событие), и ResetCursors обязан его ждать: без замка у ResetCursors его запись
        // из устаревшего прочтения легла бы поверх только что записанного курсора —
        // утверждение ожидания краснеет (мутация «снять замок у ResetCursors»).
        for (var iter = 0; iter < 50; iter++)
        {
            var hold = iter % 10 == 0;
            WikiChatTriageStore.Upsert(_root, Entry("chat-a", 7, Hash, "nothing_new", "старый исход", 3) with { RejectStreak = 2 })
                .Ok.Should().BeTrue($"посев итерации {iter}");

            using var inside = new ManualResetEventSlim();
            using var gate = new ManualResetEventSlim();
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var update = Task.Run(async () =>
            {
                if (!hold)
                    await go.Task.ConfigureAwait(false);
                return WikiChatTriageStore.Update(_root, "chat-a", cur =>
                {
                    if (hold)
                    {
                        inside.Set();
                        gate.Wait(TimeSpan.FromSeconds(10));
                    }
                    return (cur ?? Entry("chat-a")) with
                    {
                        Cursor = 5, CursorHash = Hash, Zone = "triage", Reason = "новый исход",
                        NothingNewStreak = 2, RejectStreak = 1,
                    };
                });
            });

            var reset = Task.Run(async () =>
            {
                if (!hold)
                    await go.Task.ConfigureAwait(false);
                return WikiChatTriageStore.ResetCursors(_root);
            });

            if (hold)
            {
                inside.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue($"вход в build, итерация {iter}");
                // Update держит замок корня (он внутри него, ждёт gate): сброс под тем же
                // замком не может завершиться, пока его держат.
                var winner = await Task.WhenAny(reset, Task.Delay(200));
                ReferenceEquals(winner, reset).Should().BeFalse(
                    $"итерация {iter}: ResetCursors ждёт замок корня, который держит Update");
                gate.Set();
            }
            else
            {
                go.SetResult();
            }

            var ur = await update;
            var rr = await reset;
            ur.Ok.Should().BeTrue($"итерация {iter}: {ur.Reason}");
            rr.Ok.Should().BeTrue($"итерация {iter}: {rr.Reason}");
            rr.ResetCount.Should().Be(1, $"итерация {iter}");

            var read = WikiChatTriageStore.Get(_root);
            read.State.Should().Be(WikiTriageFileState.Valid, $"итерация {iter}: файл всегда валидный JSON");
            var e = read.Entries.Single();
            new[] { 0, 5 }.Should().Contain(e.Cursor, $"итерация {iter}: курсор — либо сброленный (0), либо обновлённый (5)");
            if (e.Cursor == 0)
            {
                // Версия сброса — целая: ни одного поля апдейта в ней нет.
                e.CursorHash.Should().BeNull($"итерация {iter}");
                e.Zone.Should().BeNull($"итерация {iter}");
                e.Reason.Should().BeNull($"итерация {iter}");
                e.NothingNewStreak.Should().Be(0, $"итерация {iter}");
                e.RejectStreak.Should().Be(0, $"итерация {iter}");
            }
            else
            {
                // Версия апдейта — целая: ни одного поля сброса в ней нет.
                e.CursorHash.Should().Be(Hash, $"итерация {iter}");
                e.Zone.Should().Be("triage", $"итерация {iter}");
                e.Reason.Should().Be("новый исход", $"итерация {iter}");
                e.NothingNewStreak.Should().Be(2, $"итерация {iter}");
                e.RejectStreak.Should().Be(1, $"итерация {iter}");
            }
        }
    }

    private static WikiChatTriageEntry Entry(
        string id, int cursor = 0, string? cursorHash = null,
        string? zone = null, string? reason = null, int streak = 0,
        int? score = null, List<WikiTriageSignal>? signals = null) =>
        new(id, cursor, cursorHash, zone, reason, streak, default, score, signals);

    private string TriageFile() => Path.Combine(_root, "keeper", "chat-triage.json");

    // Файл пишется дословно: тест проверяет чтение того, что мог оставить бэкап или
    // ручная правка, а не то, что умеет писать стора. Каталог создаётся: тест пишет
    // в то же расположение, куда пишет и стора.
    private void WriteRaw(string json)
    {
        var full = TriageFile();
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
    }

    private IReadOnlyList<string> Order() =>
        WikiChatTriageStore.Get(_root).Entries.Select(e => e.SessionId).ToList();
}
