using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using Microsoft.IdentityModel.Tokens;

namespace ClaudeHomeServer.Services.Modules;

/// <summary>
/// Выпуск модульных токенов (RS256) и JWKS по контракту §5 (ТЗ R3).
/// Отдельная асимметричная ветка: HMAC-ключ ядра (JwtService) модулям не раздаётся
/// и здесь не используется. Ключи персистятся в data/module-keys.json.
/// Ротация §5.3: новый ключ публикуется в JWKS в момент активации — ДО первой подписи им;
/// отставленный ключ остаётся в JWKS ≥24 ч после последней подписи.
/// </summary>
public sealed class ModuleTokenService
{
    /// <summary>TTL по каналу выпуска (§5.1, §10.2): gateway — 5 мин, mcp и host — 60 мин.</summary>
    public static readonly TimeSpan GatewayTokenLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan McpTokenLifetime = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan HostTokenLifetime = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan RetiredKeyGrace = TimeSpan.FromHours(24);

    private sealed class StoredKey
    {
        [JsonPropertyName("kid")] public string Kid { get; set; } = "";
        [JsonPropertyName("privateKeyPkcs8")] public string PrivateKeyPkcs8 { get; set; } = "";
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
        // Момент отставки (последней подписи этим ключом); null — ключ активен
        [JsonPropertyName("retiredAt")] public DateTime? RetiredAt { get; set; }
    }

    private readonly string _storePath;
    private readonly ILogger<ModuleTokenService> _log;
    private readonly Lock _sync = new();
    private List<StoredKey> _keys = [];
    private RsaSecurityKey _activeKey = null!;   // инициализируется в конструкторе (EnsureActiveKey)
    private string _activeKid = "";

