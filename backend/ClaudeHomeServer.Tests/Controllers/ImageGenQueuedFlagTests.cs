using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Controllers;

// Провайдер включён, но картинок не отдаёт — ровно то, что видит человек при таймауте glif
// (агент не успел, драйвер вернул пустой список).
internal sealed class SilentImageGenerator : IImageGenerator
{
    // Ключ боевого драйвера: место в режиме auto должно считать генерацию доступной
    public string Key => "fal";
    public string DisplayName => "Молчащий генератор";
    public bool Enabled => true;
    public IReadOnlyList<ImageModelInfo> Models => [];

    public Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GeneratedImage>>([]);
}

// Хост, где единственный драйвер картинок — молчащий: EnabledFor(place) = true,
// а GenerateManyAsync возвращает пусто.
public class SilentImageProviderFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IImageGenerator>();
            services.AddSingleton<IImageGenerator, SilentImageGenerator>();
        });
    }
}

// Признак queued в отказах генерации: он определяет, что скажет человеку фронт —
// «картинка появится сама» (заявка в очереди) или честный отказ.
public class ImageGenQueuedFlagTests : IClassFixture<SilentImageProviderFactory>
{
    private readonly SilentImageProviderFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempProjectDir;

    public ImageGenQueuedFlagTests(SilentImageProviderFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempProjectDir = Path.Combine(factory.TempDir, "projects");
        Directory.CreateDirectory(_tempProjectDir);
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempProjectDir, "queued_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "Молчаливый", rootPath = dir });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreatePersonaAsync()
    {
        var created = await _client.PostAsJsonAsync("/api/personas", new { name = "Молчаливая" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task GenerateIcon_ПровайдерМолчит_502СQueuedИЗаявкойВОчереди()
    {
        var id = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/generate", new { count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("queued").GetBoolean().Should().BeTrue();

        var store = _factory.Services.GetRequiredService<ImageBackfillStore>();
        var request = store.Find(ImageBackfillKinds.ProjectIcon, id);
        request.Should().NotBeNull();
        // Промпт хода сохраняется в заявке — догоняющая генерация рисует то же самое
        request!.Prompt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GenerateAvatar_ПровайдерМолчит_502СQueuedИЗаявкойВОчереди()
    {
        var id = await CreatePersonaAsync();

        var response = await _client.PostAsJsonAsync($"/api/personas/{id}/avatar/generate",
            new { prompt = "рыжая кошка", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("queued").GetBoolean().Should().BeTrue();

        var store = _factory.Services.GetRequiredService<ImageBackfillStore>();
        store.Find(ImageBackfillKinds.PersonaAvatar, id).Should().NotBeNull();
    }

    // Перерисовка УЖЕ стоящей картинки: очередь такую заявку снимает следующим прогоном
    // (у сущности картинка есть), поэтому обещать «появится сама» нельзя.
    [Fact]
    public async Task GenerateIcon_УПроектаУжеЕстьКартинка_502БезQueuedИБезЗаявки()
    {
        var id = await CreateProjectAsync();
        _factory.Services.GetRequiredService<ClaudeHomeServer.Services.ProjectManager>()
            .SetIconImage(id, "icon-old.png");

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/generate", new { count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("queued", out var queued).Should().BeTrue();
        queued.GetBoolean().Should().BeFalse();
        _factory.Services.GetRequiredService<ImageBackfillStore>()
            .Find(ImageBackfillKinds.ProjectIcon, id).Should().BeNull();
    }

    [Fact]
    public async Task GenerateAvatar_УПерсоныУжеЕстьКартинка_502БезQueued()
    {
        var id = await CreatePersonaAsync();
        var personas = _factory.Services.GetRequiredService<ClaudeHomeServer.Services.PersonaManager>();
        var owner = personas.GetByIdInternal(id)!.OwnerId;
        personas.SetAvatarImage(id, owner, "avatar-old.png");

        var response = await _client.PostAsJsonAsync($"/api/personas/{id}/avatar/generate",
            new { prompt = "рыжая кошка", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("queued").GetBoolean().Should().BeFalse();
        _factory.Services.GetRequiredService<ImageBackfillStore>()
            .Find(ImageBackfillKinds.PersonaAvatar, id).Should().BeNull();
    }

    // Проекта ещё нет — заявке не за что зацепиться, «появится сама» было бы враньём
    [Fact]
    public async Task GenerateIconPreview_ПровайдерМолчит_502БезQueued()
    {
        var response = await _client.PostAsJsonAsync("/api/projects/icon/generate-preview",
            new { name = "Черновик", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("queued", out _).Should().BeFalse();
    }

    // Пустой список от драйвера доходит до очереди причиной «no-image» (она транзиентная,
    // повтор будет): роутер на пустом результате отдаёт null, а его и разбирает
    // ImageBackfillService.TryGenerateAsync.
    [Fact]
    public async Task РоутерНаПустомРезультате_ОтдаётNull()
    {
        var images = _factory.Services.GetRequiredService<ImageGenerationService>();

        var image = await images.GenerateAsync(ImagePlaces.ProjectIcon, "иконка");

        image.Should().BeNull();
    }
}

// Ветка «провайдер выключен»: заявка там ставилась и раньше — проверяем, что ответ теперь
// об этом честно сообщает признаком queued.
public class ImageGenOffQueuedFlagTests : IClassFixture<NoImageProvidersFactory>
{
    private readonly NoImageProvidersFactory _factory;
    private readonly HttpClient _client;

    public ImageGenOffQueuedFlagTests(NoImageProvidersFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GenerateIcon_ГенерацияВыключена_400СQueued()
    {
        var dir = Path.Combine(_factory.TempDir, "projects", "off_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "Выключено", rootPath = dir });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/generate", new { count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("queued").GetBoolean().Should().BeTrue();
        _factory.Services.GetRequiredService<ImageBackfillStore>()
            .Find(ImageBackfillKinds.ProjectIcon, id).Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateAvatar_ГенерацияВыключена_400СQueued()
    {
        var created = await _client.PostAsJsonAsync("/api/personas", new { name = "Выключенная" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/personas/{id}/avatar/generate",
            new { prompt = "портрет", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("queued").GetBoolean().Should().BeTrue();
    }

    // Проекта ещё нет — при выключенной генерации отказ остаётся отказом, без queued
    [Fact]
    public async Task GenerateIconPreview_ГенерацияВыключена_400БезQueued()
    {
        var response = await _client.PostAsJsonAsync("/api/projects/icon/generate-preview",
            new { name = "Черновик", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("queued", out _).Should().BeFalse();
    }
}
