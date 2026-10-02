using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Формат .film и запись под ревизией (ADR-022 §2): валидация, неизвестная схема только для чтения,
// чужая правка между чтением и записью — Conflict, атомарность без следов temp-файла
public sealed class FilmFormatAndStoreTests : IDisposable
{
    private readonly FilmWorld _w = new();

    public void Dispose() => _w.Dispose();

    private static string Json(int schema = 1, string cuts = "[]", string items = "[]", string aspect = "16:9") =>
        $$"""{ "schema": {{schema}}, "aspect": "{{aspect}}", "items": {{items}}, "cuts": {{cuts}}, "builds": [] }""";

    private const string TwoItems =
        """[{ "file": "video/a/s1.mp4", "trim": [0, 5] }, { "file": "video/a/s2.mp4", "trim": [1, 4] }]""";

    [Fact]
    public void Длина_cuts_должна_быть_на_единицу_меньше_items()
    {
        FilmFormat.Parse(Json(items: TwoItems, cuts: """[{ "type": "butt", "sec": 0 }]""")).Status.Should().Be(FilmFormat.Status.Ok);

        var wrong = FilmFormat.Parse(Json(items: TwoItems, cuts: "[]"));

        wrong.Status.Should().Be(FilmFormat.Status.Invalid);
        wrong.Error.Should().Contain("Склеек");
    }

    [Fact]
    public void Неизвестная_схема_читается_но_только_с_причиной()
    {
        var parsed = FilmFormat.Parse(Json(schema: 7, items: TwoItems, cuts: """[{ "type": "butt", "sec": 0 }]"""));

        parsed.Status.Should().Be(FilmFormat.Status.Unsupported);
        parsed.Document!.Items.Should().HaveCount(2, "для чтения документ отдаётся как есть");
        parsed.Error.Should().Contain("только для чтения");
    }

    [Theory]
    [InlineData("""[{ "file": "video/a/s1.mp4", "trim": [3, 3] }]""")]
    [InlineData("""[{ "file": "video/a/s1.mp4", "trim": [0] }]""")]
    [InlineData("""[{ "file": "docs/s1.mp4", "trim": [0, 2] }]""")]
    [InlineData("""[{ "file": "video/../etc/s1.mp4", "trim": [0, 2] }]""")]
    [InlineData("""[{ "file": "", "trim": [0, 2] }]""")]
    public void Строка_с_неверной_обрезкой_или_файлом_вне_video_невалидна(string items)
    {
        FilmFormat.Parse(Json(items: items)).Status.Should().Be(FilmFormat.Status.Invalid);
    }

    [Fact]
    public void Неизвестное_соотношение_сторон_и_битый_JSON_невалидны()
    {
        FilmFormat.Parse(Json(aspect: "21:9")).Status.Should().Be(FilmFormat.Status.Invalid);
        FilmFormat.Parse("{ не json").Status.Should().Be(FilmFormat.Status.Invalid);
        FilmFormat.Parse("[]").Status.Should().Be(FilmFormat.Status.Invalid);
    }

    [Fact]
    public void Длительность_вычитает_наплывы_и_не_трогает_затемнение()
    {
        var doc = new FilmDocument(1, "16:9",
            [FilmWorld.Item("video/a/1.mp4", 0, 5), FilmWorld.Item("video/a/2.mp4", 1, 4), FilmWorld.Item("video/a/3.mp4", 0, 2)],
            [new FilmCut(FilmCutTypes.Dissolve, 0.5), new FilmCut(FilmCutTypes.Fade, 1)], null, []);

        FilmFormat.DurationOf(doc).Should().BeApproximately(5 + 3 - 0.5 + 2, 1e-9);
    }

    // ── Запись под ревизией ──────────────────────────────────────────────────────

