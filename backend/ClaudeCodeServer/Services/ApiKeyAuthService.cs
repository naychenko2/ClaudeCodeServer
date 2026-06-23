using System.Security.Cryptography;
using System.Text;

namespace ClaudeCodeServer.Services;

/// <summary>
/// Хранит и валидирует единственный API-ключ доступа к серверу.
/// Источник ключа по приоритету:
///   1. Конфигурация Auth:ApiKey (env Auth__ApiKey, appsettings, user-secrets)
///   2. Файл data/auth-key.txt (или путь из Auth:KeyFile)
///   3. Автогенерация: создаётся случайный ключ, пишется в файл и в консоль
/// </summary>
public class ApiKeyAuthService
{
    public const string SchemeName = "ApiKey";

    private readonly byte[] _keyBytes;

    public string ApiKey { get; }

    public ApiKeyAuthService(IConfiguration config, ILogger<ApiKeyAuthService> logger)
    {
        var configured = config["Auth:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ApiKey = configured.Trim();
            logger.LogInformation("API-ключ загружен из конфигурации (Auth:ApiKey).");
        }
        else
        {
            var path = config["Auth:KeyFile"]
                ?? Path.Combine(AppContext.BaseDirectory, "data", "auth-key.txt");
            ApiKey = LoadOrCreate(path, logger);
        }

        _keyBytes = Encoding.UTF8.GetBytes(ApiKey);
    }

    private static string LoadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                logger.LogInformation("API-ключ загружен из {Path}.", path);
                return existing;
            }
        }

        var generated = GenerateKey();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, generated);

        logger.LogWarning(
            "API-ключ не был задан — сгенерирован новый и сохранён в {Path}.\n" +
            "    ╔══════════════════════════════════════════════════════════╗\n" +
            "    ║  ВВЕДИТЕ ЭТОТ КЛЮЧ НА СТРАНИЦЕ ВХОДА:                     ║\n" +
            "    ║  {Key}\n" +
            "    ╚══════════════════════════════════════════════════════════╝",
            path, generated);

        return generated;
    }

    private static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        // URL-safe base64 без паддинга
        var token = Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return "cck_" + token;
    }

    /// <summary>Сравнение в постоянном времени — защита от тайминг-атак.</summary>
    public bool Validate(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(candidateBytes, _keyBytes);
    }
}
