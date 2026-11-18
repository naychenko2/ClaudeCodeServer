using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// К2c (разрез «Знания репетитора» §2): звено каскада удаления пользователя —
/// DELETE /api/users/{id} сносит каталог материалов data/child-subjects/{id} после звена
/// «Каскада знаний» (UsersController.Delete). Best-effort: сбой звена — только лог, ответ
/// не меняется (тот же приём, что у соседних звеньев).
///
/// Границы звена (критерии К2c):
/// 1. снос — только каталог удаляемого ребёнка; каталог другого ребёнка на месте;
/// 2. удаление ПРЕДМЕТА (PUT child-profile без него) каталог материалов НЕ трогает:
///    файлы вернутся вместе с предметом, если его вернуть;
/// 3. сбой стора в звене — ответ DELETE тот же (204), что без сбоя.
///
/// Материалы засевает сам тест через боевой стор хоста (тот же синглтон, что у ручки К2b):
/// предмет проверки — каскад удаления, а не ручка загрузки.
///
/// «Стор бросает» — подмена регистрации ChildSubjectMaterialsStore на экземпляр с null-root:
/// SafePath.Join(null) бросает ArgumentNullException на построении пути (до try звена) —
/// модель отказа звена вне его catch. Свой хост с подменой — отдельный класс ниже.
///
/// Мутация (критерий c3): убрать звено childMaterials.DeleteChildAsync из UsersController.Delete —
/// краснеет УдалениеРебёнка_СнитаетКаталогМатериалов_ЧужогоНеТрогает (каталог остаётся).
/// </summary>
[Trait("Category", "Host")]
public class UsersDeleteChildMaterialsTests : IClassFixture<UsersDeleteChildMaterialsTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        // Запись пула с ApiKey для PUT child-profile (тот же приём, что у UsersChildProfileTests):
        // без неё валидный отказ provider_not_api_key срабатывал бы на валидном теле.
        public Hosts() => ExtraConfig["ClaudeSubscriptions:kids-api:ApiKey"] = "sk-ant-test-kids";
    }

    private readonly Hosts _hosts;
    private readonly UserStore _users;
    private readonly HttpClient _admin;

    public UsersDeleteChildMaterialsTests(Hosts hosts)
    {
        _hosts = hosts;
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _admin = _hosts.CreateAuthenticatedClient();
    }

    private ChildSubjectMaterialsStore Store => _hosts.Services.GetRequiredService<ChildSubjectMaterialsStore>();

    private string AdminId => _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    // Ребёнок — новый пользователь с записанным в живой профиль ChildMode (тот же приём,
    // что у ChildSubjectMaterialsControllerTests.SetupChild). Имена уникальны на тест:
    // стор пользователей общий на фикстуру.
    private string SetupChild(string username, params string[] subjectIds)
    {
        // Среда container: детский режим включается только container-пользователям
        // (ChildProfileValidator.cs:34), как в UsersChildProfileTests.NewContainerUser.
        var resp = _admin.PostAsJsonAsync("/api/users", new
        {
            username,
            password = "password12345",
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        }).GetAwaiter().GetResult();
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(resp.Content.ReadAsStringAsync()
            .GetAwaiter().GetResult()).GetProperty("id").GetString()!;

        _users.GetById(id)!.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
            Subjects = [.. subjectIds.Select(s => new ChildSubject
            {
                Id = s, Label = "Предмет " + s, Glyph = "Аа", Color = "info",
                ProviderKey = "key", Model = "sonnet",
            })],
        };
        return id;
    }

    // Каталог ребёнка в боевом сторе хоста: root — директория DataPath (TempDir фабрики) +
    // child-subjects (тот же путь, что у ChildSubjectMaterialsControllerTests.SubjectDir).
    private string ChildDir(string childId) =>
        Path.Combine(_hosts.TempDir, ChildSubjectMaterialsStore.DirName, childId);

    // ── c1: снос каскадом ────────────────────────────────────────────────────────────

    [Fact]
    public async Task УдалениеРебёнка_СнитаетКаталогМатериалов_ЧужогоНеТрогает()
    {
        var childA = SetupChild("kidmat-a", "math");
        var childB = SetupChild("kidmat-b", "math");
        await Store.AddAsync(childA, "math", "урок.txt", "текст урока"u8.ToArray(), AdminId);
        await Store.AddAsync(childB, "math", "урок.txt", "текст урока"u8.ToArray(), AdminId);

        Directory.Exists(ChildDir(childA)).Should().BeTrue();
        Directory.Exists(ChildDir(childB)).Should().BeTrue();

        var delete = await _admin.DeleteAsync($"/api/users/{childA}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Directory.Exists(ChildDir(childA)).Should().BeFalse();
        Directory.Exists(ChildDir(childB)).Should().BeTrue();
    }

    // ── c2: удаление предмета — каталог материалов нетронут ─────────────────────────

    private static object SubjectBody(params string[] subjectIds) => new
    {
        enabled = true,
        guardianUserIds = Array.Empty<string>(),
        providerKey = "kids-api",
        model = "sonnet",
        spaces = new Dictionary<string, object>(),
        subjects = subjectIds.Select(s => (object)new
        {
            id = s,
            label = "Предмет " + s,
            glyph = "Аа",
            color = "info",
            providerKey = "kids-api",
            model = "sonnet",
        }).ToArray(),
    };

    [Fact]
    public async Task УдалениеПредмета_ФайлыМатериаловНаМесте()
    {
        var childId = SetupChild("kidmat-subj");

        // Предметы через боевой PUT (Т2a): math + geo.
        var put1 = await _admin.PutAsJsonAsync($"/api/users/{childId}/child-profile", SubjectBody("math", "geo"));
        put1.StatusCode.Should().Be(HttpStatusCode.OK);

        await Store.AddAsync(childId, "math", "урок.txt", "текст урока"u8.ToArray(), AdminId);
        await Store.AddAsync(childId, "geo", "справка.txt", "справка"u8.ToArray(), AdminId);

        var filesBefore = Directory.GetFiles(Path.Combine(ChildDir(childId), "math"), "*", SearchOption.AllDirectories);
        filesBefore.Should().HaveCount(3); // опись + оригинал + текст

        // PUT без math — живое слияние снимает предмет; каталог материалов он не трогает:
        // файлы вернутся вместе с предметом, если его вернуть (разрез §2).
        var put2 = await _admin.PutAsJsonAsync($"/api/users/{childId}/child-profile", SubjectBody("geo"));
        put2.StatusCode.Should().Be(HttpStatusCode.OK);
        _users.GetById(childId)!.ChildMode!.Subjects.Should().NotContain(s => s.Id == "math");

        Directory.GetFiles(Path.Combine(ChildDir(childId), "math"), "*", SearchOption.AllDirectories)
            .Should().BeEquivalentTo(filesBefore);
        Directory.Exists(Path.Combine(ChildDir(childId), "geo")).Should().BeTrue();
    }
}

