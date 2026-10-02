using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Moq;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Правка и состояние фильма (ADR-022 §2): патч под ревизией, границы записи (video/** и music/**, ссылки наружу —
// отказ), личная область без фильмов, признаки «обновлена / переснять / устарел» и счётчик трат
public sealed class FilmServiceTests : IDisposable
{
    private readonly FilmWorld _w = new();
    private static readonly VideoEditScope Personal = new(VideoEditScope.Personal, null);

    public void Dispose() => _w.Dispose();

    private Task<FilmCallResult<FilmStateDto>> Patch(string path, string revision, params FilmPatchOp[] ops) =>
        _w.Service.PatchAsync(Owner, _w.Scope, path, new FilmPatch(revision, ops), VideoInitiators.Human, default);

    private string FilmWith(params string[] files)
    {
        foreach (var f in files) _w.WriteFile(f);
        return _w.WriteFilm("video/a/a.film", Doc([.. files.Select(f => Item(f))]));
    }

    [Fact]
    public async Task Патч_с_верной_ревизией_пишет_файл_и_возвращает_состояние_с_новой_ревизией()
    {
        var revision = FilmWith("video/a/1.mp4");
        _w.WriteFile("video/a/2.mp4");

        var result = await Patch("video/a/a.film", revision,
            new FilmPatchOp(FilmPatchOps.Add, "video/a/2.mp4", Trim: [0, 4]));

        result.IsOk.Should().BeTrue(result.Error);
        result.Value!.Revision.Should().NotBe(revision);
        result.Value.Document.Items.Select(i => i.File).Should().Equal("video/a/1.mp4", "video/a/2.mp4");
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Чужая_правка_между_чтением_и_записью_даёт_revision_conflict_со_свежим_состоянием()
    {
        var revision = FilmWith("video/a/1.mp4");
        _w.WriteFile("video/a/2.mp4");
        // Кто-то правит файл в обход нас, пока человек держит старую ревизию
        File.WriteAllText(_w.Full("video/a/a.film"), FilmFormat.Serialize(Doc(Item("video/a/1.mp4"), Item("video/a/foreign.mp4"))));

        var result = await Patch("video/a/a.film", revision, new FilmPatchOp(FilmPatchOps.Add, "video/a/2.mp4", Trim: [0, 4]));

        result.ErrorCode.Should().Be(VideoEditorErrors.RevisionConflict);
        result.Conflict!.Document.Items.Select(i => i.File).Should().Equal("video/a/1.mp4", "video/a/foreign.mp4");
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Items.Should().HaveCount(2, "ничего не записано");
    }

    [Fact]
    public async Task Личная_область_отказывает_до_диска_для_всех_операций()
    {
        var state = _w.Service.State(Owner, Personal, "video/a/a.film");
        var list = _w.Service.List(Personal);
        var patch = await _w.Service.PatchAsync(Owner, Personal, "video/a/a.film",
            new FilmPatch("x", [new FilmPatchOp(FilmPatchOps.Remove, Index: 0)]), VideoInitiators.Human, default);

        state.ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        list.ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        patch.ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
    }

    [Theory]
    [InlineData("docs/a.film")]
    [InlineData("a.film")]
    [InlineData("video/../secret.film")]
    [InlineData("music/a.film")]
    [InlineData("/etc/video/a.film")]
    public void Путь_фильма_вне_video_отказывает(string path)
    {
        var result = _w.Service.State(Owner, _w.Scope, path);

        result.IsOk.Should().BeFalse();
        result.ErrorCode.Should().BeOneOf(VideoEditorErrors.OutsideAllowedFolders, VideoEditorErrors.InvalidRequest);
    }

    [Fact]
    public async Task Строка_и_музыка_вне_разрешённых_папок_отказывают()
    {
        var revision = FilmWith("video/a/1.mp4");
        _w.WriteFile("docs/x.mp4");
        _w.WriteFile("video/m.mp3");

        var clip = await Patch("video/a/a.film", revision, new FilmPatchOp(FilmPatchOps.Add, "docs/x.mp4", Trim: [0, 3]));
        var music = await Patch("video/a/a.film", revision,
            new FilmPatchOp(FilmPatchOps.Music, Music: new FilmMusic("docs/m.mp3", 50, 2)));

        clip.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        music.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
    }

    [Fact]
    public async Task Символическая_ссылка_наружу_отказывает_и_для_папки_и_для_файла()
    {
        if (OperatingSystem.IsWindows()) return;
        var revision = FilmWith("video/a/1.mp4");
        var outside = Path.Combine(_w.Dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "evil.mp4"), [1, 2]);
        Directory.CreateSymbolicLink(_w.Full("video/link"), outside);
        File.CreateSymbolicLink(_w.Full("video/a/file-link.mp4"), Path.Combine(outside, "evil.mp4"));

        var viaDir = await Patch("video/a/a.film", revision, new FilmPatchOp(FilmPatchOps.Add, "video/link/evil.mp4", Trim: [0, 3]));
        var viaFile = await Patch("video/a/a.film", revision, new FilmPatchOp(FilmPatchOps.Add, "video/a/file-link.mp4", Trim: [0, 3]));
        var filmViaLink = _w.Service.State(Owner, _w.Scope, "video/link/x.film");

        viaDir.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        viaFile.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        filmViaLink.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Add_без_обрезки_берёт_длину_по_пробе_клипа_или_снимку_сцены()
    {
        var dsp = new Mock<IVideoDsp>();
        dsp.SetupGet(d => d.Available).Returns(true);
        dsp.Setup(d => d.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoDspInfo(7.5, 640, 360, 24, true));
        using var w = new FilmWorld(dsp.Object);
        w.WriteFile("video/a/1.mp4");
        var revision = w.WriteFilm("video/a/a.film", Doc());

        var result = await w.Service.PatchAsync(Owner, w.Scope, "video/a/a.film",
            new FilmPatch(revision, [new FilmPatchOp(FilmPatchOps.Add, "video/a/1.mp4")]), VideoInitiators.Human, default);

        result.Value!.Document.Items.Single().Trim.Should().Equal(0, 7.5);

        // Без пробы — по снимку сцены; нет и его — отказ с объяснением
        w.WriteFile("video/a/2.mp4");
        var noDsp = await _w.Service.PatchAsync(Owner, _w.Scope, "video/a/a.film",
            new FilmPatch("x", [new FilmPatchOp(FilmPatchOps.Add, "video/a/2.mp4")]), VideoInitiators.Human, default);
        noDsp.IsOk.Should().BeFalse();
    }

    [Fact]
    public async Task Пометки_агента_ставятся_человеком_сбиваются_а_удалённая_строка_их_теряет()
    {
        var revision = FilmWith("video/a/1.mp4");
        _w.WriteFile("video/a/2.mp4");
        var added = await _w.Service.PatchAsync(Owner, _w.Scope, "video/a/a.film",
            new FilmPatch(revision, [new FilmPatchOp(FilmPatchOps.Add, "video/a/2.mp4", Trim: [0, 3])]), VideoInitiators.Agent, default);

        added.Value!.Marks.Select(m => m.Claude).Should().Equal(false, true);

        var trimmed = await Patch("video/a/a.film", added.Value.Revision, new FilmPatchOp(FilmPatchOps.Trim, Index: 1, Trim: [0, 2]));
        trimmed.Value!.Marks[1].Claude.Should().BeFalse("человек поправил строку — пометка снята");
    }

    [Fact]
    public async Task Обновлена_пока_нет_сборки_и_устарел_после_правки_а_сборка_гасит_оба_признака()
    {
        var revision = FilmWith("video/a/1.mp4");
        var state = _w.Service.State(Owner, _w.Scope, "video/a/a.film").Value!;
        state.Marks.Should().OnlyContain(m => m.Updated, "сборки ещё не было — всё новее неё");
        _w.Service.List(_w.Scope).Value!.Single().Stale.Should().BeTrue();

        // Сборка записана с хешем текущих входов, файл сцены старше сборки
        var hash = FilmStaleness.SourceHash(_w.ProjectRoot, state.Document);
        File.SetLastWriteTimeUtc(_w.Full("video/a/1.mp4"), DateTime.UtcNow.AddHours(-1));
        _w.Films.Update(_w.Full("video/a/a.film"), d => d with { Builds = [new FilmBuild("video/a/film.mp4", hash, DateTime.UtcNow)] });
        var built = _w.Service.State(Owner, _w.Scope, "video/a/a.film").Value!;
        built.Marks.Should().OnlyContain(m => !m.Updated);
        _w.Service.List(_w.Scope).Value!.Single().Stale.Should().BeFalse();

        // Правка меняет входы — «устарел» вычисляется, а не хранится
        var edited = await Patch("video/a/a.film", built.Revision, new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 2]));
        _w.Service.List(_w.Scope).Value!.Single().Stale.Should().BeTrue();
        edited.IsOk.Should().BeTrue();
    }

    [Fact]
    public void Переснять_ставится_у_строки_чья_сцена_изменила_входы_после_съёмки()
    {
        FilmWith("video/a/1.mp4");
        var (scene, version) = _w.SceneWithClip();
        _w.Threads.AddSavedFile(Owner, Session, scene.SceneId, new VideoSavedFileDto(version.VersionId, "video/a/1.mp4"));
        _w.Service.State(Owner, _w.Scope, "video/a/a.film").Value!.Marks[0].Stale.Should().BeFalse();

        _w.Threads.SetSettings(Owner, Session, scene.SceneId, scene.Settings with { Text = "другой текст" }, null);

        _w.Service.State(Owner, _w.Scope, "video/a/a.film").Value!.Marks[0].Stale.Should().BeTrue();
    }

    [Fact]
    public void Потрачено_на_фильм_считает_все_варианты_по_валютам_и_не_убывает()
    {
        FilmWith("video/a/1.mp4");
        _w.SceneWithClip("video/a", cost: new VideoCostDto("usd", 1.5));
        _w.SceneWithClip("video/a", provider: "higgsfield", cost: new VideoCostDto("credits", 12), name: "Сцена 2");
        _w.SceneWithClip("video/a", provider: "local", name: "Сцена 3");
        // Сцена из другой папки в счёт не идёт
        _w.SceneWithClip(folder: "video/other", cost: new VideoCostDto("usd", 99), name: "Чужая");

        var spent = _w.Service.State(Owner, _w.Scope, "video/a/a.film").Value!.Spent;

        spent.Usd.Should().BeApproximately(1.5, 1e-9);
        spent.Credits.Should().BeApproximately(12, 1e-9);
        spent.GpuSeconds.Should().BeGreaterThan(0, "локальный запуск считается секундами");

        // Нити чата ушли, а счёт остался в состоянии вне файла
        _w.Threads.Delete(Owner, Session);
        _w.Service.State(Owner, _w.Scope, "video/a/a.film").Value!.Spent.Usd.Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public void Список_фильмов_пропускает_ссылки_и_отмечает_битые()
    {
        FilmWith("video/a/1.mp4");
        Directory.CreateDirectory(_w.Full("video/broken"));
        File.WriteAllText(_w.Full("video/broken/broken.film"), "{ не json");
        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(_w.Dir, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "evil.film"), FilmFormat.Serialize(Doc()));
            Directory.CreateSymbolicLink(_w.Full("video/link"), outside);
        }

        var list = _w.Service.List(_w.Scope).Value!;

        list.Select(f => f.Path).Should().BeEquivalentTo("video/a/a.film", "video/broken/broken.film");
        list.Single(f => f.Path == "video/a/a.film").Should().Match<FilmSummaryDto>(f => f.Valid && f.ItemCount == 1 && f.Name == "a");
        list.Single(f => f.Path == "video/broken/broken.film").Valid.Should().BeFalse();
    }

    [Fact]
    public async Task Неизвестная_схема_читается_а_правка_отказывает_с_причиной()
    {
        Directory.CreateDirectory(_w.Full("video/a"));
        File.WriteAllText(_w.Full("video/a/a.film"), """{ "schema": 9, "aspect": "16:9", "items": [], "cuts": [], "builds": [] }""");

        var state = _w.Service.State(Owner, _w.Scope, "video/a/a.film");
        var patch = await _w.Service.PatchAsync(Owner, _w.Scope, "video/a/a.film",
            new FilmPatch(state.Value!.Revision, [new FilmPatchOp(FilmPatchOps.Music, Music: null)]), VideoInitiators.Human, default);

        state.IsOk.Should().BeTrue("для чтения файл отдаётся");
        state.Value.Document.Schema.Should().Be(9);
        patch.ErrorCode.Should().Be(VideoEditorErrors.FilmSchemaUnsupported);
    }
}
