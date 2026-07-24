using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace ClaudeHomeServer.Tests;

// CT-9: валидация модульного токена RS256 на стороне ядра (gateway-passthrough, §5.2б).
// Ядро само эмитит и само проверяет — по своим приватным ключам, без JWKS.
public class ModuleTokenServiceTests
{
    private static LoadedModule Module(string id = "echo")
    {
        var manifest = new ModuleManifest
        {
            SchemaVersion = "1.0",
            Id = id,
            Version = "1.0.0",
            DisplayName = "Echo",
            Backend = new ModuleBackend
            {
                BaseUrl = "http://localhost:9/" + id,
                HealthPath = "/health",
                RoutePrefix = $"/api/modules/{id}",
            },
            Scopes = ["echo:read"],
        };
        return new LoadedModule(manifest, "/unused");
    }

    private static ModuleTokenService NewService()
    {
        // Изолированный DataPath — ModuleTokenService хранит ключи рядом с ним
        var cfg = Mock.Of<IConfiguration>(c =>
            c["DataPath"] == Path.Combine(Path.GetTempPath(), "ct9-token", Guid.NewGuid().ToString("N"), "projects.json"));
        return new ModuleTokenService(cfg, NullLogger<ModuleTokenService>.Instance);
    }

    [Fact]
    public void Принимает_свежий_mcp_токен_своего_модуля()
    {
        var svc = NewService();
        var module = Module();

        var token = svc.Issue(module, "user-1", "Tester", "mcp");

        Assert.True(svc.TryValidate(token, module, out var sub));
        Assert.Equal("user-1", sub);
    }

    [Fact]
    public void Отвергает_chan_gateway_для_passthrough()
    {
        var svc = NewService();
        var module = Module();
        // chan=gateway живёт только в middleware браузер↔ядро; passthrough пропускает лишь chan=mcp
        var token = svc.Issue(module, "user-1", "Tester", "gateway");

        Assert.False(svc.TryValidate(token, module, out var sub));
        Assert.Null(sub);
    }

    [Fact]
    public void Отвергает_токен_с_чужим_audience()
    {
        var svc = NewService();
        var echo = Module("echo");
        var finance = Module("finance");
        // Выпущен для echo, а валидируется против finance (чужой aud)
        var token = svc.Issue(echo, "user-1", "Tester", "mcp");

        Assert.False(svc.TryValidate(token, finance, out var sub));
        Assert.Null(sub);
    }

    [Fact]
    public void Отвергает_битую_подпись()
    {
        var svc = NewService();
        var module = Module();
        var token = svc.Issue(module, "user-1", "Tester", "mcp");

        // Портим signature-сегмент — подпись перестаёт сходиться
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.A{parts[2][1..]}";

        Assert.False(svc.TryValidate(tampered, module, out var sub));
        Assert.Null(sub);
    }

    [Fact]
    public void Отвергает_пустой_и_мусор()
    {
        var svc = NewService();
        var module = Module();

        Assert.False(svc.TryValidate(null, module, out var s1));
        Assert.False(svc.TryValidate("", module, out var s2));
        Assert.False(svc.TryValidate("not.a.jwt", module, out var s3));
        Assert.Null(s1);
        Assert.Null(s2);
        Assert.Null(s3);
    }

    [Fact]
    public void Отвергает_протухший_exp()
    {
        // Протухший модульный токен с ВАЛИДНОЙ сервисной подписью — подписываем активным
        // ключом напрямую (reflection), exp в прошлом. ValidateLifetime (skew 60 с) → false.
        var svc = NewService();
        var module = Module();
        var key = (RsaSecurityKey)typeof(ModuleTokenService)
            .GetField("_activeKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!;
        var jwt = new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: module.Audience,
            claims: new[] { new Claim("sub", "user-1"), new Claim("chan", "mcp") },
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        var expired = new JwtSecurityTokenHandler().WriteToken(jwt);

        Assert.False(svc.TryValidate(expired, module, out var sub));
        Assert.Null(sub);
    }

    [Fact]
    public void Отвергает_HS256_alg_cc_token_alg_confusion()
    {
        // Защита от alg-confusion: HS256 (cc_token ядра) не должен приниматься как модульный,
        // даже если подделает iss/aud/chan под схему §5.1.
        var svc = NewService();
        var module = Module();
        var hmac = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('k', 64)));
        var jwt = new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: module.Audience,
            claims: new[] { new Claim("sub", "user-1"), new Claim("chan", "mcp") },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(hmac, SecurityAlgorithms.HmacSha256));
        var hsToken = new JwtSecurityTokenHandler().WriteToken(jwt);

        Assert.False(svc.TryValidate(hsToken, module, out var sub));
        Assert.Null(sub);
    }
}
