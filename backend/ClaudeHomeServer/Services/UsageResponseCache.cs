using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Кэш ответа /api/usage: хранит готовый UsageResponse по (scope, isAdmin) и отдаёт его,
// пока не изменился отпечаток входов. Инвалидация — по СОБЫТИЮ (версии снимков/пула/опроса
// растут на Record/смене состояния, mtime файла тарифа — на re-login), а не по таймеру вслепую.
// Кэшируем ОБЪЕКТ, не строку: сериализацией занимается MVC, как и раньше — формат wire тот же.
// Объект стабилен: UsageSnapshot — неизменяемый record, списки снимков GetLatestPerWindow
// строит свежими, pollStatuses контроллер копирует в новый словарь (см. UsageController).
public sealed class UsageResponseCache
{
    // Отпечаток входов ответа: любое изменение этих версий меняет ответ.
    // Usage — UsageService.Version, Pool — ClaudeSubscriptionPool.Version,
    // Poll — SubscriptionOAuthUsageService.Version, Plan — mtime(creds).Ticks (0 — файла нет),
    // Providers — отпечаток состава LLM-провайдеров (смена состава меняет блок providerSnaps).
    public sealed record Fingerprint(long Usage, long Pool, long Poll, long Plan, long Providers);

    // Отпечаток состава LLM-провайдеров: SHA-256 по отсортированным ключам, первые 8 байт.
    // Пустой/нулевой состав — 0. Порядок ключей не важен (сортировка), поэтому добавление
    // провайдера в любом месте состава меняет отпечаток, а переименование — тоже.
    public static long ProviderFingerprint(IEnumerable<LlmProviderConfig>? providers)
    {
        if (providers is null) return 0;
        var keys = providers.Select(p => p.Key ?? "").OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0) return 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var k in keys)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(k));
            hash.AppendData("\n"u8);
        }
        return BinaryPrimitives.ReadInt64BigEndian(hash.GetCurrentHash().AsSpan(0, 8));
    }

    private sealed record Entry(Fingerprint Fp, UsageResponse Response);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    // scope — "limits" | ""; admin — видит ли пользователь админские поля (UnavailableModels).
    public bool TryGet(string scope, bool admin, Fingerprint fp, out UsageResponse response)
    {
        if (_entries.TryGetValue(Key(scope, admin), out var e) && e.Fp == fp)
        {
            response = e.Response;
            return true;
        }
        response = null!;
        return false;
    }

    public void Set(string scope, bool admin, Fingerprint fp, UsageResponse response)
        => _entries[Key(scope, admin)] = new Entry(fp, response);

    private static string Key(string scope, bool admin) => $"{scope}|{(admin ? "a" : "u")}";
}
