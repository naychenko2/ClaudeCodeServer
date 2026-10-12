using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Ранжирование фото под сюжет (ADR-027, §7): очки, правило первого места, фолбэк на нынешний порядок
public class CharacterPhotoPickerTests
{
    private static CharacterPhoto P(string file, bool primary = false, string? angle = null, string? pitch = null, string? emotion = null) =>
        new(file, primary, angle, pitch, emotion);

    private static string[] Files(CharacterPhotoPick pick) => pick.Photos.Select(p => p.File).ToArray();

    private static readonly CharacterPhoto[] Set =
    [
        P("a.jpg", angle: "front", emotion: "neutral"),
        P("main.jpg", primary: true, angle: "front", emotion: "neutral"),
        P("b.jpg", angle: "profile", emotion: "smile"),
        P("c.jpg", angle: "three-quarter", emotion: "laugh"),
    ];

    [Fact]
    public void Фолбэк_пустой_сюжет_даёт_старый_порядок()
    {
        var pick = CharacterPhotoPicker.Pick(Set, new CharacterScene(null, null, null), 3);
        pick.Pick.Should().Be("fallback");
        Files(pick).Should().Equal("main.jpg", "a.jpg", "b.jpg");
    }

    [Fact]
    public void Фолбэк_сюжет_null_и_unknown_даёт_старый_порядок()
    {
        Files(CharacterPhotoPicker.Pick(Set, null, 3)).Should().Equal("main.jpg", "a.jpg", "b.jpg");
        var pick = CharacterPhotoPicker.Pick(Set, new CharacterScene("unknown", "unknown", "unknown"), 3);
        pick.Pick.Should().Be("fallback");
    }

    [Fact]
    public void Фолбэк_без_меток_даёт_старый_порядок()
    {
        var bare = new[] { P("a.jpg"), P("b.jpg"), P("m.jpg", primary: true), P("c.jpg") };
        var pick = CharacterPhotoPicker.Pick(bare, new CharacterScene("profile", null, "smile"), 2);
        pick.Pick.Should().Be("fallback");
        Files(pick).Should().Equal("m.jpg", "a.jpg");
    }

    [Fact]
    public void Фолбэк_все_фото_с_нулём_очков()
    {
        var photos = new[] { P("a.jpg", angle: "front"), P("m.jpg", primary: true, angle: "front") };
        var pick = CharacterPhotoPicker.Pick(photos, new CharacterScene("profile", null, null), 3);
        pick.Pick.Should().Be("fallback");
        Files(pick).Should().Equal("m.jpg", "a.jpg");
    }

    [Fact]
    public void Фолбэк_без_главного_сохраняет_порядок_манифеста()
    {
        var photos = new[] { P("a.jpg"), P("b.jpg"), P("c.jpg") };
        Files(CharacterPhotoPicker.Pick(photos, null, 2)).Should().Equal("a.jpg", "b.jpg");
    }

    [Fact]
    public void В_профиль_фото_с_профилем_попадает_в_набор()
    {
        var pick = CharacterPhotoPicker.Pick(Set, new CharacterScene("profile", null, null), 2);
        pick.Pick.Should().Be("auto");
        // Точное совпадение ракурса → профильное фото первым, главное вторым
        Files(pick).Should().Equal("b.jpg", "main.jpg");
    }

    [Fact]
    public void Соседний_ракурс_даёт_1_точный_2_анфас_и_профиль_не_соседи()
    {
        var scene = new CharacterScene("three-quarter", null, null);
        CharacterPhotoPicker.Score(P("x", angle: "three-quarter"), scene).Should().Be(2);
        CharacterPhotoPicker.Score(P("x", angle: "front"), scene).Should().Be(1);
        CharacterPhotoPicker.Score(P("x", angle: "profile"), scene).Should().Be(1);
        CharacterPhotoPicker.Score(P("x", angle: "profile"), new CharacterScene("front", null, null)).Should().Be(0);
        CharacterPhotoPicker.Score(P("x", angle: "front"), new CharacterScene("profile", null, null)).Should().Be(0);
    }

    [Fact]
    public void Наклон_даёт_1_за_совпадение()
    {
        var scene = new CharacterScene(null, "up", null);
        CharacterPhotoPicker.Score(P("x", pitch: "up"), scene).Should().Be(1);
        CharacterPhotoPicker.Score(P("x", pitch: "down"), scene).Should().Be(0);
    }

    [Fact]
    public void Эмоция_точная_2_родственная_1_прочие_0()
    {
        var scene = new CharacterScene(null, null, "smile");
        CharacterPhotoPicker.Score(P("x", emotion: "smile"), scene).Should().Be(2);
        CharacterPhotoPicker.Score(P("x", emotion: "laugh"), scene).Should().Be(1);
        CharacterPhotoPicker.Score(P("x", emotion: "sad"), scene).Should().Be(0);
        CharacterPhotoPicker.Score(P("x", emotion: "smile"), new CharacterScene(null, null, "laugh")).Should().Be(1);
    }

    [Fact]
    public void Unknown_и_null_в_метке_фото_дают_ноль()
    {
        var scene = new CharacterScene("front", "level", "smile");
        CharacterPhotoPicker.Score(P("x", angle: "unknown", pitch: "unknown", emotion: "unknown"), scene).Should().Be(0);
        CharacterPhotoPicker.Score(P("x"), scene).Should().Be(0);
    }

