using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClaudeCodeServer.Models;
using Microsoft.IdentityModel.Tokens;

namespace ClaudeCodeServer.Services;

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
            issuer: "ClaudeCodeServer",
            audience: "ClaudeCodeServer",
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = "ClaudeCodeServer",
        ValidateAudience = true,
        ValidAudience = "ClaudeCodeServer",
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ClockSkew = TimeSpan.Zero,
        // sub → ClaimTypes.NameIdentifier отключён (DefaultMapInboundClaims = false в Program.cs)
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role,
    };
}
