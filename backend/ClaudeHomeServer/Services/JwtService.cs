using System.Globalization;
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
    private readonly UserStore _users;

    // Версия сессий пользователя (User.TokenVersion) в токене: смена пароля бампает её в сторе,
    // и все ранее выданные токены перестают проходить проверку (см. IsSessionCurrent)
    public const string TokenVersionClaim = "tv";
    // Метка сервисного токена: он выдаётся сервером самому себе (MCP, push), пароль в его
    // выдаче не участвует — значит и отзывать его при смене пароля нечего
    public const string TokenKindClaim = "typ";
    public const string ServiceTokenKind = "svc";

    public JwtService(IConfiguration config, UserStore users, ILogger<JwtService> logger)
    {
        _users = users;
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
            new Claim(TokenVersionClaim, user.TokenVersion.ToString(CultureInfo.InvariantCulture)),
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
            // Явная метка вместо опоры на имя "mcp-tasks": по ней проверка версии сессий
            // пропускает сервисные токены, и живой ход Claude не рвётся сменой пароля
            new Claim(TokenKindClaim, ServiceTokenKind),
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

    // ── Внешний доступ к дев-серверу по поддомену (см. ExternalPreviewOptions) ─────

    /// <summary>Аудитория ссылок внешнего доступа — своя, чтобы обычный токен сюда не подошёл.</summary>
    public const string PreviewAudience = "ClaudeHomeServer-preview";

    /// <summary>
    /// Подписанная ссылка на ОДИН сервис ОДНОГО проекта владельца. Форма как у office-токена
    /// (своя аудитория + привязка к сущности), но с двумя обязательными добавками:
    ///
    /// jti — идентификатор ссылки в реестре (ExternalPreviewStore). Именно он даёт мгновенный
    /// отзыв: подписанный JWT сам по себе не отзывается, и без реестра выданная ссылка жила бы
    /// до конца срока, что бы владелец ни делал.
    ///
    /// tv — версия сессий. У office-токена её нет, здесь она обязательна: ссылка открывает
    /// машину наружу, и выход из аккаунта обязан закрывать доступ.
    ///
    /// Порт в токен НЕ кладём намеренно: он резолвится по serviceId на месте, иначе ссылка
    /// пережила бы смену конфигурации сервиса и увела на посторонний процесс.
    /// </summary>
    public string IssuePreviewToken(string userId, string projectId, string serviceId, string jti, TimeSpan lifetime)
    {
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, jti),
            new("pv_pid", projectId),
            new("pv_sid", serviceId),
        };
        // Без tv токен не отозвать выходом из аккаунта: IsSessionCurrent такому откажет,
        // и это правильный исход — доступ наружу без права на отзыв не выдаём
        var version = _users.GetById(userId)?.TokenVersion;
        if (version is not null)
            claims.Add(new Claim(TokenVersionClaim, version.Value.ToString(CultureInfo.InvariantCulture)));

        var jwt = new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: PreviewAudience,
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>
    /// Проверяет ссылку внешнего доступа: подпись, срок, аудиторию и версию сессий.
    /// Возвращает (userId, projectId, serviceId, jti) либо null.
    ///
    /// Не отозвана ли ссылка — проверяет вызывающий по jti в реестре: подпись об этом
    /// не знает ничего.
    /// </summary>
    public (string UserId, string ProjectId, string ServiceId, string Jti)? ValidatePreviewToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "ClaudeHomeServer",
                ValidateAudience = true,
                ValidAudience = PreviewAudience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role,
            }, out _);

            if (!IsSessionCurrent(principal)) return null;

            var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var projectId = principal.FindFirstValue("pv_pid");
            var serviceId = principal.FindFirstValue("pv_sid");
            var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(projectId)
                || string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(jti)) return null;
            return (userId, projectId, serviceId, jti);
        }
        catch { return null; }
    }

    /// <summary>
    /// Жив ли предъявленный токен с точки зрения версии сессий: сервисные пропускаем,
    /// у пользовательских claim tv обязан совпасть с текущей версией в UserStore.
    /// Единственная точка правила — её зовут и JwtBearer (весь [Authorize]-периметр),
    /// и ValidateUserToken (middleware вне MVC).
    /// </summary>
    public bool IsSessionCurrent(ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        if (principal.FindFirstValue(TokenKindClaim) == ServiceTokenKind) return true;

        var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(userId)) return false;

        // Токена без claim tv (выпущен до введения версий) достаточно, чтобы отказать:
        // именно такие токены смена пароля и не отзывала
        var raw = principal.FindFirstValue(TokenVersionClaim);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)) return false;

        return _users.IsTokenVersionCurrent(userId, version);
    }

    // Валидирует обычный пользовательский/сервисный JWT и возвращает sub (userId) или null.
    // Используется вне MVC-пайплайна (preview-middleware), где нет готового ctx.User.
    public string? ValidateUserToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, ValidationParameters, out _);
            // Подпись и срок — ещё не всё: токен могли отозвать сменой пароля
            if (!IsSessionCurrent(principal)) return null;
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
