using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace ClaudeHomeServer.Tests.Services;

public class JwtServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly JwtService _sut;

    public JwtServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jwt_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = BuildConfig(_tempDir);
        _sut = new JwtService(_config, NullLogger<JwtService>.Instance);
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

    // MapInboundClaims = false — как в Program.cs (DefaultMapInboundClaims выключен),
    // чтобы sub не переименовывался в ClaimTypes.NameIdentifier
    private static ClaimsPrincipal Validate(string token, TokenValidationParameters parameters, out SecurityToken validated) =>
        new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, parameters, out validated);

    // --- Issue ---

    [Fact]
    public void Issue_Token_PassesOwnValidationParameters()
    {
        var user = new User { Username = "alice", Role = "admin" };
        var (token, _) = _sut.Issue(user);

        var act = () => Validate(token, _sut.ValidationParameters, out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void Issue_Token_ContainsSubNameAndRole()
    {
        var user = new User { Username = "alice", Role = "admin" };
        var (token, _) = _sut.Issue(user);

        var principal = Validate(token, _sut.ValidationParameters, out _);

        principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value.Should().Be(user.Id);
        principal.Identity!.Name.Should().Be("alice");
        principal.IsInRole("admin").Should().BeTrue();
    }

    [Fact]
    public void Issue_Token_HasIssuerAndAudienceClaudeHomeServer()
    {
        var (token, _) = _sut.Issue(new User { Username = "bob" });

        Validate(token, _sut.ValidationParameters, out var validated);

        var jwt = (JwtSecurityToken)validated;
        jwt.Issuer.Should().Be("ClaudeHomeServer");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("ClaudeHomeServer");
    }

    [Fact]
    public void Issue_ExpiresAt_IsInFutureAboutThirtyDays()
    {
        var (_, expiresAt) = _sut.Issue(new User { Username = "bob" });

        expiresAt.Should().BeAfter(DateTime.UtcNow);
        expiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));
    }

    // --- IssueServiceToken ---

    [Fact]
    public void IssueServiceToken_ContainsSubOfUser()
    {
        var token = _sut.IssueServiceToken("user-42");

        var principal = Validate(token, _sut.ValidationParameters, out _);

        principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value.Should().Be("user-42");
    }

    [Fact]
    public void IssueServiceToken_ExpiresAboutServiceTokenLifetime()
    {
        var token = _sut.IssueServiceToken("user-42");

        Validate(token, _sut.ValidationParameters, out var validated);

        validated.ValidTo.Should().BeCloseTo(
            DateTime.UtcNow.Add(JwtService.ServiceTokenLifetime),
            TimeSpan.FromMinutes(1));
    }

    // --- Подделка ---

    [Fact]
    public void Validate_TokenSignedWithForeignKey_Fails()
    {
        // Токен с теми же issuer/audience/claims, но подписанный чужим ключом
        var foreignKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(48));
        var creds = new SigningCredentials(foreignKey, SecurityAlgorithms.HmacSha256);
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: "ClaudeHomeServer",
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "intruder")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds));

        var act = () => Validate(forged, _sut.ValidationParameters, out _);

        act.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void Validate_ExpiredToken_Fails()
    {
        // Токен подписан НАСТОЯЩИМ секретом сервиса (читаем jwt-secret.txt),
        // но срок действия истёк — валидация обязана отклонить (ClockSkew = Zero)
        var secret = File.ReadAllText(Path.Combine(_tempDir, "jwt-secret.txt")).Trim();
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expired = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: "ClaudeHomeServer",
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "user-1")],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: creds));

        var act = () => Validate(expired, _sut.ValidationParameters, out _);

        act.Should().Throw<SecurityTokenExpiredException>();
    }

    [Fact]
    public void Validate_AlgNoneToken_Fails()
    {
        // Неподписанный токен (alg=none) с валидными claims — классическая атака на JWT
        var unsigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: "ClaudeHomeServer",
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "intruder")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: null));

        var act = () => Validate(unsigned, _sut.ValidationParameters, out _);

        act.Should().Throw<SecurityTokenException>();
    }

    // --- Office-токены (cross-owner изоляция OnlyOffice) ---

    [Fact]
    public void OfficeToken_ValidatedWithMatchingProjectAndPath_ReturnsOwner()
    {
        var token = _sut.IssueOfficeToken("owner-1", "proj-A", "docs/a.docx");

        _sut.ValidateOfficeToken(token, "proj-A", "docs/a.docx").Should().Be("owner-1");
    }

    [Fact]
    public void OfficeToken_ForDifferentProject_Rejected()
    {
        // Токен выписан на proj-A — попытка скачать файл proj-B тем же токеном должна провалиться
        var token = _sut.IssueOfficeToken("owner-1", "proj-A", "docs/a.docx");

        _sut.ValidateOfficeToken(token, "proj-B", "docs/a.docx").Should().BeNull();
    }

    [Fact]
    public void OfficeToken_ForDifferentPath_Rejected()
    {
        var token = _sut.IssueOfficeToken("owner-1", "proj-A", "docs/a.docx");

        _sut.ValidateOfficeToken(token, "proj-A", "secret.docx").Should().BeNull();
    }

    [Fact]
    public void OfficeToken_IsNotAcceptedAsUserToken_AndViceVersa()
    {
        // Разные audience: office-токен нельзя предъявить как пользовательский JWT и наоборот
        var office = _sut.IssueOfficeToken("owner-1", "proj-A", "a.docx");
        var user = _sut.Issue(new User { Id = "owner-1", Username = "alice" }).token;

        _sut.ValidateUserToken(office).Should().BeNull();
        _sut.ValidateOfficeToken(user, "proj-A", "a.docx").Should().BeNull();
    }

    [Fact]
    public void OfficeToken_ForgedWithForeignKey_Rejected()
    {
        var foreignKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(48));
        var creds = new SigningCredentials(foreignKey, SecurityAlgorithms.HmacSha256);
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: "ClaudeHomeServer-office",
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "intruder"),
                     new Claim("oo_pid", "proj-A"), new Claim("oo_path", "a.docx")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds));

        _sut.ValidateOfficeToken(forged, "proj-A", "a.docx").Should().BeNull();
    }

    [Fact]
    public void ValidateUserToken_GoodToken_ReturnsSub_AndNullOnGarbage()
    {
        var token = _sut.Issue(new User { Id = "u-7", Username = "alice" }).token;

        _sut.ValidateUserToken(token).Should().Be("u-7");
        _sut.ValidateUserToken("not-a-token").Should().BeNull();
        _sut.ValidateUserToken(null).Should().BeNull();
    }

    // --- Секрет ---

    [Fact]
    public void Constructor_FirstRun_CreatesSecretFile()
    {
        // _sut уже создан в конструкторе теста — секрет должен лежать рядом с DataPath
        var secretPath = Path.Combine(_tempDir, "jwt-secret.txt");

        File.Exists(secretPath).Should().BeTrue();
        File.ReadAllText(secretPath).Trim().Length.Should().BeGreaterThanOrEqualTo(32);
    }

    [Fact]
    public void Constructor_SecondInstance_ReusesSecret_TokensCrossValid()
    {
        var secretPath = Path.Combine(_tempDir, "jwt-secret.txt");
        var secretBefore = File.ReadAllText(secretPath);
        var (token, _) = _sut.Issue(new User { Username = "alice" });

        // «Рестарт сервера»: новый сервис с тем же DataPath переиспользует секрет
        var restarted = new JwtService(BuildConfig(_tempDir), NullLogger<JwtService>.Instance);

        File.ReadAllText(secretPath).Should().Be(secretBefore);
        var act = () => Validate(token, restarted.ValidationParameters, out _);
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_DifferentDataPath_GeneratesDifferentSecret_TokensNotCrossValid()
    {
        var otherDir = Path.Combine(_tempDir, "other");
        Directory.CreateDirectory(otherDir);
        var other = new JwtService(BuildConfig(otherDir), NullLogger<JwtService>.Instance);
        var (token, _) = _sut.Issue(new User { Username = "alice" });

        var act = () => Validate(token, other.ValidationParameters, out _);

        act.Should().Throw<SecurityTokenException>();
    }
}
