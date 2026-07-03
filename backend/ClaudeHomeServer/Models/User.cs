namespace ClaudeHomeServer.Models;

public class User
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "user";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    // NT-хэш для NTLM WebDAV: MD4(UTF-16LE(password)); null — если пользователь ещё не логинился после обновления
    public byte[]? NtHash { get; set; }
    // Per-user override фич-флагов поверх дефолтов из FeatureFlagCatalog; null/отсутствует — все по дефолту
    public Dictionary<string, bool>? FeatureFlags { get; set; }
    // Per-user пороги индикатора заполнения контекста (проценты); null — дефолты фронта
    public ContextThresholds? ContextThresholds { get; set; }
}

// Пороги подсветки индикатора контекста: warn — янтарь, danger — красный
public record ContextThresholds(int WarnPct, int DangerPct);
