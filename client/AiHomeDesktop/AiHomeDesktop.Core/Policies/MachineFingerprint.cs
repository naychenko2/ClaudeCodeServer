using System.Security.Cryptography;
using System.Text;

namespace AiHomeDesktop.Core.Policies;

/// <summary>
/// Отпечаток машины: SHA-256 (hex) от имени машины в нижнем регистре — ровно та же строка,
/// что считает сервер (Services/Desktop/DeviceRegistry.MachineFingerprint). Это контракт
/// сопряжения: меняется он только на обеих сторонах сразу.
///
/// Сложнее (MAC, серийники) намеренно не берём: отпечаток не защищает от подделки, а
/// отвечает на один вопрос — «это не та же машина, где крутится бэкенд?».
/// </summary>
public static class MachineFingerprint
{
    /// <summary>Отпечаток этой машины — уезжает при сопряжении и в каждом запросе канала.</summary>
    public static string OfThisMachine() => Of(Environment.MachineName);

    public static string Of(string? machineName) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes((machineName ?? "").Trim().ToLowerInvariant()))).ToLowerInvariant();

    public static bool IsValid(string? fingerprint) =>
        !string.IsNullOrWhiteSpace(fingerprint)
        && fingerprint.Length == 64
        && fingerprint.All(Uri.IsHexDigit);
}