    [Fact]
    public void Ничья_в_пользу_главного()
    {
        // у главного и у a.jpg по 2 очка за анфас; у главного индекс в манифесте больше
        var scene = new CharacterScene("front", null, null);
        var pick = CharacterPhotoPicker.Pick(Set, scene, 3, confidentScore: 1);
        Files(pick)[0].Should().Be("main.jpg");
        Files(pick)[1].Should().Be("a.jpg");
    }

    [Fact]
    public void Ничья_без_главного_решает_порядок_манифеста()
    {
        var photos = new[] { P("a.jpg", angle: "front"), P("b.jpg", angle: "front"), P("c.jpg", angle: "profile") };
        var pick = CharacterPhotoPicker.Pick(photos, new CharacterScene("front", null, null), 3, confidentScore: 1);
        Files(pick).Should().Equal("a.jpg", "b.jpg", "c.jpg");
    }

    [Fact]
    public void Главное_всегда_в_наборе_при_N3_и_N2()
    {
        var photos = new[]
        {
            P("a.jpg", angle: "profile"), P("b.jpg", angle: "profile"), P("c.jpg", angle: "profile"),
            P("d.jpg", angle: "profile"), P("m.jpg", primary: true, angle: "front"),
        };
        var scene = new CharacterScene("profile", null, null);
        foreach (var confident in new[] { CharacterPhotoPicker.LocalConfidentScore, 1 })
        {
            Files(CharacterPhotoPicker.Pick(photos, scene, 3, confident)).Should().Contain("m.jpg");
            Files(CharacterPhotoPicker.Pick(photos, scene, 2, confident)).Should().Contain("m.jpg");
        }
    }

    [Fact]
    public void При_пороге_2_по_ракурсу_лучшее_идёт_первым()
    {
        // Default confidentScore = 2: точное совпадение ракурса → b (profile) первым
        var pick = CharacterPhotoPicker.Pick(Set, new CharacterScene("profile", null, null), 3);
        pick.Pick.Should().Be("auto");
        Files(pick).Should().Equal("b.jpg", "main.jpg", "c.jpg");
    }

    [Fact]
    public void При_достигнутом_пороге_лучшее_фото_идёт_первым_а_главное_вторым()
    {
        var pick = CharacterPhotoPicker.Pick(Set, new CharacterScene("profile", null, null), 3, confidentScore: 2);
        Files(pick).Should().Equal("b.jpg", "main.jpg", "c.jpg");
    }

    [Fact]
    public void Лучшее_ниже_порога_первым_не_идёт()
    {
        var pick = CharacterPhotoPicker.Pick(Set, new CharacterScene("profile", null, null), 3, confidentScore: 3);
        Files(pick)[0].Should().Be("main.jpg");
    }

    [Fact]
    public void Остальные_по_очкам()
    {
        var photos = new[]
        {
            P("m.jpg", primary: true, angle: "front"),
            P("low.jpg", angle: "front", emotion: "sad"),
            P("mid.jpg", angle: "three-quarter", emotion: "laugh"),
            P("top.jpg", angle: "profile", emotion: "smile"),
        };
        var pick = CharacterPhotoPicker.Pick(photos, new CharacterScene("profile", null, "smile"), 4);
        // Точное совпадение ракурса → top первым, main вторым, остальные по очкам
        Files(pick).Should().Equal("top.jpg", "m.jpg", "mid.jpg", "low.jpg");
    }

    [Fact]
    public void N1_отдаёт_одно_фото_и_N_больше_числа_фото_не_падает()
    {
        var scene = new CharacterScene("profile", null, null);
        // Точное совпадение ракурса → b (profile) первым
        Files(CharacterPhotoPicker.Pick(Set, scene, 1)).Should().Equal("b.jpg");
        CharacterPhotoPicker.Pick(Set, scene, 10).Photos.Should().HaveCount(4);
    }

    // ── Тесты подмены при точном совпадении ракурса (ADR-027 §7, замер шага 21) ──

    [Fact]
    public void Вполоборота_без_точного_совпадения_даёт_главное()
    {
        // Только front и profile: three-quarter — соседний (1 очко), не точное совпадение
        var photos = new[]
        {
            P("m.jpg", primary: true, angle: "front"),
            P("a.jpg", angle: "front"),
            P("b.jpg", angle: "profile"),
        };
        var pick = CharacterPhotoPicker.Pick(photos, new CharacterScene("three-quarter", null, null), 1);
        pick.Pick.Should().Be("auto");
        Files(pick).Should().Equal("m.jpg");
    }

    [Fact]
    public void Эмоция_без_ракурса_не_подменяет_главное()
    {
        // laugh-эмоция даёт 2 очка, но ракурс в сцене не задан → подмены нет
        var photos = new[]
        {
            P("m.jpg", primary: true, angle: "front", emotion: "neutral"),
            P("a.jpg", angle: "front", emotion: "laugh"),
        };
        var pick = CharacterPhotoPicker.Pick(photos, new CharacterScene(null, null, "laugh"), 1);
        pick.Pick.Should().Be("auto");
        Files(pick).Should().Equal("m.jpg");
    }

    [Fact]
    public void Нейтральный_сюжет_даёт_главное()
    {
        // Сюжет без ракурса и без эмоции → фолбэк, главное первым
        var pick = CharacterPhotoPicker.Pick(Set, new CharacterScene(null, null, null), 1);
        pick.Pick.Should().Be("fallback");
        Files(pick).Should().Equal("main.jpg");
    }
}
