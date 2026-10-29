using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S6e-1 (ADR-035, решения 12а/12б): стор разбора чатов keeper/chat-triage.json — курсор
// «разобрано до записи N» + хэш записи N, зона и причина исхода, счётчик «нечего добавить»
// подряд. Запись расширяемая: чужие поля (балл S6f) при перезаписи сохраняются. Битый/пустой
// файл — «невалиден» с причиной, не исключение и не «пустой стор»; отсутствие файла — норма.
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
        // Балл S6f («score», «signals») и поле будущего шага («future»): стор S6e не знает их,
        // но правка курсора не должна стирать то, что написал другой шаг.
        WriteRaw("""
            [{"sessionId":"chat-a","cursor":4,"cursorHash":null,"zone":"nothing_new","reason":null,
              "nothingNewStreak":2,"triagedAt":"2026-10-20T12:00:00+00:00",
              "score":7,"signals":["решения","факты"],"future":{"k":1}}]
            """);

        var up = WikiChatTriageStore.Upsert(_root, Entry("chat-a", 9, Hash, "triage", "есть утверждения"));
        up.Ok.Should().BeTrue(up.Reason);

        var raw = File.ReadAllText(TriageFile(), new UTF8Encoding(false));
        raw.Should().Contain("\"score\":7");
        raw.Should().Contain("решения");
        raw.Should().Contain("\"future\"");

        var disk = WikiChatTriageStore.Get(_root);
        disk.State.Should().Be(WikiTriageFileState.Valid);
        disk.Entries.Should().ContainSingle(e =>
            e.SessionId == "chat-a" && e.Cursor == 9 && e.CursorHash == Hash && e.NothingNewStreak == 0,
            "известные поля обновлены, чужие — нет (их модель не читает и не трогает)");

        // Правка чужого чата тоже не задевает чужих полей первой записи.
        WikiChatTriageStore.Upsert(_root, Entry("chat-b", 1)).Ok.Should().BeTrue();
        File.ReadAllText(TriageFile()).Should().Contain("\"score\":7");
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

    private static WikiChatTriageEntry Entry(
        string id, int cursor = 0, string? cursorHash = null,
        string? zone = null, string? reason = null, int streak = 0) =>
        new(id, cursor, cursorHash, zone, reason, streak, default);

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
