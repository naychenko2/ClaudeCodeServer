using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Modules;

/// <summary>
/// Лимит одновременных вызовов LLM-канала на модуль (контракт §10.5: ≤4, сверх — 429).
/// Неблокирующий: свободного слота нет — вызов сразу отклоняется, очереди не копим
/// (модуль обязан уметь ретраить по Retry-After).
/// </summary>
public sealed class HostLlmConcurrencyLimiter
{
    public const int MaxConcurrentPerModule = 4;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _byModule =
        new(StringComparer.Ordinal);

    private SemaphoreSlim For(string moduleId) =>
        _byModule.GetOrAdd(moduleId, _ => new SemaphoreSlim(MaxConcurrentPerModule, MaxConcurrentPerModule));

    /// <summary>Занять слот. false — все слоты модуля заняты (вызывающий отвечает 429).</summary>
    public bool TryEnter(string moduleId) => For(moduleId).Wait(0);

    /// <summary>Освободить слот — строго после успешного TryEnter.</summary>
    public void Exit(string moduleId) => For(moduleId).Release();
}