    public ModuleTokenService(IConfiguration config, ILogger<ModuleTokenService> log)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "module-keys.json");
        Load();
        EnsureActiveKey();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storePath))
                _keys = JsonSerializer.Deserialize<List<StoredKey>>(File.ReadAllText(_storePath)) ?? [];
        }
        catch (Exception ex)
        {
            // Нечитаемый стор — не валим ядро: сгенерируем свежий ключ (модули отвалидируют по JWKS)
            _log.LogWarning("Стор ключей модулей {Path} не читается ({Error}) — будет создан новый ключ",
                _storePath, ex.Message);
            _keys = [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        File.WriteAllText(_storePath, JsonSerializer.Serialize(_keys,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private void EnsureActiveKey()
    {
        lock (_sync)
        {
            var active = _keys.FirstOrDefault(k => k.RetiredAt is null);
            if (active is null)
            {
                using var rsa = RSA.Create(2048);
                active = new StoredKey
                {
                    // kid по формату §5.1 ^[a-z0-9-]{8,64}$
                    Kid = $"mk-{Guid.NewGuid():N}",
                    PrivateKeyPkcs8 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
                    CreatedAt = DateTime.UtcNow,
                };
                _keys.Add(active);
                Save();
                _log.LogInformation("Сгенерирован RSA-ключ модульных токенов kid={Kid}", active.Kid);
            }
            var rsaKey = RSA.Create();
            rsaKey.ImportPkcs8PrivateKey(Convert.FromBase64String(active.PrivateKeyPkcs8), out _);
            _activeKey = new RsaSecurityKey(rsaKey) { KeyId = active.Kid };
            _activeKid = active.Kid;
        }
    }

    /// <summary>
    /// Ротация ключа (§5.3): текущий ключ отставляется (остаётся в JWKS ещё ≥24 ч),
    /// новый становится активным. Новый ключ виден в JWKS с этого же момента —
    /// строго до первой подписи им.
    /// </summary>
    public void Rotate()
    {
        lock (_sync)
        {
            foreach (var k in _keys.Where(k => k.RetiredAt is null))
                k.RetiredAt = DateTime.UtcNow;
            Save();
        }
        EnsureActiveKey();
        _log.LogInformation("Ротация ключа модульных токенов: активен kid={Kid}", _activeKid);
    }

    /// <summary>
    /// Выпуск модульного токена по замороженной схеме §5.1.
    /// chan: "gateway" (TTL 5 мин) | "mcp" (TTL 60 мин) | "host" (TTL 60 мин, §10.2).
    /// </summary>
    public string Issue(LoadedModule module, string userId, string userName, string chan)
    {
        if (chan is not ("gateway" or "mcp" or "host"))
            throw new ArgumentException($"Неизвестный канал выпуска «{chan}»", nameof(chan));
        var ttl = chan switch
        {
            "gateway" => GatewayTokenLifetime,
            "mcp" => McpTokenLifetime,
            _ => HostTokenLifetime,
        };
        var now = DateTime.UtcNow;

        RsaSecurityKey key;
        lock (_sync) key = _activeKey;
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        // iat/nbf — явные claims (§5.1: nbf=iat). Конструктор JwtSecurityToken сам их не пишет,
        // поэтому задаём NumericDate вручную, чтобы модуль видел ровно эту схему.
        var iat = EpochTime.GetIntDate(now).ToString();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Name, userName),
            new("scope", module.ScopeString),
            new("chan", chan),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, iat, ClaimValueTypes.Integer64),
        };
        var jwt = new JwtSecurityToken(
            issuer: "ClaudeHomeServer",
            audience: module.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(ttl),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>Допустимая временная неточность при валидации модульного токена (§5.1: 60 с).</summary>
    private static readonly TimeSpan ValidationClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Валидация модульного токена RS256 (контракт §5.1) своими ключами — БЕЗ JWKS
    /// (JWKS — публичный эндпоинт для модулей; ядро проверяет по своим приватным ключам
    /// напрямую, потому что оно же их и эмитит). Используется gateway-веткой passthrough
    /// (§5.2б): модуль шлёт свой MODULE_API_TOKEN (chan=mcp) как Authorization при обратных
    /// вызовах через gateway.
    /// Проверяет alg=RS256 (явно, до валидации — защита от alg-confusion), iss, aud (точное
    /// сравнение с module.Audience), chan=mcp, подпись и lifetime (skew 60 с). Возвращает
    /// sub (userId) при успехе; любая негодность → false и null.
    /// </summary>
    public bool TryValidate(string? token, LoadedModule module, out string? userId)
    {
        // Passthrough (§5.2б) — только модульные токены канала mcp: chan=gateway живёт
        // лишь в gateway-middleware на пути браузер↔ядро и извне прийти не может.
        var validated = Validate(token, module, "mcp");
        userId = validated?.UserId;
        return validated is not null;
    }

    /// <summary>Провалидированный модульный токен: кто (sub), имя и канал выпуска.</summary>
    public sealed record ValidatedModuleToken(string UserId, string UserName, string Chan);

    /// <summary>
    /// Общая валидация модульного токена RS256 с перечнем допустимых каналов (chan).
    /// Тот же контракт §5.1, что и TryValidate; host-канал (§10.2) требует и обмен токена
    /// (принимает gateway|mcp), и вызовы /api/host/** (принимают только host).
    /// null — токен негоден (подпись/aud/lifetime/chan вне списка).
    /// </summary>
    public ValidatedModuleToken? Validate(string? token, LoadedModule module, params string[] allowedChans)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
        JwtSecurityToken parsed;
        try { parsed = handler.ReadJwtToken(token); }
        catch { return null; }

        // alg жёстко RS256 (§5.1): HS256 сюда не относится (это cc_token из отдельной ветки).
        // Проверяем ДО валидации подписи — классический alg-confusion (HS256 с публичной
        // RSA-компонентой как HMAC-секретом) здесь невозможен.
        if (parsed.Header.Alg != SecurityAlgorithms.RsaSha256) return null;

        List<RsaSecurityKey> publicKeys;
        lock (_sync)
        {
            var cutoff = DateTime.UtcNow - RetiredKeyGrace;
            publicKeys = _keys
                .Where(k => k.RetiredAt is null || k.RetiredAt >= cutoff)
                .Select(ToPublicKey)
                .ToList();
        }
        if (publicKeys.Count == 0) return null;

        // Перебираем ключи (активный + отставленные в grace 24 ч): при ротации (§5.3) токены,
        // подписанные только что отставленным ключом, должны ещё приниматься.
        foreach (var key in publicKeys)
        {
            ClaimsPrincipal principal;
            try
            {
                principal = handler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "ClaudeHomeServer",
                    ValidateAudience = true,
                    ValidAudience = module.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ClockSkew = ValidationClockSkew,
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role,
                }, out _);
            }
            catch
            {
                // Ключ не подошёл (другая подпись/ротация) — пробуем следующий
                continue;
            }

            var chan = principal.FindFirstValue("chan");
            if (chan is null || !allowedChans.Contains(chan, StringComparer.Ordinal)) return null;
            var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrEmpty(userId)) return null;
            return new ValidatedModuleToken(
                userId, principal.FindFirstValue(JwtRegisteredClaimNames.Name) ?? "", chan);
        }
        return null;
    }

    // Публичный RSA-ключ (только Modulus/Exponent) из хранимого приватного — для проверки
    // подписи токенов. Приватная компонента намеренно отбрасывается: валидация её не требует.
    private static RsaSecurityKey ToPublicKey(StoredKey stored)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.PrivateKeyPkcs8), out _);
        var pub = rsa.ExportParameters(false);
        return new RsaSecurityKey(new RSAParameters { Modulus = pub.Modulus, Exponent = pub.Exponent })
        { KeyId = stored.Kid };
    }

    /// <summary>
    /// JWKS-документ (§5.3): активный ключ + отставленные, чей grace 24 ч ещё не вышел.
    /// Только публичные компоненты.
    /// </summary>
    public object BuildJwks()
    {
        lock (_sync)
        {
            var cutoff = DateTime.UtcNow - RetiredKeyGrace;
            var keys = new List<object>();
            foreach (var k in _keys)
            {
                if (k.RetiredAt is { } retired && retired < cutoff) continue;
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(k.PrivateKeyPkcs8), out _);
                var p = rsa.ExportParameters(false);
                keys.Add(new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = k.Kid,
                    n = Base64UrlEncoder.Encode(p.Modulus!),
                    e = Base64UrlEncoder.Encode(p.Exponent!),
                });
            }
            return new { keys };
        }
    }
}
