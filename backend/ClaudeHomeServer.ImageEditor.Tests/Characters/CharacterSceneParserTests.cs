using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Разбор сюжета запроса (ADR-027, §6): модель плюс запасной словарь правил
public class CharacterSceneParserTests
{
    private sealed class FakeRunner(Func<string, CancellationToken, Task<string?>> run) : ICheapTextRunner
    {
        public int Calls;
        public string? LastActionKey;
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";
        // CharacterSceneParser зовёт RunLocalOrChosenAsync: локаль по умолчанию → без
        // облачного шага (бесплатный фоновый разбор), явный выбор → полная цепочка.
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            return await run(prompt, ct);
        }
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static FakeRunner Answer(string? raw) => new((_, _) => Task.FromResult(raw));

    [Theory]
    [InlineData("портрет в профиль у окна", "profile", null, null)]
    [InlineData("Профиль на закате", "profile", null, null)]
    [InlineData("стоит вполоборота", "three-quarter", null, null)]
    [InlineData("три четверти, мягкий свет", "three-quarter", null, null)]
    [InlineData("смотрит анфас", "front", null, null)]
    [InlineData("смотрит в камеру", "front", null, null)]
    [InlineData("она смеётся", null, null, "laugh")]
    [InlineData("громкий смех", null, null, "laugh")]
    [InlineData("тихо улыбается", null, null, "smile")]
    [InlineData("лёгкая улыбка", null, null, "smile")]
    [InlineData("серьёзный взгляд", null, null, "serious")]
    [InlineData("грустная девушка", null, null, "sad")]
    [InlineData("удивлённое лицо", null, null, "surprised")]
    [InlineData("снято сверху", null, null, null)]
    [InlineData("снимок снизу", null, null, null)]
    // допустимо: подбор мягко штрафует, фиксируем текущее поведение подстрочного поиска
    [InlineData("профиль компании", "profile", null, null)]
    [InlineData("серьёзный бизнес", null, null, "serious")]
    [InlineData("кот на диване", null, null, null)]
    [InlineData("", null, null, null)]
    [InlineData("   ", null, null, null)]
    public void Правила_находят_признаки_в_тексте(string prompt, string? angle, string? pitch, string? emotion)
    {
        CharacterSceneParser.ParseByRules(prompt).Should().Be(new CharacterScene(angle, pitch, emotion));
    }

    [Fact]
    public async Task Пустой_сюжет_не_зовёт_раннер()
    {
        var runner = Answer("""{"angle":"front"}""");

        var scene = await new CharacterSceneParser(runner).ParseAsync("  ");

        scene.IsEmpty.Should().BeTrue();
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Ответ_модели_раскладывается_по_словарям()
    {
        var runner = Answer("""{"angle":"profile","pitch":null,"emotion":"laugh"}""");

        var scene = await new CharacterSceneParser(runner).ParseAsync("что угодно");

        scene.Should().Be(new CharacterScene("profile", null, "laugh"));
        runner.LastActionKey.Should().Be(LocalActionCatalog.CharacterSceneParse);
    }

    [Fact]
    public async Task Значение_вне_словаря_даёт_null_для_поля()
    {
        var runner = Answer("""{"angle":"sideways","pitch":"unknown","emotion":"smile"}""");

        var scene = await new CharacterSceneParser(runner).ParseAsync("в профиль");

        scene.Should().Be(new CharacterScene(null, null, "smile"));
    }

    [Fact]
    public async Task Сбой_раннера_отдаёт_правила()
    {
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("упал"));

        var scene = await new CharacterSceneParser(runner).ParseAsync("смеётся в профиль");

        scene.Should().Be(new CharacterScene("profile", null, "laugh"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не json вовсе")]
    [InlineData("[1,2,3]")]
    public async Task Пустой_или_мусорный_ответ_отдаёт_правила(string? raw)
    {
        var scene = await new CharacterSceneParser(Answer(raw)).ParseAsync("улыбается анфас");

        scene.Should().Be(new CharacterScene("front", null, "smile"));
    }

    [Fact]
    public async Task Таймаут_отдаёт_правила_даже_если_раннер_не_слушает_токен()
    {
        var never = new TaskCompletionSource<string?>();
        var runner = new FakeRunner((_, _) => never.Task);

        var scene = await new CharacterSceneParser(runner, TimeSpan.FromMilliseconds(50)).ParseAsync("серьёзный профиль");

        scene.Should().Be(new CharacterScene("profile", null, "serious"));
    }

    [Fact]
    public async Task Второй_вызов_с_тем_же_сюжетом_берёт_кэш()
    {
        var runner = Answer("""{"angle":"front","pitch":null,"emotion":null}""");
        var parser = new CharacterSceneParser(runner);

        var first = await parser.ParseAsync("сюжет");
        var second = await parser.ParseAsync("сюжет");

        runner.Calls.Should().Be(1);
        second.Should().Be(first);
    }

    [Fact]
    public async Task Сбой_не_кэшируется()
    {
        var attempt = 0;
        var runner = new FakeRunner((_, _) =>
            ++attempt == 1 ? throw new InvalidOperationException() : Task.FromResult<string?>("""{"angle":"front"}"""));
        var parser = new CharacterSceneParser(runner);

        await parser.ParseAsync("сюжет");
        var second = await parser.ParseAsync("сюжет");

        runner.Calls.Should().Be(2);
        second.Angle.Should().Be("front");
    }

    [Fact]
    public void Место_разбора_объявлено_в_каталоге_с_малым_профилем()
    {
        var action = LocalActionCatalog.All.Single(a => a.Key == "character-scene-parse");
        action.Profile.Should().Be(CheapProfile.Small);
        action.DefaultLocal.Should().BeTrue();
        action.Title.Should().Be("Разбор сюжета для подбора фото");
    }
}
