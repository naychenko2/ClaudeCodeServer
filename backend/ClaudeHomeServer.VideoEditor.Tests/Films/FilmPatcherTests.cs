using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Операции патча (add, remove, move, cut, trim, music): склейки живут между строками и едут вместе с ними;
// первая неверная операция отказывает, документ не меняется
public sealed class FilmPatcherTests
{
    private static FilmDocument Three() => new(1, "16:9",
        [FilmWorld.Item("video/a/1.mp4"), FilmWorld.Item("video/a/2.mp4"), FilmWorld.Item("video/a/3.mp4")],
        [new FilmCut(FilmCutTypes.Dissolve, 0.5), new FilmCut(FilmCutTypes.Fade, 1)], null, []);

    private static FilmPatchOp Add(string file, int? index = null) =>
        new(FilmPatchOps.Add, file, index, Trim: [0, 3], Scene: null);

    private static FilmDocument Ok(FilmDocument doc, params FilmPatchOp[] ops)
    {
        var result = FilmPatcher.Apply(doc, ops);
        result.Error.Should().BeNull();
        FilmFormat.Validate(result.Document!).Should().BeNull("после любой операции документ валиден: cuts = items − 1");
        return result.Document!;
    }

    [Fact]
    public void Add_в_конец_дописывает_строку_и_стык_встык()
    {
        var doc = Ok(Three(), Add("video/a/4.mp4"));

        doc.Items.Select(i => i.File).Should().Equal("video/a/1.mp4", "video/a/2.mp4", "video/a/3.mp4", "video/a/4.mp4");
        doc.Cuts.Should().HaveCount(3);
        doc.Cuts[2].Type.Should().Be(FilmCutTypes.Butt);
        doc.Cuts[0].Type.Should().Be(FilmCutTypes.Dissolve, "прежние стыки на местах");
    }

    [Fact]
    public void Add_в_начало_и_в_середину_двигает_стыки_вместе_со_строками()
    {
        var first = Ok(Three(), Add("video/a/0.mp4", 0));
        first.Items[0].File.Should().Be("video/a/0.mp4");
        first.Cuts.Select(c => c.Type).Should().Equal(FilmCutTypes.Butt, FilmCutTypes.Dissolve, FilmCutTypes.Fade);

        var middle = Ok(Three(), Add("video/a/x.mp4", 1));
        middle.Items.Select(i => i.File).Should().Equal("video/a/1.mp4", "video/a/x.mp4", "video/a/2.mp4", "video/a/3.mp4");
        middle.Cuts.Should().HaveCount(3);
    }

    [Fact]
    public void Add_в_пустой_фильм_стыков_не_заводит()
    {
        var doc = Ok(FilmWorld.Doc(), Add("video/a/1.mp4"));

        doc.Items.Should().ContainSingle();
        doc.Cuts.Should().BeEmpty();
    }

    [Fact]
    public void Add_отказывает_на_дубле_чужой_папке_и_лишней_сцене()
    {
        FilmPatcher.Apply(Three(), [Add("video/a/1.mp4")]).Error.Should().Contain("уже есть");
        FilmPatcher.Apply(Three(), [Add("docs/x.mp4")]).Error.Should().Contain("video/");
        FilmPatcher.Apply(Three(), [new FilmPatchOp(FilmPatchOps.Add, "video/a/9.mp4", Trim: [0, 3], Index: 9)]).Error
            .Should().Contain("места");

        var full = FilmWorld.Doc([.. Enumerable.Range(0, FilmFormat.MaxItems).Select(i => FilmWorld.Item($"video/a/{i}.mp4"))]);
        FilmPatcher.Apply(full, [Add("video/a/extra.mp4")]).Error.Should().Contain("не больше 50");
    }

