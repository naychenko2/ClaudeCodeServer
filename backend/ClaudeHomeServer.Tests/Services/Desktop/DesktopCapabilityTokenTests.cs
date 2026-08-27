using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Capability-токен канала устройств (ADR-008, «Авторизация канала»): выдача, строгая
/// проверка по audience и схема аутентификации, которая выводит чат-вызывателя ИЗ токена.
/// </summary>
public class DesktopCapabilityTokenTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly JwtService _sut;

    public DesktopCapabilityTokenTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "desktop_cap_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _users = new UserStore(BuildConfig(_tempDir), new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _sut = new JwtService(BuildConfig(_tempDir), _users, NullLogger<JwtService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static IConfiguration BuildConfig(string dir) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Конструктор JwtService берёт каталог от DataPath и кладёт рядом jwt-secret.txt
            ["DataPath"] = Path.Combine(dir, "projects.json")
        })
        .Build();

    // Токен, подписанный НАСТОЯЩИМ секретом сервиса: так проверяются подделки,
    // отличающиеся только audience/сроком/составом claims
    private string SignWithRealSecret(string audience, IEnumerable<Claim> claims, DateTime? expires = null)
    {
        var secret = File.ReadAllText(Path.Combine(_tempDir, "jwt-secret.txt")).Trim();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: audience,
            claims: claims,
            expires: expires ?? DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    }

    // --- Выдача ---

    [Fact]
    public void IssueDesktopToken_HasDesktopAudience_AndCallerClaims()
    {
        var token = _sut.IssueDesktopToken("owner-1", "chat-1", "dev-1");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Issuer.Should().Be("ClaudeHomeServer");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be(JwtService.DesktopAudience);
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "owner-1");
        jwt.Claims.Should().Contain(c => c.Type == DesktopCaller.SessionClaim && c.Value == "chat-1");
        jwt.Claims.Should().Contain(c => c.Type == DesktopCaller.DeviceClaim && c.Value == "dev-1");
    }

    [Fact]
    public void IssueDesktopToken_Lifetime_IsMinutes()
    {
        var token = _sut.IssueDesktopToken("owner-1", "chat-1");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        JwtService.DesktopTokenLifetime.Should().BeLessThan(TimeSpan.FromHours(1));
        jwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.Add(JwtService.DesktopTokenLifetime), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void IssueDesktopToken_TwiceInSameTurn_ProducesUsableToken_WithoutDeviceClaim()
    {
        // Устройство на момент запуска CLI может быть ещё неизвестно (сеанс рук стартует
        // с самого устройства) — claim did тогда просто отсутствует
        var caller = _sut.ValidateDesktopToken(_sut.IssueDesktopToken("owner-1", "chat-1"));

        caller.Should().NotBeNull();
        caller!.DeviceId.Should().BeNull();
        caller.OwnerId.Should().Be("owner-1");
        caller.SessionId.Should().Be("chat-1");
    }

    // --- Проверка строго по audience ---

    [Fact]
    public void ValidateDesktopToken_OwnToken_ReturnsCaller()
    {
        var caller = _sut.ValidateDesktopToken(_sut.IssueDesktopToken("owner-1", "chat-1", "dev-1"));

        caller.Should().Be(new DesktopCaller("owner-1", "chat-1", "dev-1"));
    }

    [Fact]
    public void ValidateDesktopToken_UserToken_Rejected()
    {
        // Обычный пользовательский JWT (aud ClaudeHomeServer) грань десктопа не открывает
        var user = _users.Add("alice", "password-1", "user");

        _sut.ValidateDesktopToken(_sut.Issue(user).token).Should().BeNull();
    }

    [Fact]
    public void ValidateDesktopToken_ServiceToken_Rejected()
    {
        // Сервисный JWT владельца (typ=svc) лежит в env КАЖДОГО хода, включая ночной
        // tasks-executor: принять его здесь — значит отдать руки любому чату владельца
        _sut.ValidateDesktopToken(_sut.IssueServiceToken("owner-1")).Should().BeNull();
    }

    [Fact]
    public void ValidateDesktopToken_OfficeToken_Rejected()
    {
        _sut.ValidateDesktopToken(_sut.IssueOfficeToken("owner-1", "proj-A", "a.docx")).Should().BeNull();
    }

    [Fact]
    public void DesktopToken_IsNotAcceptedAsUserOrOfficeToken()
    {
        // Обратная сторона: capability-токен не годится на общем периметре
        var desktop = _sut.IssueDesktopToken("owner-1", "chat-1", "dev-1");

        _sut.ValidateUserToken(desktop).Should().BeNull();
        _sut.ValidateOfficeToken(desktop, "proj-A", "a.docx").Should().BeNull();
        var act = () => new JwtSecurityTokenHandler { MapInboundClaims = false }
            .ValidateToken(desktop, _sut.ValidationParameters, out _);
        act.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ValidateDesktopToken_ServiceTokenRelabeledToDesktopAudience_ButForeignKey_Rejected()
    {
        var foreign = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(48));
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: JwtService.DesktopAudience,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "intruder"),
                     new Claim(DesktopCaller.SessionClaim, "chat-1")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(foreign, SecurityAlgorithms.HmacSha256)));

        _sut.ValidateDesktopToken(forged).Should().BeNull();
    }

    [Fact]
    public void ValidateDesktopToken_Expired_Rejected()
    {
        var expired = SignWithRealSecret(JwtService.DesktopAudience,
            [new Claim(JwtRegisteredClaimNames.Sub, "owner-1"), new Claim(DesktopCaller.SessionClaim, "chat-1")],
            DateTime.UtcNow.AddMinutes(-1));

        _sut.ValidateDesktopToken(expired).Should().BeNull();
    }

    [Fact]
    public void ValidateDesktopToken_WithoutSessionClaim_Rejected()
    {
        // Без чата токен бессмыслен: сверять с чатом активного сеанса рук нечего
        var noSession = SignWithRealSecret(JwtService.DesktopAudience,
            [new Claim(JwtRegisteredClaimNames.Sub, "owner-1")]);

        _sut.ValidateDesktopToken(noSession).Should().BeNull();
    }

    [Fact]
    public void ValidateDesktopToken_GarbageAndNull_Rejected()
    {
        _sut.ValidateDesktopToken("not-a-token").Should().BeNull();
        _sut.ValidateDesktopToken(null).Should().BeNull();
        _sut.ValidateDesktopToken("   ").Should().BeNull();
    }

    // --- Схема аутентификации ---

    private async Task<(AuthenticateResult result, HttpContext ctx)> AuthenticateAsync(
        Action<HttpContext> arrange)
    {
        var ctx = new DefaultHttpContext();
        arrange(ctx);
        var handler = new DesktopCapabilityAuthHandler(
            new SchemeOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default, _sut);
        await handler.InitializeAsync(
            new AuthenticationScheme(DesktopCapabilityAuthHandler.SchemeName, null, typeof(DesktopCapabilityAuthHandler)),
            ctx);
        return (await handler.AuthenticateAsync(), ctx);
    }

    [Fact]
    public async Task Handler_ValidToken_AuthenticatesCallerFromToken()
    {
        var token = _sut.IssueDesktopToken("owner-1", "chat-1", "dev-1");

        var (result, _) = await AuthenticateAsync(ctx => ctx.Request.Headers.Authorization = "Bearer " + token);

        result.Succeeded.Should().BeTrue();
        DesktopCaller.FromPrincipal(result.Principal).Should().Be(new DesktopCaller("owner-1", "chat-1", "dev-1"));
    }

    [Fact]
    public async Task Handler_SpoofedCallerHeader_ChangesNothing()
    {
        // X-Caller-Session-Id подделывается ходом тривиально — в решении об авторизации
        // он не участвует вообще: чат берётся из токена
        var token = _sut.IssueDesktopToken("owner-1", "chat-1", "dev-1");

        var (result, _) = await AuthenticateAsync(ctx =>
        {
            ctx.Request.Headers.Authorization = "Bearer " + token;
            ctx.Request.Headers["X-Caller-Session-Id"] = "chat-СОВСЕМ-другой";
        });

        result.Succeeded.Should().BeTrue();
        DesktopCaller.FromPrincipal(result.Principal)!.SessionId.Should().Be("chat-1");
    }

    [Fact]
    public async Task Handler_CallerHeaderWithoutToken_DoesNotAuthenticate()
    {
        var (result, _) = await AuthenticateAsync(ctx =>
            ctx.Request.Headers["X-Caller-Session-Id"] = "chat-1");

        result.Succeeded.Should().BeFalse();
        result.Principal.Should().BeNull();
    }

    [Fact]
    public async Task Handler_ServiceToken_Fails()
    {
        var (result, _) = await AuthenticateAsync(ctx =>
            ctx.Request.Headers.Authorization = "Bearer " + _sut.IssueServiceToken("owner-1"));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task Handler_UserToken_Fails()
    {
        var user = _users.Add("alice", "password-1", "user");

        var (result, _) = await AuthenticateAsync(ctx =>
            ctx.Request.Headers.Authorization = "Bearer " + _sut.Issue(user).token);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Handler_NoAuthorizationHeader_NoResult()
    {
        var (result, _) = await AuthenticateAsync(_ => { });

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task Handler_Challenge_Returns401WithBearer()
    {
        var ctx = new DefaultHttpContext();
        var handler = new DesktopCapabilityAuthHandler(
            new SchemeOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default, _sut);
        await handler.InitializeAsync(
            new AuthenticationScheme(DesktopCapabilityAuthHandler.SchemeName, null, typeof(DesktopCapabilityAuthHandler)),
            ctx);

        await handler.ChallengeAsync(null);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().Contain("Bearer");
    }

    /// <summary>Опции схемы без DI — хендлеру достаточно значений по умолчанию.</summary>
    private sealed class SchemeOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        private readonly AuthenticationSchemeOptions _options = new();
        public AuthenticationSchemeOptions CurrentValue => _options;
        public AuthenticationSchemeOptions Get(string? name) => _options;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
