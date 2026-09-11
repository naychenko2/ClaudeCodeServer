using ClaudeHomeServer.Services.Images;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Роутер генерации картинок: кто отработает запрос при auto/явном выборе, фолбэк
// на второго провайдера, независимость мест друг от друга и поведение, когда не настроен
// ни один провайдер.
public class ImageGenerationServiceTests
{
    private const string Avatar = ImagePlaces.PersonaAvatar;

    // Драйвер-заглушка: считает вызовы и запоминает модель, с которой его позвали
    private sealed class FakeGenerator(string key, bool enabled, bool returnsImages = true) : IImageGenerator
    {
        public string Key => key;
        public string DisplayName => key;
        public bool Enabled => enabled;
        public IReadOnlyList<ImageModelInfo> Models { get; init; } = [];
        public int Calls { get; private set; }
        public string? LastModel { get; private set; }

        public Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
            string prompt, int count, string? model, CancellationToken ct = default)
        {
            Calls++;
            LastModel = model;
            IReadOnlyList<GeneratedImage> result = returnsImages
                ? [new GeneratedImage([1, 2, 3], "image/png")]
                : [];
            return Task.FromResult(result);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_img_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ImageGenerationSettingsStore Store(string tempDir, params (string Key, string Value)[] config)
    {
        var values = new Dictionary<string, string?> { ["DataPath"] = Path.Combine(tempDir, "projects.json") };
        foreach (var (key, value) in config) values[key] = value;
        return new ImageGenerationSettingsStore(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public async Task Auto_GlifВыключен_ИдёмНаFal()
    {
        var fal = new FakeGenerator("fal", enabled: true);
        var glif = new FakeGenerator("glif", enabled: false);
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()));

        Assert.True(service.EnabledFor(Avatar));
        Assert.Equal("fal", service.ActiveProviderFor(Avatar)?.Key);

        var images = await service.GenerateManyAsync(Avatar, "кот", 1);

        Assert.Single(images);
        Assert.Equal(1, fal.Calls);
        Assert.Equal(0, glif.Calls);
    }

    [Fact]
    public async Task Auto_ПорядокНеЗависитОтРегистрации_GlifПервый()
    {
        var fal = new FakeGenerator("fal", enabled: true);
        var glif = new FakeGenerator("glif", enabled: true);
        // Регистрация «наоборот»: fal раньше glif
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()));

        Assert.Equal("glif", service.ActiveProviderFor(Avatar)?.Key);
        await service.GenerateManyAsync(Avatar, "кот", 1);

