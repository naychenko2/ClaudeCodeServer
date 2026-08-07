using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Dossiers;

// Санитайзер секретов для конвейера выжимки паспортов (ADR-004 §3): чистая статическая
// логика без побочных эффектов — источники секретов (конфиг, файлы профилей) собирает
// InstanceSecretsProvider отдельно, сюда приходит готовый список точных значений.
// Прогоняется ДВАЖДЫ: до модели (сырьё) и после (модель могла процитировать то, что
// просочилось мимо первого прохода).
public static partial class SecretRedactor
{
    // Точное значение участвует в замене-по-подстроке, только если оно длиннее порога:
    // пустой ApiKey — штатное состояние (провайдер выключен), а короткое значение из
    // тестового конфига превратило бы редактор в решето (подстрока встречается в половине
    // текстов). Короче порога — исключается молча здесь; вызывающий (InstanceSecretsProvider)
    // логирует факт при построении списка.
    public const int MinExactSecretLength = 12;

    // PEM/SSH-блоки режутся ЦЕЛИКОМ (от BEGIN до END) — построчного совпадения внутри
    // base64-тела нет, а блок целиком совершенно однозначно является секретом.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?-----END [A-Z0-9 ]+-----", RegexOptions.Compiled)]
    private static partial Regex PemBlockRegex();

    // Известные форматы токенов провайдеров: Anthropic (sk-ant-…), общий sk-…, GitHub
    // (ghp_/gho_/ghu_/ghs_/ghr_), AWS access key id (AKIA/ASIA + 16 симв.), Slack (xox[baprs]-).
    [GeneratedRegex(
        @"\b(sk-ant-[A-Za-z0-9_\-]{10,}|sk-[A-Za-z0-9]{20,}|gh[oprsu]_[A-Za-z0-9]{20,}|" +
        @"A[SK]IA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,})\b", RegexOptions.Compiled)]
    private static partial Regex KnownPrefixRegex();

    // JWT: три base64url-сегмента через точку (header.payload.signature)
    [GeneratedRegex(@"\bey[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\b", RegexOptions.Compiled)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-_.=]{10,}", RegexOptions.Compiled)]
    private static partial Regex BearerRegex();

    // Строка подключения вида scheme://user:password@host — пароль в открытом виде в URL
    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.-]*://[^\s:/@]+:[^\s@]+@[^\s]+", RegexOptions.Compiled)]
    private static partial Regex ConnectionStringRegex();

    // Строка из diff'а, похожая на присваивание секрета: api_key = "…", TOKEN: xyz…
    [GeneratedRegex(@"(?i)\b(api[_-]?key|token|secret|password)\s*[=:]\s*\S+", RegexOptions.Compiled)]
    private static partial Regex AssignmentRegex();

    /// <summary>
    /// Редакция текста: (1) точные значения известных секретов инстанса длиннее порога,
    /// (2) PEM/SSH-блоки целиком, (3) известные форматы токенов/JWT/Bearer/connection-string,
    /// (4) присваивания, похожие на секрет. Порядок важен: точные значения ловят то, чего
    /// не знают регулярки (произвольные ключи сторонних провайдеров без узнаваемого префикса).
    /// </summary>
    public static string Redact(string? text, IReadOnlyCollection<string>? exactSecrets = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = text;

        if (exactSecrets is { Count: > 0 })
        {
            foreach (var secret in exactSecrets)
            {
                if (string.IsNullOrEmpty(secret) || secret.Length < MinExactSecretLength) continue;
                if (result.Contains(secret, StringComparison.Ordinal))
                    result = result.Replace(secret, "[REDACTED:instance-secret]", StringComparison.Ordinal);
            }
        }

        result = PemBlockRegex().Replace(result, "[REDACTED:private-key]");
        result = JwtRegex().Replace(result, "[REDACTED:jwt]");
        result = BearerRegex().Replace(result, "Bearer [REDACTED:token]");
        result = KnownPrefixRegex().Replace(result, "[REDACTED:token]");
        result = ConnectionStringRegex().Replace(result, "[REDACTED:connection-string]");
        result = AssignmentRegex().Replace(result, m => m.Groups[1].Value + "=[REDACTED:secret]");

        return result;
    }
}
