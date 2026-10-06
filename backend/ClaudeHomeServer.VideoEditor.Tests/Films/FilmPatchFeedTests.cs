using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Тексты правки фильма для человека и склейка частых правок в одну строку ленты
public sealed class FilmPatchFeedTests : IDisposable
{
    private readonly FilmWorld _w = new();
    private readonly ManualTime _time = new();
    private const string Path = "video/a/a.film";

    public void Dispose() => _w.Dispose();

    // Часы под ручным управлением: таймеры бросаются в бесконечность, сброс — FlushAllAsync
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private FilmPatchFeed NewFeed() =>
        new(_w.JobThreads, NullLogger<FilmPatchFeed>.Instance, _time, TimeSpan.FromHours(1), TimeSpan.FromSeconds(60));

    private FilmService ServiceWith(FilmPatchFeed feed) =>
        new(_w.Films, _w.Side, _w.JobThreads, _w.Registry, NullLogger<FilmService>.Instance, patchFeed: feed);

    private string Film(params string[] files)
    {
        foreach (var f in files) _w.WriteFile(f);
        return _w.WriteFilm(Path, Doc([.. files.Select(f => Item(f))]));
    }

    private async Task<string> Patch(FilmService svc, string revision, string initiator, params FilmPatchOp[] ops)
    {
        var result = await svc.PatchAsync(Owner, _w.Scope, Path, new FilmPatch(revision, ops), initiator, default, Session);
        result.IsOk.Should().BeTrue(result.Error);
        return result.Value!.Revision;
    }

    private List<string> Lines() => _w.Feed.Records.Select(r => r.Record.Fallback).ToList();

    [Fact]
    public async Task Тексты_правок_человека_по_человечески()
    {
        var revision = Film("video/a/1.mp4", "video/a/2.mp4");
        _w.WriteFile("video/a/3.mp4");
        _w.WriteFile("music/тема.mp3");
        // Сервис без копилки пишет сразу: тексты проверяем по одной правке
        var direct = _w.Service;

        revision = await Patch(direct, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Move, From: 0, To: 1));
        revision = await Patch(direct, revision, VideoInitiators.Human,
            new FilmPatchOp(FilmPatchOps.Cut, Index: 0, CutType: FilmCutTypes.Dissolve, Sec: 0.5));
        revision = await Patch(direct, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 2]));
        revision = await Patch(direct, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Add, "video/a/3.mp4", Trim: [0, 3]));
        revision = await Patch(direct, revision, VideoInitiators.Human,
            new FilmPatchOp(FilmPatchOps.Music, Music: new FilmMusic("music/тема.mp3", 50, 2)));

        Lines().Should().Equal(
            "Вы переставили сцены",
            "Вы поменяли склейку 1 → 2 на наплыв",
            "Вы подрезали сцену 1",
            "Вы добавили сцену 3",
            "Вы поставили музыку «тема»");
    }

    [Fact]
    public async Task У_агента_текст_без_Вы_и_без_слова_Claude()
    {
        var revision = Film("video/a/1.mp4", "video/a/2.mp4");

        await Patch(_w.Service, revision, VideoInitiators.Agent, new FilmPatchOp(FilmPatchOps.Move, From: 0, To: 1));

        Lines().Should().Equal("Переставил сцены");
    }

    [Fact]
    public async Task Частые_правки_одного_фильма_одним_инициатором_дают_одну_строку()
    {
        var revision = Film("video/a/1.mp4", "video/a/2.mp4");
        var feed = NewFeed();
        var svc = ServiceWith(feed);

        revision = await Patch(svc, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Move, From: 0, To: 1));
        _time.Now += TimeSpan.FromSeconds(10);
        revision = await Patch(svc, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 2]));
        _time.Now += TimeSpan.FromSeconds(10);
        await Patch(svc, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 3]));
        Lines().Should().BeEmpty("серия ещё идёт — строка пишется после неё");

        await feed.FlushAllAsync();

        Lines().Should().Equal("Вы поправили фильм: переставили сцены, подрезали сцену 1");
    }

    [Fact]
    public async Task Одна_правка_серии_пишется_коротко_а_правки_человека_и_агента_не_смешиваются()
    {
        var revision = Film("video/a/1.mp4", "video/a/2.mp4");
        var feed = NewFeed();
        var svc = ServiceWith(feed);

        revision = await Patch(svc, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Move, From: 0, To: 1));
        await Patch(svc, revision, VideoInitiators.Agent, new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 2]));
        await feed.FlushAllAsync();

        Lines().Should().BeEquivalentTo(["Вы переставили сцены", "Подрезал сцену 1"]);
    }

    [Fact]
    public async Task Правки_дальше_окна_от_первой_дают_отдельные_строки()
    {
        var revision = Film("video/a/1.mp4", "video/a/2.mp4");
        var feed = NewFeed();
        var svc = ServiceWith(feed);

        revision = await Patch(svc, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Move, From: 0, To: 1));
        _time.Now += TimeSpan.FromSeconds(61);
        await Patch(svc, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 2]));
        await feed.FlushAllAsync();

        Lines().Should().Equal("Вы переставили сцены", "Вы подрезали сцену 1");
    }

    [Fact]
    public async Task Одинаковые_правки_в_серии_не_повторяются_в_строке()
    {
        var revision = Film("video/a/1.mp4", "video/a/2.mp4");
        var feed = NewFeed();
        var svc = ServiceWith(feed);

        for (var i = 1; i <= 3; i++)
            revision = await Patch(svc, revision, VideoInitiators.Human, new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, i]));
        await feed.FlushAllAsync();

        Lines().Should().Equal("Вы подрезали сцену 1");
    }
}
