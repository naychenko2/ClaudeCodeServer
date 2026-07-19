using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using Microsoft.IdentityModel.Tokens;

namespace ClaudeHomeServer.Services;

public class JwtService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);
    private readonly SymmetricSecurityKey _key;

    public JwtService(IConfiguration config, ILogger<JwtService> logger)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var dataDir = Path.GetDirectoryName(dataPath) ?? Path.Combine(AppContext.BaseDirectory, "data");
        var secretPath = Path.Combine(dataDir, "jwt-secret.txt");
        var secret = LoadOrCreateSecret(secretPath, logger);
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    private static string LoadOrCreateSecret(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, secret);
        logger.LogInformation("JWT-секрет сгенерирован и сохранён в {Path}.", path);
        return secret;
    }

    public (string token, DateTime expiresAt) Issue(User user)
    {
        var expiresAt = DateTime.UtcNow.Add(TokenLifetime);
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
        };
        var jwt = new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: "ClaudeHomeServer",
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
    }

    // Срок сервисного токена MCP — короткий: токен каждый ход пишется в temp-файл
    // конфига MCP, и при крэше сервера файл может остаться на диске
    public static readonly TimeSpan ServiceTokenLifetime = TimeSpan.FromDays(7);

    // Токен для MCP tasks-server от имени владельца сессии:
    // задачи per-owner, поэтому токен привязан к конкретному пользователю.
    // Живёт только в temp-конфиге MCP на машине сервера, наружу не отдаётся.
    public string IssueServiceToken(string userId)
    {
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(ClaimTypes.Name, "mcp-tasks"),
            new Claim(ClaimTypes.Role, "user"),
        };
        var jwt = new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: "ClaudeHomeServer",
            claims: claims,
            expires: DateTime.UtcNow.Add(ServiceTokenLifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    // Срок office-токена: OnlyOffice DS кеширует download-URL на время сессии
    // редактирования и дёргает callback после сохранения — берём с запасом.
    private static readonly TimeSpan OfficeTokenLifetime = TimeSpan.FromDays(1);

    // Подписанный токен доступа OnlyOffice к одному файлу одного владельца.
    // Заменяет прежний общий статичный download-токен: привязка к userId+projectId+path
    // закрывает cross-owner чтение/запись (office-download / office-callback анонимны).
    public string IssueOfficeToken(string userId, string projectId, string path)
    {
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim("oo_pid", projectId),
            new Claim("oo_path", path),
        };
        var jwt = new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: "ClaudeHomeServer-office",
            claims: claims,
            expires: DateTime.UtcNow.Add(OfficeTokenLifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    // Проверяет office-токен и сверяет привязку к projectId+path. Возвращает userId владельца
    // либо null (подпись/срок невалидны или токен выписан на другой файл/проект).
    public string? ValidateOfficeToken(string? token, string projectId, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            // MapInboundClaims=false — читаем raw "sub"/"oo_*" без ремапа в ClaimTypes.*
            // (не полагаемся на глобальный DefaultMapInboundClaims, выставленный в Program.cs)
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "ClaudeHomeServer",
                ValidateAudience = true,
                ValidAudience = "ClaudeHomeServer-office",
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role,
            }, out _);
            if (principal.FindFirstValue("oo_pid") != projectId) return null;
            if (principal.FindFirstValue("oo_path") != path) return null;
            return principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        }
        catch { return null; }
    }

    // Валидирует обычный пользовательский/сервисный JWT и возвращает sub (userId) или null.
    // Используется вне MVC-пайплайна (preview-middleware), где нет готового ctx.User.
    public string? ValidateUserToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, ValidationParameters, out _);
            return principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        }
        catch { return null; }
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = "ClaudeHomeServer",
        ValidateAudience = true,
        ValidAudience = "ClaudeHomeServer",
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ClockSkew = TimeSpan.Zero,
        // sub → ClaimTypes.NameIdentifier отключён (DefaultMapInboundClaims = false в Program.cs)
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role,
    };
}
