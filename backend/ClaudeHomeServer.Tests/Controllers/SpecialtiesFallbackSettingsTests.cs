using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Бюджет подмен цепочки хода (MaxSubstitutions) отдаётся фронту через
// GET /api/specialties/settings — поле maxSubstitutions, эффективное значение по
// цепочке per-owner → global → дефолт (FallbackSettingsStore.ResolveMaxSubstitutions).
// Фронт приглушает шаги пресета за этим пределом; без поля UI хардкодил дефолт.
//
// Отдельный класс (не SpecialtiesControllerTests): FallbackSettingsStore — singleton на
// хост, а здесь мы мутируем его global/owner прямо через DI. Свой TestWebApplicationFactory
// даёт чистый стор без интерференции с тестами специальностей.
public class SpecialtiesFallbackSettingsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _user;
    private readonly FallbackSettingsStore _fallback;
    private readonly string _userId;

    public SpecialtiesFallbackSettingsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        // seconduser (роль user) — чтобы параллельно видеть и global, и свой owner-слой
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        _fallback = factory.Services.GetRequiredService<FallbackSettingsStore>();
        var users = factory.Services.GetRequiredService<UserStore>();
        _userId = users.GetAll().First(u => u.Username == TestWebApplicationFactory.SecondUsername).Id;
    }

    private async Task<JsonElement> GetSettingsAsync()
    {
        var response = await _user.GetAsync("/api/specialties/settings");
        response.IsSuccessStatusCode.Should().BeTrue();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> GetMaxSubstitutionsAsync() =>
        (await GetSettingsAsync()).GetProperty("maxSubstitutions").GetInt32();

    // Стор — singleton на хост: между тестами состояние сохраняется. Каждый тест
    // начинает с чистого global=null + owner=null, чтобы порядок выполнения не влиял.
    private void ResetStore()
    {
        _fallback.SetGlobal(null);
        _fallback.SetOwner(_userId, null);
    }

    // Цепочка owner → global → дефолт одним проходом: детерминированно и без
    // перекрёстного влияния шагов друг на друга.
    [Fact]
    public async Task MaxSubstitutions_ЭффективноеЗначениеПоЦепочкеВладелецГлобалДефолт()
    {
        ResetStore();

        // 1) Настроек нет — дефолт кода
        (await GetMaxSubstitutionsAsync()).Should().Be(FallbackSettingsStore.DefaultMaxSubstitutions);

        // 2) Глобальный слой — общий для всех владельцев
        _fallback.SetGlobal(2);
        (await GetMaxSubstitutionsAsync()).Should().Be(2, "global задаёт потолок, пока владелец его не перебил");

        // 3) Личный слой владельца бьёт глобальный
        _fallback.SetOwner(_userId, 5);
        (await GetMaxSubstitutionsAsync()).Should().Be(5, "per-owner override сильнее global");

        // 4) Снятие личного слоя возвращает к глобальному
        _fallback.SetOwner(_userId, null);
        (await GetMaxSubstitutionsAsync()).Should().Be(2, "без личного слоя снова виден global");

        // 5) Снятие глобального слоя возвращает к дефолту
        _fallback.SetGlobal(null);
        (await GetMaxSubstitutionsAsync()).Should().Be(FallbackSettingsStore.DefaultMaxSubstitutions);

        ResetStore();
    }

    // Контракт — только добавление: существующие поля ответа остаются на месте,
    // новое поле присутствует и в допустимом диапазоне.
    [Fact]
    public async Task MaxSubstitutions_ПолеДобавленоБезРазрываКонтракта()
    {
        ResetStore();
        var settings = await GetSettingsAsync();

        // Существующие поля на месте
        settings.GetProperty("version").GetInt32().Should().Be(SpecialtySettingsStore.FormatVersion);
        settings.GetProperty("global").ValueKind.Should().Be(JsonValueKind.Object);
        settings.GetProperty("owner").ValueKind.Should().Be(JsonValueKind.Object);
        settings.GetProperty("user").ValueKind.Should().Be(JsonValueKind.Object);
        settings.GetProperty("presets").ValueKind.Should().Be(JsonValueKind.Array);

        // Новое поле присутствует и клампится в жёсткий диапазон 1..HardMaxSubstitutions
        var max = settings.GetProperty("maxSubstitutions").GetInt32();
        max.Should().BeInRange(1, FallbackSettingsStore.HardMaxSubstitutions);

        ResetStore();
    }

    // --- Запись бюджета подмен (PUT settings/fallback/*) ---

    // Запись глобального и личного потолка через API: сохраняется, отдаётся в ответе
    // и читается эффективным значением; снятие возвращает наследование.
    [Fact]
    public async Task MaxSubstitutions_ЗаписьГлобальныйИЛичный_ЧерезApi()
    {
        ResetStore();

        // Глобальный: пишет только админ (проверим и это)
        var forbidden = await _user.PutAsJsonAsync("/api/specialties/settings/fallback/global",
            new { maxSubstitutions = 3 });
        forbidden.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden,
            "глобальный бюджет подмен — только admin");

        using var admin = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var putGlobal = await admin.PutAsJsonAsync("/api/specialties/settings/fallback/global",
            new { maxSubstitutions = 3 });
        putGlobal.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await putGlobal.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("maxSubstitutions").GetInt32().Should().Be(3);
        (await GetMaxSubstitutionsAsync()).Should().Be(3, "global применился к пользователю");

        // Личный: пользователь пишет свой сам
        var putOwner = await _user.PutAsJsonAsync("/api/specialties/settings/fallback/owner",
            new { maxSubstitutions = 5 });
        putOwner.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await GetMaxSubstitutionsAsync()).Should().Be(5, "личный потолок сильнее глобального");

        // Снятие личного (null) возвращает глобальный
        await _user.PutAsJsonAsync("/api/specialties/settings/fallback/owner",
            new { maxSubstitutions = (int?)null });
        (await GetMaxSubstitutionsAsync()).Should().Be(3);

        // Снятие глобального возвращает дефолт
        await admin.PutAsJsonAsync("/api/specialties/settings/fallback/global",
            new { maxSubstitutions = (int?)null });
        (await GetMaxSubstitutionsAsync()).Should().Be(FallbackSettingsStore.DefaultMaxSubstitutions);

        ResetStore();
    }

    // Дефолт остаётся 4 — поле нет ни в global, ни в owner (контракт задачи).
    [Fact]
    public async Task MaxSubstitutions_ДефолтБезНастроек_РавенЧетырём()
    {
        ResetStore();
        (await GetMaxSubstitutionsAsync()).Should().Be(4);
        ResetStore();
    }

    // Вне диапазона 1..HardMaxSubstitutions — 400 (кламп на записи, не тихий).
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task MaxSubstitutions_ВнеДиапазона_400(int value)
    {
        ResetStore();
        var put = await _user.PutAsJsonAsync("/api/specialties/settings/fallback/owner",
            new { maxSubstitutions = value });
        put.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        // Слой не записался — дефолт
        (await GetMaxSubstitutionsAsync()).Should().Be(FallbackSettingsStore.DefaultMaxSubstitutions);
        ResetStore();
    }
}