    [Fact]
    public void Новый_фильм_пишется_CreateNew_и_не_затирает_существующий()
    {
        var path = _w.Full("video/a/a.film");

        FilmStore.Write first = _w.Films.WriteFile(path, FilmWorld.Doc(), null, create: true);
        var second = _w.Films.WriteFile(path, FilmWorld.Doc(FilmWorld.Item("video/a/s1.mp4")), null, create: true);

        first.Status.Should().Be(FilmStore.WriteStatus.Ok);
        second.Status.Should().Be(FilmStore.WriteStatus.NameTaken);
        _w.Films.ReadFile(path).Document!.Items.Should().BeEmpty("второй CreateNew ничего не записал");
    }

    [Fact]
    public void Запись_с_устаревшей_ревизией_отказывает_конфликтом_и_не_пишет()
    {
        var path = _w.Full("video/a/a.film");
        var revision = _w.WriteFilm("video/a/a.film", FilmWorld.Doc());
        // Чужая правка между чтением и записью: файл меняется в обход нас
        var foreign = FilmWorld.Doc(FilmWorld.Item("video/a/foreign.mp4"));
        File.WriteAllText(path, FilmFormat.Serialize(foreign));

        var written = _w.Films.WriteFile(path, FilmWorld.Doc(FilmWorld.Item("video/a/mine.mp4")), revision, create: false);

        written.Status.Should().Be(FilmStore.WriteStatus.Conflict);
        written.Current.Document!.Items.Single().File.Should().Be("video/a/foreign.mp4", "в Conflict приходит чужая правка");
        _w.Films.ReadFile(path).Document!.Items.Single().File.Should().Be("video/a/foreign.mp4");
    }

    [Fact]
    public void Запись_с_верной_ревизией_проходит_без_следов_temp_и_меняет_ревизию()
    {
        var path = _w.Full("video/a/a.film");
        var revision = _w.WriteFilm("video/a/a.film", FilmWorld.Doc());

        var written = _w.Films.WriteFile(path, FilmWorld.Doc(FilmWorld.Item("video/a/s1.mp4")), revision, create: false);

        written.Status.Should().Be(FilmStore.WriteStatus.Ok);
        written.Current.Revision.Should().NotBe(revision);
        _w.Films.ReadFile(path).Revision.Should().Be(written.Current.Revision);
        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().ContainSingle("temp-файла после записи нет");
    }

    [Fact]
    public void Запись_без_ревизии_в_существующий_файл_конфликт_а_в_отсутствующий_NotFound()
    {
        _w.WriteFilm("video/a/a.film", FilmWorld.Doc());

        _w.Films.WriteFile(_w.Full("video/a/a.film"), FilmWorld.Doc(), null, create: false).Status
            .Should().Be(FilmStore.WriteStatus.Conflict);
        _w.Films.WriteFile(_w.Full("video/a/none.film"), FilmWorld.Doc(), "abc", create: false).Status
            .Should().Be(FilmStore.WriteStatus.NotFound);
    }

    [Fact]
    public void Файл_неизвестной_схемы_править_нельзя()
    {
        var path = _w.Full("video/a/a.film");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Json(schema: 9));
        var read = _w.Films.ReadFile(path);

        var written = _w.Films.WriteFile(path, FilmWorld.Doc(), read.Revision, create: false);

        read.Status.Should().Be(FilmStore.ReadStatus.Unsupported);
        written.Status.Should().Be(FilmStore.WriteStatus.Invalid);
        File.ReadAllText(path).Should().Contain("\"schema\": 9");
    }

    [Fact]
    public void Update_пишет_поверх_свежего_содержимого_а_не_устаревшего()
    {
        var path = _w.Full("video/a/a.film");
        _w.WriteFilm("video/a/a.film", FilmWorld.Doc());
        // Человек успел добавить строку; сервер дописывает сборку поверх, не затирая её
        File.WriteAllText(path, FilmFormat.Serialize(FilmWorld.Doc(FilmWorld.Item("video/a/s1.mp4"))));

        var written = _w.Films.Update(path, d => d with { Builds = [new FilmBuild("video/a/film.mp4", "h", DateTime.UtcNow)] });

        written.Status.Should().Be(FilmStore.WriteStatus.Ok);
        var doc = _w.Films.ReadFile(path).Document!;
        doc.Items.Should().ContainSingle("правка человека не потеряна");
        doc.Builds.Should().ContainSingle();
    }
}
