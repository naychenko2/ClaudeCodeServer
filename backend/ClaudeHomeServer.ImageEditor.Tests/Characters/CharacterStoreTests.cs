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

    internal static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    internal static CharacterPhotoUpload[] Photos(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new CharacterPhotoUpload(Jpeg((byte)i), i == 1 ? "front" : null))];

    private CharacterManifest Create(string name, string? description = null)
    {
        var result = CharacterStore.Create(_root, new CharacterDraft(name, description, Photos(3)), DateTime.UtcNow);
        result.Error.Should().BeNull();
        return result.Value!.Manifest;
    }

    [Theory]
    [InlineData("anya\n")]
    [InlineData("a\n")]
    public void Slug_с_переводом_строки_на_конце_отклоняется(string slug)
    {
        CharacterStore.IsValidSlug(slug).Should().BeFalse();
    }

    [Theory]
    [InlineData("ccsandrey\n")]
    [InlineData("ccs andrey")]
    [InlineData("")]
    public void Триггер_с_переводом_строки_или_пробелом_отклоняется(string trigger)
    {
        CharacterStore.IsValidTrigger(trigger).Should().BeFalse();
        CharacterStore.IsValidTrigger("ccsandrey_1").Should().BeTrue();
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
        CharacterStore.ForRequest(_root, Owner, slug).Should().BeNull();
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

        var updated = CharacterStore.Update(_root, Owner, manifest.Slug,
            new CharacterPatch("Анна", null, Photos(1), [manifest.Photos[1].File], null));

        updated!.Error.Should().BeNull();
        var after = JsonNode.Parse(File.ReadAllText(file))!;
        after["name"]!.GetValue<string>().Should().Be("Анна");
        after["providers"]!["glif"]!["id"]!.GetValue<string>().Should().Be("g-1");
        after["futureField"]!.GetValue<string>().Should().Be("keep");
        after["photos"]!.AsArray().Select(p => p!["file"]!.GetValue<string>())
            .Should().Equal("face-01.jpg", "face-03.jpg", "face-04.jpg");
    }

    private void SetLocal(CharacterManifest m, JsonObject? local)
    {
        var file = Path.Combine(_root, CharacterStore.Folder, m.Slug, CharacterStore.ManifestFile);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["providers"]![CharacterManifest.LocalProvider] = local;
        File.WriteAllText(file, json.ToJsonString());
    }

    private const string Owner = "0a1b2c3d-0000-0000-0000-000000000001";
    private static readonly string OwnLora = LoraNamespace.PrefixFor(Owner) + "anya.safetensors";

    [Theory]
    [InlineData(null, 0.8)]
    [InlineData(0.55, 0.55)]
    public void Лора_из_providers_local_доходит_до_запроса(double? strength, double expected)
    {
        var m = Create("Аня");
        var local = new JsonObject { ["lora"] = OwnLora, ["trigger"] = "anya_person", ["photosHash"] = "h" };
        if (strength is { } s) local["strength"] = s;
        SetLocal(m, local);

        var lora = CharacterStore.ForRequest(_root, Owner, m.Slug)!.Ref.Lora;

        lora.Should().Be(new CharacterLora(OwnLora, "anya_person", expected));
    }

    [Theory]
    [InlineData("anya.safetensors")]
    [InlineData("ccs/ffffffffffffffffffffffffffffffff/anya.safetensors")]
    [InlineData("ccs/0a1b2c3d000000000000000000000001/../x.safetensors")]
    public void Чужая_лора_из_манифеста_отбрасывается(string lora)
    {
        var m = Create("Аня");
        SetLocal(m, new JsonObject { ["lora"] = lora, ["trigger"] = "anya_person", ["strength"] = 0.9 });

        var found = CharacterStore.ForRequest(_root, Owner, m.Slug)!;

        found.Ref.Lora.Should().BeNull();
        found.Photos.Should().NotBeEmpty();
    }

    [Fact]
    public void Без_блока_или_без_lora_trigger_лоры_нет()
    {
        var m = Create("Аня");
        CharacterStore.ForRequest(_root, Owner, m.Slug)!.Ref.Lora.Should().BeNull();
        SetLocal(m, new JsonObject { ["lora"] = "a.safetensors" });
        CharacterStore.ForRequest(_root, Owner, m.Slug)!.Ref.Lora.Should().BeNull();
        SetLocal(m, new JsonObject { ["trigger"] = "t" });
        CharacterStore.ForRequest(_root, Owner, m.Slug)!.Ref.Lora.Should().BeNull();
    }

    [Fact]
    public void Круг_Parse_Serialize_сохраняет_providers_local_и_чужие_ключи()
    {
        var json = """{"name":"Аня","slug":"anya","futureField":1,"providers":{"local":{"lora":"a","trigger":"t","strength":0.7,"x":"y"},"glif":{"id":"g"}}}""";
        var again = CharacterManifest.Parse(CharacterManifest.Parse(json)!.Serialize())!;
        var node = JsonNode.Parse(again.Serialize())!;
        node["providers"]!["local"]!["x"]!.GetValue<string>().Should().Be("y");
        node["providers"]!["local"]!["strength"]!.GetValue<double>().Should().Be(0.7);
        node["providers"]!["glif"]!["id"]!.GetValue<string>().Should().Be("g");
        node["futureField"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Лора_не_попадает_в_json_CharacterRef()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new CharacterRef("anya", "Аня", null, new CharacterLora("a", "t", 0.8)));
        json.Should().NotContainEquivalentOf("lora");
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
        var character = CharacterStore.ForRequest(_root, Owner, "anya")!;
        character.Photos.Should().HaveCount(3).And.OnlyContain(p => p.Role == ReferenceRole.Character);

        var model = new ImageEditModelInfo("m", "M",
            new ImageEditCaps([ImageEditOp.Edit], MaskSupport.None, 6, 4, true));
        var input = new ImageEditJobInput("q", "в осеннем парке", null,
            new ImageBytes(Jpeg(9), "image/jpeg"), null, null, character.Photos, "hero.jpg", character.Ref);

        var request = EditRequestComposer.Compose(input, ImageEditOp.Edit, model, 1).Value!;

        request.Character.Should().BeEquivalentTo(new CharacterRef("anya", "Аня", "каре, веснушки"),
            o => o.Excluding(c => c.FacePhotos));
        request.References.Where(r => r.Role == ReferenceRole.Character).Should().HaveCount(3);
        request.Prompt.Should().Contain("Аня").And.Contain("каре, веснушки");
    }

    [Fact]
    public void FacePhotos_содержит_все_фото_а_Photos_ограничено_maxPhotos()
    {
        var m = Create("Аня");
        var character = CharacterStore.ForRequest(_root, Owner, m.Slug, maxPhotos: 1)!;
        character.Photos.Should().HaveCount(1);
        character.Ref.FacePhotos.Should().HaveCount(3);
    }

    [Fact]
    public void FacePhotos_не_берёт_симлинк_и_битый_файл()
    {
        var m = Create("Аня");
        var dir = Path.Combine(_root, CharacterStore.Folder, m.Slug);
        var files = m.Photos.Select(p => p.File).ToList();
        File.WriteAllBytes(Path.Combine(dir, files[1]), [1, 2, 3]);
        var target = Path.Combine(_base, "outside.jpg");
        File.WriteAllBytes(target, Jpeg(7));
        File.Delete(Path.Combine(dir, files[2]));
        try { File.CreateSymbolicLink(Path.Combine(dir, files[2]), target); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Windows без прав на ссылки — проверка идёт в CI на Linux
        }

        CharacterStore.ForRequest(_root, Owner, m.Slug)!.Ref.FacePhotos.Should().HaveCount(1);
    }

    [Fact]
    public void FacePhotos_не_больше_десяти_при_двенадцати_фото()
    {
        var m = Create("Аня");
        var dir = Path.Combine(_root, CharacterStore.Folder, m.Slug);
        var json = JsonNode.Parse(File.ReadAllText(ManifestPath(m)))!;
        var photos = json["photos"]!.AsArray();
        for (var i = 4; i <= 12; i++)
        {
            var file = $"face-{i:00}.jpg";
            File.WriteAllBytes(Path.Combine(dir, file), Jpeg((byte)i));
            photos.Add(new JsonObject { ["file"] = file });
        }
        File.WriteAllText(ManifestPath(m), json.ToJsonString());

        CharacterStore.ForRequest(_root, Owner, m.Slug)!.Ref.FacePhotos.Should().HaveCount(10);
    }

    [Fact]
    public void Явный_список_берёт_ровно_эти_фото_в_этом_порядке()
    {
        var m = Create("Аня");
        var files = m.Photos.Select(p => p.File).ToList();
        var chosen = new[] { files[2], files[1] };

        var found = CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: chosen)!;

        found.Photos.Select(p => Convert.ToHexString(p.Bytes)).Should().Equal(Hex(Jpeg(3)), Hex(Jpeg(2)));
    }

    [Fact]
    public void Эталон_facePhotos_не_зависит_от_явного_списка()
    {
        var m = Create("Аня");
        var files = m.Photos.Select(p => p.File).ToList();
        var byDefault = CharacterStore.ForRequest(_root, Owner, m.Slug)!.Ref.FacePhotos;

        var explicitOne = CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: [files[2]])!;

        explicitOne.Photos.Should().ContainSingle();
        explicitOne.Ref.FacePhotos.Should().HaveCount(3).And.Subject.Select(Hex).Should().Equal(byDefault!.Select(Hex));
    }

    [Fact]
    public void Без_явного_списка_образцы_как_раньше()
    {
        var m = Create("Аня");
        var byDefault = CharacterStore.ForRequest(_root, Owner, m.Slug)!;
        var withNull = CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: null)!;

        withNull.Photos.Select(p => Hex(p.Bytes)).Should().Equal(byDefault.Photos.Select(p => Hex(p.Bytes)));
    }

    [Theory]
    [InlineData("other.jpg")]
    [InlineData("../x.png")]
    [InlineData("/etc/passwd")]
    [InlineData("character.json")]
    public void Явный_список_с_файлом_вне_манифеста_отклоняется_целиком(string bad)
    {
        var m = Create("Аня");
        var files = m.Photos.Select(p => p.File).ToList();
        File.WriteAllBytes(Path.Combine(_root, CharacterStore.Folder, m.Slug, "other.jpg"), Jpeg(8));
        File.WriteAllBytes(Path.Combine(_root, "x.png"), Jpeg(8));

        CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: [files[0], bad]).Should().BeNull();
    }

    [Fact]
    public void Явный_список_с_символической_ссылкой_отклоняется()
    {
        var m = Create("Аня");
        var dir = Path.Combine(_root, CharacterStore.Folder, m.Slug);
        var files = m.Photos.Select(p => p.File).ToList();
        var target = Path.Combine(_base, "outside.jpg");
        File.WriteAllBytes(target, Jpeg(7));
        File.Delete(Path.Combine(dir, files[2]));
        try { File.CreateSymbolicLink(Path.Combine(dir, files[2]), target); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Windows без прав на ссылки — проверка идёт в CI на Linux
        }

        CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: [files[0], files[2]]).Should().BeNull();
    }

    [Fact]
    public void Явный_список_с_дублем_пустой_или_длиннее_лимита_отклоняется()
    {
        var m = Create("Аня");
        var files = m.Photos.Select(p => p.File).ToList();

        CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: [files[0], files[0]]).Should().BeNull();
        CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: []).Should().BeNull();
        CharacterStore.ForRequest(_root, Owner, m.Slug, explicitPhotos: files).Should().NotBeNull();
        CharacterStore.ForRequest(_root, Owner, m.Slug, maxPhotos: 2, explicitPhotos: files).Should().BeNull();
    }

    private string ManifestPath(CharacterManifest m) =>
        Path.Combine(_root, CharacterStore.Folder, m.Slug, CharacterStore.ManifestFile);

    private static CharacterPatch LoraPatch(CharacterLoraPatch lora) => new(null, null, [], [], null, lora);

    private static readonly string[] KnownLoras = [OwnLora, LoraNamespace.PrefixFor(Owner) + "other.safetensors"];

    private CharacterManifest WithLocalBlock()
    {
        var m = Create("Аня");
        var file = ManifestPath(m);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["providers"]![CharacterManifest.LocalProvider] = new JsonObject
        {
            ["photosHash"] = "h1", ["trainedAt"] = "2026-10-01", ["futureKey"] = "keep",
        };
        File.WriteAllText(file, json.ToJsonString());
        return m;
    }

    [Fact]
    public void Задать_лору_пишет_три_ключа_и_не_трогает_остальное()
    {
        var m = WithLocalBlock();

        var result = CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(OwnLora, "anya_face", null, KnownLoras)));

        result!.Error.Should().BeNull();
        var providers = JsonNode.Parse(File.ReadAllText(ManifestPath(m)))!["providers"]!;
        var local = providers[CharacterManifest.LocalProvider]!;
        local["lora"]!.GetValue<string>().Should().Be(OwnLora);
        local["trigger"]!.GetValue<string>().Should().Be("anya_face");
        local["strength"]!.GetValue<double>().Should().Be(CharacterManifest.DefaultLoraStrength);
        local["photosHash"]!.GetValue<string>().Should().Be("h1");
        local["trainedAt"]!.GetValue<string>().Should().Be("2026-10-01");
        local["futureKey"]!.GetValue<string>().Should().Be("keep");
        providers[CharacterManifest.HiggsfieldProvider].Should().NotBeNull();
    }

    [Fact]
    public void Убрать_лору_снимает_три_ключа_и_оставляет_чужой()
    {
        var m = WithLocalBlock();
        CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(OwnLora, "anya_face", 1.2, KnownLoras)));

        var result = CharacterStore.Update(_root, Owner, m.Slug, LoraPatch(CharacterLoraPatch.Removal()));

        result!.Error.Should().BeNull();
        var local = JsonNode.Parse(File.ReadAllText(ManifestPath(m)))!["providers"]![CharacterManifest.LocalProvider]!.AsObject();
        local.ContainsKey("lora").Should().BeFalse();
        local.ContainsKey("trigger").Should().BeFalse();
        local.ContainsKey("strength").Should().BeFalse();
        local["futureKey"]!.GetValue<string>().Should().Be("keep");
    }

    [Fact]
    public void Убрать_лору_удаляет_опустевший_блок_local()
    {
        var m = Create("Аня");
        CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(OwnLora, "anya_face", null, KnownLoras)));

        CharacterStore.Update(_root, Owner, m.Slug, LoraPatch(CharacterLoraPatch.Removal()))!.Error.Should().BeNull();

        var providers = JsonNode.Parse(File.ReadAllText(ManifestPath(m)))!["providers"]!.AsObject();
        providers.ContainsKey(CharacterManifest.LocalProvider).Should().BeFalse();
    }

    [Fact]
    public void Без_патча_лоры_блок_local_не_меняется()
    {
        var m = WithLocalBlock();
        var before = File.ReadAllBytes(ManifestPath(m));

        var result = CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null));

        result!.Error.Should().BeNull();
        var local = JsonNode.Parse(File.ReadAllText(ManifestPath(m)))!["providers"]![CharacterManifest.LocalProvider]!;
        local.ToJsonString().Should().Be(JsonNode.Parse(before)!["providers"]![CharacterManifest.LocalProvider]!.ToJsonString());
    }

    [Theory]
    [InlineData("anya.safetensors")]
    [InlineData("ccs/ffffffffffffffffffffffffffffffff/anya.safetensors")]
    public void Чужая_или_голая_лора_напрямую_в_сторе_отказ_и_файл_не_меняется(string name)
    {
        var m = Create("Аня");
        var before = File.ReadAllBytes(ManifestPath(m));
        // имя есть в списке ComfyUI: отказывает именно видимость, а не «нет в списке»
        var patch = LoraPatch(CharacterLoraPatch.Set(name, "anya_face", null, [name]));

        CharacterStore.CheckLora(Owner, patch.Lora!)!.ErrorCode.Should().Be(ImageEditErrorCodes.LoraNotVisible);
        var result = CharacterStore.Update(_root, Owner, m.Slug, patch);

        result!.ErrorCode.Should().Be(ImageEditErrorCodes.LoraNotVisible);
        File.ReadAllBytes(ManifestPath(m)).Should().Equal(before);
    }

    [Fact]
    public void Лора_чужому_владельцу_отказ_а_без_владельца_тем_более()
    {
        var m = Create("Аня");
        var patch = LoraPatch(CharacterLoraPatch.Set(OwnLora, "anya_face", null, [OwnLora]));

        CharacterStore.Update(_root, "ffffffff-0000-0000-0000-000000000002", m.Slug, patch)!.ErrorCode
            .Should().Be(ImageEditErrorCodes.LoraNotVisible);
        CharacterStore.Update(_root, "", m.Slug, patch)!.ErrorCode.Should().Be(ImageEditErrorCodes.LoraNotVisible);
        CharacterStore.CheckLora(Owner, patch.Lora!).Should().BeNull();
    }

    [Fact]
    public void Имя_не_из_списка_ComfyUI_отказ_и_файл_не_меняется()
    {
        var m = Create("Аня");
        var before = File.ReadAllBytes(ManifestPath(m));

        var result = CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(LoraNamespace.PrefixFor(Owner) + "ghost.safetensors", "anya_face", null, KnownLoras)));

        result!.Error.Should().NotBeNull();
        result!.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        File.ReadAllBytes(ManifestPath(m)).Should().Equal(before);
    }

    [Fact]
    public void ComfyUI_недоступен_отказ_provider_unavailable_и_файл_не_меняется()
    {
        var m = Create("Аня");
        var before = File.ReadAllBytes(ManifestPath(m));

        var result = CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(OwnLora, "anya_face", null, null)));

        result!.ErrorCode.Should().Be(ImageEditErrorCodes.ProviderUnavailable);
        File.ReadAllBytes(ManifestPath(m)).Should().Equal(before);
    }

    [Theory]
    [InlineData("два слова", null)]
    [InlineData("", null)]
    [InlineData("anya_face", 2.5)]
    [InlineData("anya_face", -0.1)]
    public void Плохой_триггер_или_сила_отказ_и_файл_не_меняется(string trigger, double? strength)
    {
        var m = Create("Аня");
        var before = File.ReadAllBytes(ManifestPath(m));

        var result = CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(OwnLora, trigger, strength, KnownLoras)));

        result!.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        File.ReadAllBytes(ManifestPath(m)).Should().Equal(before);
    }

    [Fact]
    public void Лору_можно_сменить_у_персонажа_с_11_фото_на_диске()
    {
        var m = Create("Аня");
        var file = ManifestPath(m);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        var photos = json["photos"]!.AsArray();
        for (var i = 4; i <= 11; i++) photos.Add(new JsonObject { ["file"] = $"face-{i:00}.jpg", ["primary"] = false });
        File.WriteAllText(file, json.ToJsonString());

        var result = CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(OwnLora, "anya_face", 1.0, KnownLoras)));

        result!.Error.Should().BeNull();
        CharacterStore.Get(_root, m.Slug)!.Photos.Should().HaveCount(11);
    }

    [Fact]
    public void Dto_отдаёт_Local_и_не_отдаёт_Providers()
    {
        var m = Create("Аня");
        CharacterStore.Update(_root, Owner, m.Slug,
            LoraPatch(CharacterLoraPatch.Set(OwnLora, "anya_face", 0.9, KnownLoras)));

        var dto = CharacterDto.From(CharacterStore.Get(_root, m.Slug)!);

        dto.Local.Should().Be(new CharacterLocalDto(OwnLora, "anya_face", 0.9));
        typeof(CharacterDto).GetProperty("Providers").Should().BeNull();
        CharacterDto.From(m).Local.Should().BeNull();
    }

    [Fact]
    public void Dto_отдаёт_PhotosPerRequest_только_при_включённом_флаге()
    {
        var m = CharacterStore.Get(_root, Create("Аня").Slug)!;

        CharacterDto.From(m, withLabels: true).PhotosPerRequest.Should().Be(CharacterStore.PhotosPerRequest);
        CharacterDto.From(m).PhotosPerRequest.Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(CharacterDto.From(m)).Should().NotContain("hotosPerRequest");
    }

    // --- Метки фото (ADR-027, §1) ---

    private static IReadOnlyDictionary<string, CharacterPhotoLabels> Labels(string file, string angle, string emotion) =>
        new Dictionary<string, CharacterPhotoLabels> { [file] = new(angle, "level", emotion) };

    [Fact]
    public void Авторазметка_не_затирает_ручную()
    {
        var m = Create("Аня");
        var file = m.Photos[0].File;
        CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null,
            Labels(file, "profile", "laugh")))!.Error.Should().BeNull();

        CharacterStore.MergeAutoLabels(_root, m.Slug, Labels(file, "front", "sad"))!.Should().BeEmpty();

        var photo = CharacterStore.Get(_root, m.Slug)!.Photos.Single(p => p.File == file);
        photo.Angle.Should().Be("profile");
        photo.Emotion.Should().Be("laugh");
        photo.LabelSource.Should().Be("human");
    }

    [Fact]
    public void Авторазметка_пишет_метки_и_ставит_источник_auto()
    {
        var m = Create("Аня");
        var file = m.Photos[1].File;

        CharacterStore.MergeAutoLabels(_root, m.Slug, Labels(file, "three-quarter", "smile"))!.Should().Equal(file);

        var photo = CharacterStore.Get(_root, m.Slug)!.Photos.Single(p => p.File == file);
        photo.Angle.Should().Be("three-quarter");
        photo.Pitch.Should().Be("level");
        // MergeAutoLabels по умолчанию пишет место ракурса (anglePlace): AngleSource=auto,
        // EmotionSource не трогается (эмоция — отдельное место, своя запись)
        photo.AngleSource.Should().Be("auto");
        photo.EmotionSource.Should().BeNull();
    }

    [Fact]
    public void Старый_манифест_читается_без_потерь()
    {
        var m = Create("Аня");
        var path = ManifestPath(m);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["photos"]![0]!.AsObject().Remove("pitch");
        node["photos"]![0]!["angle"] = "front";
        File.WriteAllText(path, node.ToJsonString());

        var photo = CharacterStore.Get(_root, m.Slug)!.Photos[0];

        photo.Angle.Should().Be("front");
        photo.Pitch.Should().BeNull();
        photo.Emotion.Should().BeNull();
        photo.LabelSource.Should().BeNull();
    }

    [Fact]
    public void Неизвестное_поле_фото_переживает_сохранение()
    {
        var m = Create("Аня");
        var path = ManifestPath(m);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["photos"]![0]!["futureField"] = "keep";
        File.WriteAllText(path, node.ToJsonString());

        CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch("Анна", null, [], [], null))!
            .Error.Should().BeNull();

        JsonNode.Parse(File.ReadAllText(path))!["photos"]![0]!["futureField"]!.GetValue<string>().Should().Be("keep");
    }

    [Fact]
    public void Метка_удалённого_фото_отбрасывается()
    {
        var m = Create("Аня");
        var path = ManifestPath(m);
        var gone = m.Photos[0].File;
        // фото пропало из манифеста за время разметки
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["photos"]!.AsArray().RemoveAt(0);
        File.WriteAllText(path, node.ToJsonString());

        var applied = CharacterStore.MergeAutoLabels(_root, m.Slug, Labels(gone, "front", "smile"));

        applied.Should().BeEmpty();
        CharacterStore.Get(_root, m.Slug)!.Photos.Should().NotContain(p => p.File == gone);
    }

    [Fact]
    public void Ручная_правка_чужого_имени_файла_отказ()
    {
        var m = Create("Аня");
        var result = CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null,
            Labels("nope.jpg", "front", "smile")));
        result!.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    [Fact]
    public void Ручная_правка_со_словом_вне_словаря_отказ()
    {
        var m = Create("Аня");
        var file = m.Photos[0].File;
        var bogus = new Dictionary<string, CharacterPhotoLabels> { [file] = new("bogus", null, null) };

        var result = CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null, bogus));

        result!.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    [Fact]
    public void Ручная_запись_из_одних_null_сбрасывает_разметку_и_открывает_фото_авторазметке()
    {
        var m = Create("Аня");
        var file = m.Photos[0].File;
        var other = m.Photos[1].File;
        CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null,
            Labels(file, "profile", "laugh")))!.Error.Should().BeNull();
        CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null,
            Labels(other, "front", "smile")))!.Error.Should().BeNull();
        var before = CharacterStore.Get(_root, m.Slug)!;
        var empty = new Dictionary<string, CharacterPhotoLabels> { [file] = new(null, null, null) };

        CharacterStore.Update(_root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null, empty))!
            .Error.Should().BeNull();

        var photo = CharacterStore.Get(_root, m.Slug)!.Photos.Single(p => p.File == file);
        photo.Angle.Should().BeNull();
        photo.Pitch.Should().BeNull();
        photo.Emotion.Should().BeNull();
        photo.LabelSource.Should().BeNull();
        CharacterStore.MergeAutoLabels(_root, m.Slug, Labels(file, "front", "smile"))!.Should().Equal(file);
        // MergeAutoLabels (anglePlace) ставит источник места ракурса, а не общий LabelSource фото
        CharacterStore.Get(_root, m.Slug)!.Photos.Single(p => p.File == file).AngleSource.Should().Be("auto");
        // Соседнее фото не задето: ручные метки на месте, манифест не изменился
        var after = CharacterStore.Get(_root, m.Slug)!;
        var neighbour = after.Photos.Single(p => p.File == other);
        neighbour.Angle.Should().Be("front");
        neighbour.Emotion.Should().Be("smile");
        neighbour.LabelSource.Should().Be("human");
        after.Name.Should().Be(before.Name);
        after.Description.Should().Be(before.Description);
    }

    [Fact]
    public void Авторазметка_без_меток_отбрасывается_целиком()
    {
        var m = Create("Аня");
        var file = m.Photos[0].File;
        var empty = new Dictionary<string, CharacterPhotoLabels> { [file] = new(null, null, null) };

        CharacterStore.MergeAutoLabels(_root, m.Slug, empty)!.Should().BeEmpty();

        CharacterStore.Get(_root, m.Slug)!.Photos.Single(p => p.File == file).LabelSource.Should().BeNull();
    }
}
