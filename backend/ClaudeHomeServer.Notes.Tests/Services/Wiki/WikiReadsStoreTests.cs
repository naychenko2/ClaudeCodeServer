using System.Security.Cryptography;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// R1 (ADR-039, решение 7): keeper/reads.json — счётчик чтений страниц вики. Уровень стора:
// состояния файла («нет файла» / валиден / невалиден), раздельные счётчики путей и их время,
// Since одной первой записью, гонка одновременных записей. Состояние строится записью файла
// с явным текстом (как в образце), проверяется то, что вернут Read/RecordMany.
public class WikiReadsStoreTests : IDisposable
{
    private const string User = "u1";
    private const string PageA = "Общее/Тест.md";
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-01T03:00:00+00:00");

    private readonly string _dir;

    public WikiReadsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_reads_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── гонка: одновременные записи не теряют друг друга ───────────────────────

    [Fact]
    public async Task RecordMany_ParallelCalls_AllCounted()
    {
        var calls = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Recall)], T0)))
            .ToArray();
        var results = await Task.WhenAll(calls);

        results.All(ok => ok).Should().BeTrue("никакой вызов не отказал");

        var page = WikiReadsStore.Read(Root()).Pages[PageA];
        page.Recall.Should().Be(50, "read-modify-write целиком под семафором файла: без него параллельные записи теряют счётчики");
        page.LastRecall.Should().Be(T0);
    }

    // ─── состояний файла: «нет файла» — норма, не «пустой стор» и не ошибка ──────

    [Fact]
    public void НетФайла_ПустойВалидныйСнимок_БезНачалаУчёта()
    {
        var snap = WikiReadsStore.Read(Root());

        snap.Valid.Should().BeTrue("до первой записи это нормальное состояние хранилища");
        snap.Reason.Should().BeNull();
        snap.Since.Should().BeNull();
        snap.Pages.Should().BeEmpty();
    }

    [Fact]
    public void ПустойВход_ФайлНеСоздаётся_УчётНеВыглядитНачатым()
    {
        WikiReadsStore.RecordMany(Root(), [], T0).Should().BeTrue();

        File.Exists(ReadsPath()).Should().BeFalse("«ничего не прочли» не должно ставить начало учёта");
        WikiReadsStore.Read(Root()).Since.Should().BeNull();
    }

    [Theory]
    [InlineData("")] // пустой файл
    [InlineData("{}")] // ни версии, ни начала, ни словаря
    [InlineData("""{"v":2,"since":"2026-11-01T00:00:00+00:00","pages":{}}""")] // чужая версия формата
    [InlineData("""{"v":1,"pages":{}}""")] // нет начала учёта
    [InlineData("""{"v":1,"since":"2026-11-01T00:00:00+00:00"}""")] // нет словаря страниц
    [InlineData("""{"v":1,"since":"2026-11-01T00:00:00+00:00","pages":{"Общее/Тест.md":{"recall":1}}}""")] // счётчик без времени
    [InlineData("""{"v":1,"since":"2026-11-01T00:00:00+00:00","pages":{"Общее/Тест.md":{"recall":-1,"lastRead":"2026-11-01T00:00:00+00:00"}}}""")] // отрицательный счётчик
    [InlineData("""{"v":1,"since":"2026-11-01T00:00:00+00:00","pages":{"../Общее/Тест.md":{"recall":1,"lastRead":"2026-11-01T00:00:00+00:00","lastRecall":"2026-11-01T00:00:00+00:00"}}}""")] // ключ вне pages/
    [InlineData("""{"v":1,"since":"2026-11-01T00:00:00+00:00","pages":{"Общее/_index.md":{"recall":1,"lastRead":"2026-11-01T00:00:00+00:00","lastRecall":"2026-11-01T00:00:00+00:00"}}}""")] // служебная проекция
    public void КривыйФайл_НевалиденСПричиной_ИЗаписьВНегоНевозможна_ФайлНеТронут(string body)
    {
        WriteFile(body);
        var before = Sha256();

        var snap = WikiReadsStore.Read(Root());
        snap.Valid.Should().BeFalse($"«{body}»");
        snap.Reason.Should().NotBeNullOrEmpty("причина нужна вызывающему: он ставит сигнал, а не молчит");
        snap.Pages.Should().BeEmpty("половина прочитанного притворилась бы половиной учёта");

        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Recall)], T0)
            .Should().BeFalse("невалидный файл не перезаписывается молча: его счётники были бы стёрты");
        Sha256().Should().Be(before, "отказ не трогает файл");
    }

    // ─── Since: момент первой записи, дальше не двигается ────────────────────────

    [Fact]
    public void ПерваяЗапискаСтавитSince_ПоследующиеЕгоНеДвигают()
    {
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Read)], T0).Should().BeTrue();
        WikiReadsStore.Read(Root()).Since.Should().Be(T0, "начало учёта — момент первой записи файла");

        var later = T0.AddMinutes(5);
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Query)], later).Should().BeTrue();

        var snap = WikiReadsStore.Read(Root());
        snap.Since.Should().Be(T0, "дата из входа не переносит начало учёта: по нему R5 не считает страницы архивом");
        snap.Valid.Should().BeTrue();
    }

    // ─── счётчики по путям: раздельно, время — по своей группе ──────────────────

    [Fact]
    public void ВидыЧтенийСчитаютсяРаздельно_ВремяГруппируетсяПоСиле()
    {
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Recall)], T0);
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Human)], T0.AddHours(1));
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Query)], T0.AddHours(2));

        var p = WikiReadsStore.Read(Root()).Pages[PageA];
        p.Recall.Should().Be(1);
        p.Query.Should().Be(1);
        p.Read.Should().Be(0);
        p.Human.Should().Be(1);
        p.LastRead.Should().Be(T0.AddHours(2), "любое чтение");
        p.LastRecall.Should().Be(T0, "слабый путь — своё время");
        p.LastStrongRead.Should().Be(T0.AddHours(2), "сильные — Query, Read, Human");

        // Отставшая запись не отматывает страницу: «последнее чтение» остаётся последним.
        var stale = T0.AddMinutes(10);
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Recall)], stale).Should().BeTrue();

        var after = WikiReadsStore.Read(Root()).Pages[PageA];
        after.Recall.Should().Be(2);
        after.LastRead.Should().Be(T0.AddHours(2), "отставшая запись не должна открывать страницу из архива");
        after.LastRecall.Should().Be(stale);
        after.LastStrongRead.Should().Be(T0.AddHours(2));
    }

    [Fact]
    public void ОдинВызовСДубликатомСтраницыДаётПлюсПоКаждомуЭлементу()
    {
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Recall), (PageA, WikiReadKind.Recall), (PageA, WikiReadKind.Query)], T0)
            .Should().BeTrue();

        var p = WikiReadsStore.Read(Root()).Pages[PageA];
        p.Recall.Should().Be(2, "каждый элемент даёт +1");
        p.Query.Should().Be(1);
    }

    [Fact]
    public void НесколькоСтраницВОдномВызове_КаждаяСвоейЗаписью()
    {
        var other = "Личное/Другое.md";
        WikiReadsStore.RecordMany(Root(), [(PageA, WikiReadKind.Query), (other, WikiReadKind.Read)], T0).Should().BeTrue();

        var pages = WikiReadsStore.Read(Root()).Pages;
        pages.Should().HaveCount(2);
        pages[PageA].Query.Should().Be(1);
        pages[PageA].Read.Should().Be(0);
        pages[other].Read.Should().Be(1);
        pages[other].LastStrongRead.Should().Be(T0);
    }

    // ─── форма входа: ключ обязан быть путём под pages/ ──────────────────────────

    [Theory]
    [InlineData("")] // пустой ключ
    [InlineData("Общее/_index.md")] // служебная проекция
    [InlineData("../Общее/Тест.md")] // выход за pages/
    [InlineData("pages/Общее/Тест.md")] // путь с префиксом pages/, а не относительный
    [InlineData("Проекты/не тот id/Тест.md")] // id раздела с пробелом
    public void КлючНеПутьСтраницы_ЗаписьНевозможна_ФайлаНет(string key)
    {
        WikiReadsStore.RecordMany(Root(), [(key, WikiReadKind.Recall)], T0).Should().BeFalse();

        File.Exists(ReadsPath()).Should().BeFalse("кривый ключ не должен оставлять в сторе запись, которую никто не покажет");
    }

    // ─── порядок записей на диске — по ключу: diff между периодами читаем ────────

    [Fact]
    public void ЗаписиВФайлеУпорядоченыПоКлючу_НеПоПорядкуВызовов()
    {
        WikiReadsStore.RecordMany(Root(), [("Общее/Б.md", WikiReadKind.Recall), ("Личное/А.md", WikiReadKind.Recall)], T0)
            .Should().BeTrue();

        var json = File.ReadAllText(ReadsPath());
        json.IndexOf("Личное/А.md", StringComparison.Ordinal)
            .Should().BeLessThan(json.IndexOf("Общее/Б.md", StringComparison.Ordinal), "ключи по Ordinal");
        json.Should().Contain("Общее/Б.md", "пусть страниц с русскими именами папок остаются читаемыми, не \\u-escape");
    }

    // ─── построение состояния ───────────────────────────────────────────────────

    private string Root() => WikiPaths.Root(_dir, User);
    private string ReadsPath() => Path.Combine(Root(), "keeper/reads.json");

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReadsPath())!);
        File.WriteAllText(ReadsPath(), json, new System.Text.UTF8Encoding(false));
    }

    private string Sha256() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ReadsPath())));
}