/// <summary>
/// К2c, граница 3: сбой звена каскада не меняет ответ Delete. Хост с подменённым
/// ChildSubjectMaterialsStore: экземпляр с null-root — DeleteChildAsync бросает
/// ArgumentNullException на SafePath.Join (до try звена в сторе), т.е. наружу стора —
/// ровно то, что обязан ловить catch звена в контроллере. Ответ DELETE — тот же 204,
/// что в нормальном хосте (см. УдалениеРебёнка_СнитаетКаталогМатериалов выше).
/// </summary>
[Trait("Category", "Host")]
public class UsersDeleteChildMaterialsBrokenStoreTests : IClassFixture<UsersDeleteChildMaterialsBrokenStoreTests.BrokenHosts>
{
    public sealed class BrokenHosts : TestWebApplicationFactory
    {
        public BrokenHosts() => ExtraServices = services =>
        {
            // Подмена боевой регистрации (Program.cs:626): последняя регистрация побеждает.
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Markitdown:Command"] = "ccs-no-such-converter-9f3a",
            }).Build();
            services.AddSingleton(new ChildSubjectMaterialsStore(
                null!,
                new ClaudeHomeServer.Services.Docs.MarkitdownService(cfg, NullLogger<ClaudeHomeServer.Services.Docs.MarkitdownService>.Instance),
                cfg,
                // Ребёнок «жив»: тест про звено каскада и его catch, а не про проверку стора.
                _ => true,
                // Картинок в этом тесте не грузится — OCR не вызывается вовсе.
                new UnusedOcr(),
                raster: null,
                NullLogger<ChildSubjectMaterialsStore>.Instance));
        };
    }

    private sealed class UnusedOcr : ILocalPageOcr
    {
        public Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct) =>
            throw new InvalidOperationException("OCR в этом тесте не вызывается");
    }

    private readonly BrokenHosts _hosts;
    private readonly UserStore _users;
    private readonly HttpClient _admin;

    public UsersDeleteChildMaterialsBrokenStoreTests(BrokenHosts hosts)
    {
        _hosts = hosts;
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _admin = _hosts.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task СторБросает_ОтветDeleteНеМеняется()
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = "kidmat-broken",
            password = "password12345",
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var childId = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        _users.GetById(childId)!.ChildMode = new ChildProfile { Enabled = true };

        // Звено каскада бросает (стор с null-root) — ловится catch контроллера, лог,
        // ответ не меняется: 204, как в нормальном хосте.
        var delete = await _admin.DeleteAsync($"/api/users/{childId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _users.GetById(childId).Should().BeNull();
    }
}