        Assert.Equal(1, glif.Calls);
        Assert.Equal(0, fal.Calls);
    }

    [Fact]
    public async Task Auto_ПервыйВернулПусто_ФолбэкНаВторого()
    {
        var glif = new FakeGenerator("glif", enabled: true, returnsImages: false);
        var fal = new FakeGenerator("fal", enabled: true);
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()));

        var images = await service.GenerateManyAsync(Avatar, "кот", 2);

        Assert.Single(images);
        Assert.Equal(1, glif.Calls);
        Assert.Equal(1, fal.Calls);
    }

    [Fact]
    public async Task ЯвныйВыбор_ИдёмТолькоКНему_БезФолбэка()
    {
        var fal = new FakeGenerator("fal", enabled: true);
        var glif = new FakeGenerator("glif", enabled: true, returnsImages: false);
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()));
        Assert.Null(service.UpdateSettings(Avatar, "glif"));

        Assert.Equal("glif", service.ActiveProviderFor(Avatar)?.Key);

        var images = await service.GenerateManyAsync(Avatar, "кот", 1);

        Assert.Empty(images);           // выбранный провайдер вернул пусто — молча на fal не уходим
        Assert.Equal(0, fal.Calls);
        Assert.Equal(1, glif.Calls);
    }

    [Fact]
    public async Task ЯвныйВыбор_ПровайдерВыключен_Пусто()
    {
        var fal = new FakeGenerator("fal", enabled: true);
        var glif = new FakeGenerator("glif", enabled: false);
        // Выключенный провайдер через UpdateSettings не выбирается — кладём в конфиг машины
        var service = new ImageGenerationService([fal, glif],
            Store(NewTempDir(), ("Images:Provider", "glif")));

        Assert.False(service.EnabledFor(Avatar));
        Assert.Empty(await service.GenerateManyAsync(Avatar, "кот", 1));
        Assert.Equal(0, fal.Calls);
    }

    [Fact]
    public async Task ОбаВыключены_ГенерацияНедоступна()
    {
        var fal = new FakeGenerator("fal", enabled: false);
        var glif = new FakeGenerator("glif", enabled: false);
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()));

        Assert.False(service.AnyEnabled);
        Assert.False(service.EnabledFor(Avatar));
        Assert.Null(service.ActiveProviderFor(Avatar));
        Assert.Empty(await service.GenerateManyAsync(Avatar, "кот", 1));
    }

    [Fact]
    public async Task МодельИзКонфига_ОбщаяДляВсехМест()
    {
        var fal = new FakeGenerator("fal", enabled: true);
        var service = new ImageGenerationService([fal],
            Store(NewTempDir(), ("Images:Models:fal", "fal-ai/flux/dev")));

        await service.GenerateManyAsync(Avatar, "кот", 1);
        Assert.Equal("fal-ai/flux/dev", fal.LastModel);
        Assert.Equal("fal-ai/flux/dev", service.ModelFor(Avatar, "fal"));
    }

    [Fact]
    public void UpdateSettings_СохраняетИВалидирует()
    {
        var tempDir = NewTempDir();
        var fal = new FakeGenerator("fal", enabled: true)
        {
            Models = [new ImageModelInfo("fal-ai/flux/dev", "FLUX dev")],
        };
        var glif = new FakeGenerator("glif", enabled: true);
        var service = new ImageGenerationService([fal, glif], Store(tempDir));

        Assert.Null(service.UpdateSettings(Avatar, "glif", new Dictionary<string, string?> { ["fal"] = "fal-ai/flux/dev" }));
        Assert.Equal("glif", service.ModeFor(Avatar));
        Assert.Equal("fal-ai/flux/dev", service.ModelFor(Avatar, "fal"));

        // Неизвестное место, неизвестный провайдер и модель не из курируемого списка — отказ с текстом
        Assert.NotNull(service.UpdateSettings("chat-background", "fal"));
        Assert.NotNull(service.UpdateSettings(Avatar, "midjourney"));
        Assert.NotNull(service.UpdateSettings(Avatar, null, new Dictionary<string, string?> { ["fal"] = "нет-такой" }));

        // Настройка пережила рестарт (перечитали файл новым стором)
        var reloaded = new ImageGenerationService([fal, glif], Store(tempDir));
        Assert.Equal("glif", reloaded.ModeFor(Avatar));
        Assert.Equal("fal-ai/flux/dev", reloaded.ModelFor(Avatar, "fal"));
    }

    [Fact]
    public void UpdateSettings_ВыключенныйПровайдерЯвноНеВыбирается()
    {
        var fal = new FakeGenerator("fal", enabled: true);
        var glif = new FakeGenerator("glif", enabled: false);
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()));

        var error = service.UpdateSettings(Avatar, "glif");

        Assert.NotNull(error);
        Assert.Contains("не настроен", error);
        Assert.Equal("auto", service.ModeFor(Avatar));
    }

    [Fact]
    public void СтарыйФорматСтора_РаскладываетсяНаМесто()
    {
        var tempDir = NewTempDir();
        // Файл формата 1: один выбор на весь инстанс
        File.WriteAllText(Path.Combine(tempDir, "image-generation.json"),
            """{"Version":1,"Provider":"fal","Models":{"fal":"fal-ai/flux/dev"}}""");

        var fal = new FakeGenerator("fal", enabled: true);
        var glif = new FakeGenerator("glif", enabled: true);
        var service = new ImageGenerationService([fal, glif], Store(tempDir));

        foreach (var place in ImagePlaces.All)
        {
            Assert.Equal("fal", service.ModeFor(place));
            Assert.Equal("fal-ai/flux/dev", service.ModelFor(place, "fal"));
        }
    }
}
