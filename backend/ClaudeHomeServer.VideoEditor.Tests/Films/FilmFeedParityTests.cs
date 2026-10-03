using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Требование Андрея (2026-10-02): действие агента ложится в ленту ТЕМИ ЖЕ записями, что действие человека кнопкой;
// отличается только initiator. У сохранения сцены, правки и сборки фильма одна точка записи ленты — в сервисе,
// поэтому набор recordType при Human и Agent обязан совпадать, а тулсет блока 3 лишь зовёт эти методы
public sealed class FilmFeedParityTests
{
    private sealed class OkDsp : IVideoDsp
    {
        public bool Available => true;
        public Task<VideoDspInfo?> ProbeAsync(string path, CancellationToken ct) =>
            Task.FromResult<VideoDspInfo?>(new VideoDspInfo(10, 640, 360, 24, true));
        public Task<VideoDspResult> FilmstripAsync(string path, string outPath, int frames, int height, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);
        public Task<VideoDspResult> LastFrameAsync(string path, string outPath, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);
        public async Task<VideoDspResult> AssembleAsync(FilmPlan plan, string outPath, IProgress<VideoAssembleProgress>? progress, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(outPath, [1, 2, 3], ct);
            return VideoDspResult.Success;
        }
    }

    // Три действия подряд; возвращает записи ленты по действиям (recordType, initiator из data)
    private static async Task<Dictionary<string, List<(string RecordType, string? Initiator, string Text)>>> Run(string initiator)
    {
        using var w = new FilmWorld(new OkDsp());
        var result = new Dictionary<string, List<(string, string?, string)>>();
        List<(string, string?, string)> Take()
        {
            var taken = w.Feed.Records.Select(r => (r.Record.RecordType,
                r.Record.Data is { } d && d.TryGetProperty("initiator", out var i) ? i.GetString() : null, r.Record.Fallback)).ToList();
            w.Feed.Records.Clear();
            return taken;
        }

        // Сохранение сцены
        var (scene, version) = w.SceneWithClip();
        w.Feed.Records.Clear();
        var saver = new FilmSceneSaver(w.Service, w.Workspace, NullLogger<FilmSceneSaver>.Instance);
        (await saver.SaveAsync(Owner, w.Scope, new SaveSceneRequest(Session, scene.SceneId, version.VersionId, null, null), initiator, default))
            .IsOk.Should().BeTrue();
        result["save"] = Take();

        // Правка фильма
        w.WriteFile("video/утро/extra.mp4");
        var state = w.Service.State(Owner, w.Scope, "video/утро/утро.film").Value!;
        (await w.Service.PatchAsync(Owner, w.Scope, "video/утро/утро.film",
            new FilmPatch(state.Revision, [new FilmPatchOp(FilmPatchOps.Add, "video/утро/extra.mp4", Trim: [0, 3])]),
            initiator, default, Session)).IsOk.Should().BeTrue();
        result["patch"] = Take();

        // Сборка фильма
        var assembler = new FilmAssembler(w.Service, w.Registry, new ConfigurationBuilder().Build(),
            NullLogger<FilmAssembler>.Instance, new OkDsp());
        assembler.Start(Owner, w.Scope, "video/утро/утро.film", initiator, Session).IsOk.Should().BeTrue();
        await assembler.WhenIdleAsync();
        result["build"] = Take();
        return result;
    }

    [Fact]
    public async Task Набор_recordType_одинаков_у_человека_и_агента_а_отличается_только_initiator()
    {
        var human = await Run(VideoInitiators.Human);
        var agent = await Run(VideoInitiators.Agent);

        foreach (var action in new[] { "save", "patch", "build" })
        {
            human[action].Should().NotBeEmpty($"действие «{action}» пишет в ленту");
            agent[action].Select(r => r.RecordType).Should().Equal(human[action].Select(r => r.RecordType),
                $"«{action}»: агент пишет теми же recordType, что человек");
            human[action].Should().OnlyContain(r => r.Initiator == VideoInitiators.Human);
            agent[action].Should().OnlyContain(r => r.Initiator == VideoInitiators.Agent);
        }
        human["save"].Select(r => r.RecordType).Should().Contain(VideoThreadRecordTypes.Saved);
        human["patch"].Select(r => r.RecordType).Should().Contain(VideoThreadRecordTypes.Note);
        human["build"].Select(r => r.RecordType).Should().Contain(VideoThreadRecordTypes.FilmBuilt);
    }

    // Лицо у агентской строки даёт метка «✦ Claude» на фронте, поэтому слова «Claude» в тексте быть не должно
    [Fact]
    public async Task Тексты_строк_ленты_у_человека_с_лицом_у_агента_без_слова_Claude()
    {
        var human = await Run(VideoInitiators.Human);
        var agent = await Run(VideoInitiators.Agent);

        human["save"].Select(r => r.Text).Should().Contain(t => t.StartsWith("Вы сохранили сцену «"));
        human["patch"].Select(r => r.Text).Should().Contain(t => t.StartsWith("Вы добавили сцену "));
        human["build"].Select(r => r.Text).Should().Contain(t => t.StartsWith("Вы собрали фильм "));
        agent["save"].Select(r => r.Text).Should().Contain(t => t.StartsWith("Сохранил сцену «"));
        agent["patch"].Select(r => r.Text).Should().Contain(t => t.StartsWith("Добавил сцену "));
        agent["build"].Select(r => r.Text).Should().Contain(t => t.StartsWith("Собрал фильм "));
        agent.Values.SelectMany(v => v).Select(r => r.Text).Should().NotContain(t => t.Contains("Claude"));
    }

    // Человек из чата с sessionId получает те же записи, что агент: правка и сборка фильма не молчат
    [Fact]
    public async Task Человек_с_sessionId_получает_записи_ленты_правки_и_сборки()
    {
        var human = await Run(VideoInitiators.Human);
        human["patch"].Should().ContainSingle(r => r.RecordType == VideoThreadRecordTypes.Note && r.Initiator == VideoInitiators.Human);
        human["build"].Should().ContainSingle(r => r.RecordType == VideoThreadRecordTypes.FilmBuilt && r.Initiator == VideoInitiators.Human);
    }

    [Fact]
    public async Task Правка_и_сборка_без_чата_ленту_не_пишут_а_чужой_чат_игнорируется()
    {
        using var w = new FilmWorld(new OkDsp());
        w.WriteFile("video/a/1.mp4");
        w.WriteFile("video/a/2.mp4");
        var revision = w.WriteFilm("video/a/a.film", Doc(Item("video/a/1.mp4")));

        await w.Service.PatchAsync(Owner, w.Scope, "video/a/a.film",
            new FilmPatch(revision, [new FilmPatchOp(FilmPatchOps.Add, "video/a/2.mp4", Trim: [0, 3])]),
            VideoInitiators.Agent, default, "чужой-чат");

        w.Feed.Records.Should().BeEmpty();
    }
}
