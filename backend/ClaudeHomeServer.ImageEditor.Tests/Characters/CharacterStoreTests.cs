using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Хранилище персонажей characters/<slug>/ (ADR-017, раздел 10): граница проекта, формат
// character.json с заделом providers.higgsfield, «существующее не перетираем», персонаж
// доходит до запроса драйвера.
public class CharacterStoreTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ccs_chars_" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public CharacterStoreTests()
    {
        _root = Path.Combine(_base, "project");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    internal static byte[] Jpeg(byte tag) => TestImages.Jpeg(tag);

    internal static CharacterPhotoUpload[] Photos(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new CharacterPhotoUpload(Jpeg((byte)i), i == 1 ? "front" : null))];

    private CharacterManifest Create(string name, string? description = null)
    {
        var result = CharacterStore.Create(_root, new CharacterDraft(name, description, Photos(3)), DateTime.UtcNow);
        result.Error.Should().BeNull();
        return result.Value!.Manifest;
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("a/../../x")]
    [InlineData("/etc")]
    [InlineData("Anya")]
    public void Slug_с_обходом_пути_не_выходит_из_characters(string slug)
    {
        // Соседняя папка с «персонажем» вне characters/: до неё не должен дотянуться ни один метод
        var outside = Path.Combine(_root, "x");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, CharacterStore.ManifestFile),
            """{ "formatVersion": 1, "name": "чужой", "slug": "x", "photos": [ { "file": "face-01.jpg" } ] }""");
        File.WriteAllBytes(Path.Combine(outside, "face-01.jpg"), Jpeg(1));

        CharacterStore.CharacterDir(_root, slug).Should().BeNull();
        CharacterStore.Get(_root, slug).Should().BeNull();
        CharacterStore.OpenPhoto(_root, slug, "face-01.jpg").Should().BeNull();
        CharacterStore.ForRequest(_root, slug).Should().BeNull();
        CharacterStore.Delete(_root, slug).Should().BeFalse();

        Directory.Exists(outside).Should().BeTrue("удаление по slug с обходом пути не должно ничего снести");
    }

    [Fact]
    public void Имя_с_обходом_пути_даёт_папку_внутри_characters()
    {
        var manifest = Create("../x");

        manifest.Slug.Should().Be("x");
        Directory.Exists(Path.Combine(_root, CharacterStore.Folder, "x")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "x")).Should().BeFalse();
    }

    [Fact]
    public void Character_json_содержит_секцию_providers_higgsfield()
    {
        var manifest = Create("Аня", "девушка 25 лет, каре");

        var json = JsonNode.Parse(File.ReadAllText(
            Path.Combine(_root, CharacterStore.Folder, manifest.Slug, CharacterStore.ManifestFile)))!;
        json["formatVersion"]!.GetValue<int>().Should().Be(1);
        json["slug"]!.GetValue<string>().Should().Be("anya");
        json["name"]!.GetValue<string>().Should().Be("Аня");
        json["photos"]!.AsArray().Should().HaveCount(3);
        json["photos"]![0]!["primary"]!.GetValue<bool>().Should().BeTrue();
        var higgsfield = json["providers"]?["higgsfield"];
        higgsfield.Should().NotBeNull("задел под Soul ID — словарь providers с ключом higgsfield");
        higgsfield!.AsObject().Select(p => p.Key).Should().Contain(["soulId", "status", "trainedAt", "photosHash"]);
    }

    [Fact]
    public void Занятое_имя_даёт_новую_папку_и_не_перетирает_существующую()
    {
        var first = Create("Аня");
        var photo = Path.Combine(_root, CharacterStore.Folder, first.Slug, first.Photos[0].File);
        var before = File.ReadAllBytes(photo);

        var second = Create("Аня");

        second.Slug.Should().Be("anya-2");
        File.ReadAllBytes(photo).Should().Equal(before);
        CharacterStore.List(_root).Select(m => m.Slug).Should().Equal("anya", "anya-2");
    }

    [Fact]
    public void Правка_сохраняет_незнакомые_ключи_поставщиков()
    {
        var manifest = Create("Аня");
        var file = Path.Combine(_root, CharacterStore.Folder, manifest.Slug, CharacterStore.ManifestFile);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["providers"]!["glif"] = new JsonObject { ["id"] = "g-1" };
        json["futureField"] = "keep";
        File.WriteAllText(file, json.ToJsonString());

        var updated = CharacterStore.Update(_root, manifest.Slug,
            new CharacterPatch("Анна", null, Photos(1), [manifest.Photos[1].File], null));

        updated!.Error.Should().BeNull();
        var after = JsonNode.Parse(File.ReadAllText(file))!;
        after["name"]!.GetValue<string>().Should().Be("Анна");
        after["providers"]!["glif"]!["id"]!.GetValue<string>().Should().Be("g-1");
        after["futureField"]!.GetValue<string>().Should().Be("keep");
        after["photos"]!.AsArray().Select(p => p!["file"]!.GetValue<string>())
            .Should().Equal("face-01.jpg", "face-03.jpg", "face-04.jpg");
    }

    [Fact]
    public void Меньше_трёх_фото_и_не_картинка_отклоняются()
    {
        CharacterStore.Create(_root, new CharacterDraft("Аня", null, Photos(2)), DateTime.UtcNow)
            .ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        CharacterStore.Create(_root,
                new CharacterDraft("Аня", null, [.. Photos(2), new CharacterPhotoUpload("text"u8.ToArray())]),
                DateTime.UtcNow)
            .ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        Directory.Exists(Path.Combine(_root, CharacterStore.Folder, "anya")).Should().BeFalse();
    }

    [Fact]
    public void Папка_characters_символической_ссылкой_наружу_не_читается()
    {
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "anya"));
        File.WriteAllText(Path.Combine(outside, "anya", CharacterStore.ManifestFile),
            """{ "formatVersion": 1, "name": "Аня", "slug": "anya", "photos": [] }""");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, CharacterStore.Folder), outside);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return; // Windows без прав на ссылки — проверка идёт в CI на Linux
        }

        CharacterStore.List(_root).Should().BeEmpty();
        CharacterStore.Get(_root, "anya").Should().BeNull();
        CharacterStore.Create(_root, new CharacterDraft("Боря", null, Photos(3)), DateTime.UtcNow)
            .ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        Directory.EnumerateDirectories(outside).Should().ContainSingle("наружу ничего не записано");
    }

    [Fact]
    public void Персонаж_уходит_в_запрос_драйвера_ссылкой_и_фото_с_ролью_character()
    {
        Create("Аня", "каре, веснушки");
        var character = CharacterStore.ForRequest(_root, "anya")!;
        character.Photos.Should().HaveCount(3).And.OnlyContain(p => p.Role == ReferenceRole.Character);

        var model = new ImageEditModelInfo("m", "M",
            new ImageEditCaps([ImageEditOp.Edit], MaskSupport.None, 6, 4, true));
        var input = new ImageEditJobInput("q", "в осеннем парке", null,
            new ImageBytes(Jpeg(9), "image/jpeg"), null, null, character.Photos, "hero.jpg", character.Ref);

        var request = EditRequestComposer.Compose(input, ImageEditOp.Edit, model, 1).Value!;

        request.Character.Should().Be(new CharacterRef("anya", "Аня", "каре, веснушки"));
        request.References.Where(r => r.Role == ReferenceRole.Character).Should().HaveCount(3);
        request.Prompt.Should().Contain("Аня").And.Contain("каре, веснушки");
    }
}