    [Fact]
    public void Remove_убирает_строку_и_стык_рядом()
    {
        var middle = Ok(Three(), new FilmPatchOp(FilmPatchOps.Remove, Index: 1));
        middle.Items.Select(i => i.File).Should().Equal("video/a/1.mp4", "video/a/3.mp4");
        middle.Cuts.Should().ContainSingle();

        var last = Ok(Three(), new FilmPatchOp(FilmPatchOps.Remove, Index: 2));
        last.Cuts.Should().ContainSingle().Which.Type.Should().Be(FilmCutTypes.Dissolve, "стык между оставшимися сохранён");

        var only = Ok(FilmWorld.Doc(FilmWorld.Item("video/a/1.mp4")), new FilmPatchOp(FilmPatchOps.Remove, Index: 0));
        only.Items.Should().BeEmpty();
        only.Cuts.Should().BeEmpty();
    }

    [Fact]
    public void Move_переставляет_строки_а_стыки_остаются_на_местах()
    {
        var doc = Ok(Three(), new FilmPatchOp(FilmPatchOps.Move, From: 0, To: 2));

        doc.Items.Select(i => i.File).Should().Equal("video/a/2.mp4", "video/a/3.mp4", "video/a/1.mp4");
        doc.Cuts.Select(c => c.Type).Should().Equal(FilmCutTypes.Dissolve, FilmCutTypes.Fade);
    }

    [Fact]
    public void Cut_меняет_тип_и_длину_стыка_а_встык_обнуляет_длину()
    {
        var doc = Ok(Three(),
            new FilmPatchOp(FilmPatchOps.Cut, Index: 0, CutType: FilmCutTypes.Fade, Sec: 2),
            new FilmPatchOp(FilmPatchOps.Cut, Index: 1, CutType: FilmCutTypes.Butt, Sec: 3));

        doc.Cuts[0].Should().Be(new FilmCut(FilmCutTypes.Fade, 2));
        doc.Cuts[1].Should().Be(new FilmCut(FilmCutTypes.Butt, 0));
        FilmPatcher.Apply(Three(), [new FilmPatchOp(FilmPatchOps.Cut, Index: 2, CutType: FilmCutTypes.Butt)]).Error.Should().Contain("стыка нет");
        FilmPatcher.Apply(Three(), [new FilmPatchOp(FilmPatchOps.Cut, Index: 0, CutType: "wipe", Sec: 1)]).Error.Should().Contain("Неизвестная склейка");
    }

    [Fact]
    public void Trim_и_Music_ставятся_и_проверяются()
    {
        var doc = Ok(Three(),
            new FilmPatchOp(FilmPatchOps.Trim, Index: 1, Trim: [0.5, 4]),
            new FilmPatchOp(FilmPatchOps.Music, Music: new FilmMusic("music/m.mp3", 50, 2)));
        doc.Items[1].Trim.Should().Equal(0.5, 4);
        doc.Music.Should().Be(new FilmMusic("music/m.mp3", 50, 2));
        Ok(doc, new FilmPatchOp(FilmPatchOps.Music, Music: null)).Music.Should().BeNull("null убирает музыку");

        FilmPatcher.Apply(Three(), [new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [4, 2])]).Error.Should().Contain("конец");
        FilmPatcher.Apply(Three(), [new FilmPatchOp(FilmPatchOps.Music, Music: new FilmMusic("video/m.mp3", 50, 2))]).Error
            .Should().Contain("music/");
        FilmPatcher.Apply(Three(), [new FilmPatchOp(FilmPatchOps.Music, Music: new FilmMusic("music/m.mp3", 150, 2))]).Error
            .Should().Contain("Громкость");
    }

    [Fact]
    public void Неверная_операция_среди_верных_отказывает_целиком_без_изменений()
    {
        var original = Three();

        var result = FilmPatcher.Apply(original, [Add("video/a/4.mp4"), new FilmPatchOp("explode"), Add("video/a/5.mp4")]);

        result.Document.Should().BeNull();
        result.Error.Should().Contain("Неизвестная операция");
        original.Items.Should().HaveCount(3);
    }

    [Fact]
    public void Затронутые_строки_считаются_по_итоговым_индексам()
    {
        var result = FilmPatcher.Apply(Three(), [Add("video/a/0.mp4", 0), new FilmPatchOp(FilmPatchOps.Trim, Index: 3, Trim: [0, 2])]);

        result.TouchedIndexes.Should().Equal(0, 3);
    }
}
