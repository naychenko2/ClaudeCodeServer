using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Волна правок после приёмки: признак «устарел» у фильма целиком, пустой новый фильм, заготовка музыки
public sealed class FilmWaveContractTests : IDisposable
{
    private readonly FilmWorld _w = new();
    private const string Path = "video/a/a.film";

    public void Dispose() => _w.Dispose();

    // Собранный фильм: в builds последняя сборка с хешем ТЕКУЩИХ входов
    private string BuiltFilm(params string[] files)
    {
        foreach (var f in files) _w.WriteFile(f);
        var doc = Doc([.. files.Select(f => Item(f))]);
        var hash = FilmStaleness.SourceHash(_w.ProjectRoot, doc);
        return _w.WriteFilm(Path, doc with { Builds = [new FilmBuild("video/a/film.mp4", hash, DateTime.UtcNow)] });
    }

    private async Task<FilmStateDto> Patch(string revision, params FilmPatchOp[] ops)
    {
        var result = await _w.Service.PatchAsync(Owner, _w.Scope, Path, new FilmPatch(revision, ops), VideoInitiators.Human, default);
        result.IsOk.Should().BeTrue(result.Error);
        return result.Value!;
    }

    [Fact]
    public void Собранный_фильм_без_правок_не_устарел()
    {
        BuiltFilm("video/a/1.mp4", "video/a/2.mp4");
        _w.Service.State(Owner, _w.Scope, Path).Value!.Stale.Should().BeFalse();
    }

    public static TheoryData<string> Операции() => new()
    {
        "add", "remove", "move", "cut", "trim", "music",
    };

    [Theory]
    [MemberData(nameof(Операции))]
    public async Task Каждая_операция_патча_после_сборки_поднимает_признак(string op)
    {
        var revision = BuiltFilm("video/a/1.mp4", "video/a/2.mp4");
        _w.WriteFile("video/a/3.mp4");
        _w.WriteFile("music/a.mp3");
        FilmPatchOp patch = op switch
        {
            "add" => new(FilmPatchOps.Add, "video/a/3.mp4", Trim: [0, 3]),
            "remove" => new(FilmPatchOps.Remove, Index: 0),
            "move" => new(FilmPatchOps.Move, From: 0, To: 1),
            "cut" => new(FilmPatchOps.Cut, Index: 0, CutType: FilmCutTypes.Dissolve, Sec: 0.5),
            "trim" => new(FilmPatchOps.Trim, Index: 0, Trim: [1, 4]),
            _ => new(FilmPatchOps.Music, Music: new FilmMusic("music/a.mp3", 50, 2)),
        };

        var state = await Patch(revision, patch);

        state.Stale.Should().BeTrue($"после «{op}» фильм изменён после сборки");
    }

    [Fact]
    public async Task Возврат_к_собранному_состоянию_снимает_признак()
    {
        var revision = BuiltFilm("video/a/1.mp4", "video/a/2.mp4");
        var moved = await Patch(revision, new FilmPatchOp(FilmPatchOps.Move, From: 0, To: 1));
        moved.Stale.Should().BeTrue();

        var back = await Patch(moved.Revision, new FilmPatchOp(FilmPatchOps.Move, From: 1, To: 0));

        back.Stale.Should().BeFalse("хеш входов снова совпал с хешем сборки");
    }

    [Fact]
    public async Task Новый_фильм_создаётся_пустым_и_валидным()
    {
        var created = await _w.Service.CreateAsync(Owner, _w.Scope, new FilmCreateRequest("video/новый/новый.film"), default);

        created.IsOk.Should().BeTrue(created.Error);
        created.Value!.Document.Items.Should().BeEmpty();
        created.Value.Document.Cuts.Should().BeEmpty();
        created.Value.Document.Aspect.Should().Be("16:9");
        created.Value.Stale.Should().BeFalse("пустой фильм собирать нечего");
        var read = _w.Films.ReadFile(_w.Full("video/новый/новый.film"));
        read.Status.Should().Be(FilmStore.ReadStatus.Ok);
        read.Revision.Should().Be(created.Value.Revision);
    }

    [Fact]
    public async Task Занятое_имя_и_плохой_путь_отказывают_ничего_не_затирая()
    {
        var revision = BuiltFilm("video/a/1.mp4");

        var taken = await _w.Service.CreateAsync(Owner, _w.Scope, new FilmCreateRequest(Path), default);
        var outside = await _w.Service.CreateAsync(Owner, _w.Scope, new FilmCreateRequest("docs/x/x.film"), default);
        var aspect = await _w.Service.CreateAsync(Owner, _w.Scope, new FilmCreateRequest("video/b/b.film", "7:3"), default);

        taken.ErrorCode.Should().Be(VideoEditorErrors.NameTaken);
        outside.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        aspect.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        _w.Films.ReadFile(_w.Full(Path)).Revision.Should().Be(revision);
    }

    [Fact]
    public void Заготовка_музыки_короткого_фильма_честно_говорит_про_минимум_и_берёт_стиль_из_сцен()
    {
        var doc = Doc(
            new FilmItem("video/a/1.mp4", [0, 2], new FilmSceneSnapshot("Камера у окна", null, null, "fal", "veo", 2)),
            new FilmItem("video/a/2.mp4", [0, 3], new FilmSceneSnapshot("Крупный план рук", null, null, "fal", "veo", 3)));

        var draft = FilmMusicComposer.DraftOf("t-1", doc);

        draft.DurationSec.Should().Be(5);
        draft.MinDurationSec.Should().Be(10);
        draft.ActualDurationSec.Should().Be(10);
        draft.DurationNote.Should().Contain("не короче 10 с");
        draft.StyleText.Should().Be("Камера у окна; Крупный план рук");
    }

    [Fact]
    public void Заготовка_музыки_длинного_фильма_без_пометки()
    {
        var doc = Doc(Item("video/a/1.mp4", 0, 12));

        var draft = FilmMusicComposer.DraftOf("t-1", doc);

        draft.ActualDurationSec.Should().Be(12);
        draft.DurationNote.Should().BeNull();
    }
}
