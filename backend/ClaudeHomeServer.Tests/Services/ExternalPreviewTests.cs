using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Внешний доступ к дев-серверу по поддомену: токен ссылки, реестр (он же механизм отзыва)
/// и разбор публичного адреса.
/// </summary>
public class ExternalPreviewTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly JwtService _jwt;

    public ExternalPreviewTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "extprev_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = BuildConfig(_dir);
        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _jwt = new JwtService(_config, _users, NullLogger<JwtService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IConfiguration BuildConfig(string dir) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(dir, "projects.json") })
        .Build();

    private ExternalPreviewStore NewStore() => new(_config, NullLogger<ExternalPreviewStore>.Instance);

    private static ExternalPreviewLink Link(string jti, string userId, string projectId = "p1",
        string serviceId = "s1", double hours = 12) =>
        new(jti, userId, projectId, serviceId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(hours));

    // ── Токен ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Токен_ссылки_валидируется_и_несёт_привязку()
    {
        var user = _users.Add("alice", "password-1", "user");
        var token = _jwt.IssuePreviewToken(user.Id, "proj-1", "svc-1", "jti-1", TimeSpan.FromHours(12));

        var claims = _jwt.ValidatePreviewToken(token);

        claims.Should().NotBeNull();
        claims!.Value.UserId.Should().Be(user.Id);
        claims.Value.ProjectId.Should().Be("proj-1");
        claims.Value.ServiceId.Should().Be("svc-1");
        claims.Value.Jti.Should().Be("jti-1");
    }

    /// <summary>
    /// Ключевой рубеж: ссылка открывает ТОЛЬКО дев-сервер. Проходи она как обычный
    /// пользовательский токен, утёкшая ссылка давала бы доступ ко всему API продукта.
    /// </summary>
    [Fact]
    public void Токен_ссылки_не_годится_как_пользовательский()
    {
        var user = _users.Add("bob", "password-1", "user");
        var token = _jwt.IssuePreviewToken(user.Id, "proj-1", "svc-1", "jti-1", TimeSpan.FromHours(12));

        _jwt.ValidateUserToken(token).Should().BeNull("аудитория у ссылки своя");
    }

    /// <summary>И обратно: обычный токен не открывает поддомен.</summary>
    [Fact]
    public void Обычный_токен_не_годится_как_ссылка()
    {
        var user = _users.Add("carol", "password-1", "user");
        var (token, _) = _jwt.Issue(user);

        _jwt.ValidatePreviewToken(token).Should().BeNull();
    }

    /// <summary>
    /// Сброс пароля обязан закрывать и внешний доступ: он выкидывает пользователя со всех
    /// устройств, и оставшаяся живой ссылка наружу сделала бы этот жест бессмысленным.
    /// </summary>
    [Fact]
    public void Сброс_пароля_убивает_ссылку()
    {
        var user = _users.Add("dave", "password-1", "user");
        var token = _jwt.IssuePreviewToken(user.Id, "proj-1", "svc-1", "jti-1", TimeSpan.FromHours(12));
        _jwt.ValidatePreviewToken(token).Should().NotBeNull();

        // Бампает TokenVersion — тот самый механизм, на который опирается IsSessionCurrent
        _users.ResetPassword(user.Id, "password-2").Should().BeTrue();

        _jwt.ValidatePreviewToken(token).Should().BeNull();
    }

    [Fact]
    public void Протухшая_ссылка_не_валидируется()
    {
        var user = _users.Add("erin", "password-1", "user");
        var token = _jwt.IssuePreviewToken(user.Id, "proj-1", "svc-1", "jti-1", TimeSpan.FromSeconds(-1));

        _jwt.ValidatePreviewToken(token).Should().BeNull();
    }

    // ── Реестр (он же отзыв) ─────────────────────────────────────────────────────

    [Fact]
    public void Отзыв_гасит_ссылку_немедленно()
    {
        var store = NewStore();
        store.Add(Link("j1", "u1"));
        store.Get("j1").Should().NotBeNull();

        store.Revoke("j1", "u1").Should().BeTrue();

        store.Get("j1").Should().BeNull("живость определяет реестр, а не подпись");
    }

    [Fact]
    public void Чужую_ссылку_отозвать_нельзя()
    {
        var store = NewStore();
        store.Add(Link("j1", "u1"));

        store.Revoke("j1", "другой-владелец").Should().BeFalse();
        store.Get("j1").Should().NotBeNull();
    }

    [Fact]
    public void Протухшая_запись_не_отдаётся()
    {
        var store = NewStore();
        store.Add(Link("j1", "u1", hours: -1));

        store.Get("j1").Should().BeNull("срок — это тоже отзыв");
    }

    /// <summary>
    /// Потолок — предохранитель от забытых открытыми витрин. Вытеснение обязано быть ВИДИМЫМ:
    /// молча умершая ссылка выглядит как поломка продукта.
    /// </summary>
    [Fact]
    public void Потолок_вытесняет_старейшую_и_сообщает_об_этом()
    {
        var store = NewStore();
        for (var i = 0; i < ExternalPreviewStore.MaxPerOwner; i++)
        {
            // IssuedAt строго возрастает — иначе «самая старая» не определена
            store.Add(new ExternalPreviewLink($"j{i}", "u1", "p1", $"s{i}",
                DateTimeOffset.UtcNow.AddMinutes(i), DateTimeOffset.UtcNow.AddHours(12)));
        }

        var evicted = store.Add(new ExternalPreviewLink("j-new", "u1", "p1", "s-new",
            DateTimeOffset.UtcNow.AddMinutes(100), DateTimeOffset.UtcNow.AddHours(12)));

        evicted.Should().ContainSingle().Which.Jti.Should().Be("j0");
        store.Get("j0").Should().BeNull();
        store.Get("j-new").Should().NotBeNull();
        store.ListFor("u1").Should().HaveCount(ExternalPreviewStore.MaxPerOwner);
    }

    [Fact]
    public void Потолок_считается_на_владельца_а_не_на_всех()
    {
        var store = NewStore();
        for (var i = 0; i < ExternalPreviewStore.MaxPerOwner; i++)
            store.Add(Link($"a{i}", "u1"));

        var evicted = store.Add(Link("b1", "u2"));

        evicted.Should().BeEmpty("у второго владельца свой счёт");
        store.ListFor("u1").Should().HaveCount(ExternalPreviewStore.MaxPerOwner);
        store.ListFor("u2").Should().ContainSingle();
    }

    [Fact]
    public void Список_владельца_сквозной_по_проектам()
    {
        var store = NewStore();
        store.Add(Link("j1", "u1", projectId: "proj-A"));
        store.Add(Link("j2", "u1", projectId: "proj-B"));
        store.Add(Link("j3", "u2", projectId: "proj-C"));

        store.ListFor("u1").Select(l => l.ProjectId).Should().BeEquivalentTo(new[] { "proj-A", "proj-B" });
    }

    [Fact]
    public void Отзыв_всех_закрывает_только_свои()
    {
        var store = NewStore();
        store.Add(Link("j1", "u1"));
        store.Add(Link("j2", "u1"));
        store.Add(Link("j3", "u2"));

        store.RevokeAll("u1").Should().Be(2);

        store.ListFor("u1").Should().BeEmpty();
        store.ListFor("u2").Should().ContainSingle();
    }

    /// <summary>
    /// Реестр обязан пережить рестарт: выкатка на бой гасит продукт, и без файла каждая
    /// публикация молча убивала бы все открытые ссылки.
    /// </summary>
    [Fact]
    public void Реестр_переживает_перезапуск()
    {
        NewStore().Add(Link("j1", "u1"));

        var restarted = NewStore();

        restarted.Get("j1").Should().NotBeNull();
    }

    // ── Разбор публичного адреса ─────────────────────────────────────────────────

    [Theory]
    [InlineData("https://svc.example.me:8080", "svc.example.me")]
    [InlineData("https://svc.example.me", "svc.example.me")]
    [InlineData("https://svc.example.me:8080/", "svc.example.me")]
    public void Хост_выводится_из_публичного_адреса(string url, string host)
    {
        var opt = new ExternalPreviewOptions { PublicBaseUrl = url };

        opt.Host.Should().Be(host);
        opt.IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("svc.example.me:8080")]
    [InlineData("не адрес вовсе")]
    public void Кривой_публичный_адрес_считается_ненастроенным(string url)
    {
        var opt = new ExternalPreviewOptions { PublicBaseUrl = url };

        opt.IsConfigured.Should().BeFalse("лучше честный отказ, чем ссылка в никуда");
    }
}
