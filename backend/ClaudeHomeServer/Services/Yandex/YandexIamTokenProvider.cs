using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace ClaudeHomeServer.Services.Yandex;

// IAM-токен Yandex Cloud из авторизованного ключа сервисного аккаунта.
//
// Зачем вообще: Billing API принимает ТОЛЬКО IAM-токен (Api-Key, которым ходит SpeechKit,
// там не работает), а живёт такой токен 12 часов — держать его в конфиге нельзя. Поэтому
// сервер сам подписывает JWT ключом сервисного аккаунта и меняет его на IAM-токен.
//
// Ключ берётся из appsettings.Local.json (секция Yandex:Billing) — того самого JSON, что
// скачивается при создании авторизованного ключа. Аккаунту нужна роль billing.accounts.viewer
// НА БИЛЛИНГ-АККАУНТЕ: роль, выданная на облако, биллинг не открывает — запрос вернёт пустой
// список аккаунтов, а не отказ, и это самая частая причина «баланса нет при живом ключе».
public sealed class YandexIamTokenProvider(IHttpClientFactory http, IConfiguration config,
    ILogger<YandexIamTokenProvider> logger)
{
    public const string HttpClientName = "yandex-billing";
    private const string TokenEndpoint = "https://iam.api.cloud.yandex.net/iam/v1/tokens";

    private readonly string? _serviceAccountId = config["Yandex:Billing:ServiceAccountId"];
    private readonly string? _keyId = config["Yandex:Billing:KeyId"];
    private readonly string? _privateKey = config["Yandex:Billing:PrivateKey"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTime _expiresAt;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_serviceAccountId)
        && !string.IsNullOrWhiteSpace(_keyId)
        && !string.IsNullOrWhiteSpace(_privateKey);

    // Действующий IAM-токен; null — не настроено или обмен не удался (причина в логе).
    // Кэш с запасом в 5 минут: токен живёт 12 часов, и запрашивать его на каждый показ
    // баланса незачем — обмен стоит сетевого похода и подписи RSA.
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _expiresAt.AddMinutes(-5)) return _token;

            var jwt = BuildJwt();
            if (jwt is null) return null;

            var client = http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await client.PostAsync(TokenEndpoint,
                new StringContent(JsonSerializer.Serialize(new { jwt }), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Yandex IAM отверг обмен ({Status}): {Body}", (int)resp.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("iamToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogWarning("Yandex IAM ответил успехом, но токена в ответе нет");
                return null;
            }

            _token = token;
            _expiresAt = doc.RootElement.TryGetProperty("expiresAt", out var e)
                && DateTime.TryParse(e.GetString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var exp)
                ? exp
                // Срок не разобрался — считаем час: недооценить безопасно (лишний обмен),
                // переоценить нельзя (протухший токен даст 401 на каждом запросе)
                : DateTime.UtcNow.AddHours(1);
            return _token;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            // Недоступность Яндекса — штатный случай, строку пишет QuietHttpLogger
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // PEM в JSON ключа лежит одной строкой, где переносы записаны ДВУМЯ символами
    // (обратная косая + n): так его отдаёт Яндекс и так он попадает в конфиг, если
    // значение скопировали целиком. ImportFromPem такую строку не разбирает — ему нужны
    // настоящие переносы, иначе баланс молча не работает при верном ключе.
    internal static string UnescapeNewlines(string pem) => pem.Replace("\\n", "\n");

    // JWT для обмена: PS256, kid = id ключа, iss = id сервисного аккаунта, aud — сам эндпоинт
    // обмена. Срок жизни час — максимум, который принимает Яндекс.
    private string? BuildJwt()
    {
        try
        {
            using var rsa = RSA.Create();
            // В JSON ключа PEM лежит одной строкой с \n; если человек вставил его руками,
            // переносы могли остаться литеральными — тогда ImportFromPem не разберёт ключ
            rsa.ImportFromPem(UnescapeNewlines(_privateKey!));

            var now = DateTime.UtcNow;
            // iat обязателен по требованиям обмена, а этот конструктор кладёт в payload только
            // nbf/exp — без явного claim Яндекс отвергает JWT (сторож — тест на состав полей)
            var iat = new System.Security.Claims.Claim(JwtRegisteredClaimNames.Iat,
                EpochTime.GetIntDate(now).ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Security.Claims.ClaimValueTypes.Integer64);
            var token = new JwtSecurityToken(
                issuer: _serviceAccountId,
                audience: TokenEndpoint,
                claims: [iat],
                notBefore: now,
                expires: now.AddHours(1),
                signingCredentials: new SigningCredentials(
                    new RsaSecurityKey(rsa) { KeyId = _keyId },
                    SecurityAlgorithms.RsaSsaPssSha256));
            // Токен пишем ДО выхода из using: обработчик подписывает ключом прямо здесь
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        catch (Exception ex)
        {
            // Битый PEM, не тот формат ключа — конфигурационная ошибка, человеку нужна причина
            logger.LogError(ex, "Не удалось подписать JWT для Yandex IAM: проверь Yandex:Billing:PrivateKey");
            return null;
        }
    }
}
