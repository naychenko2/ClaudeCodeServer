using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.ChatContext;

// Реестр видов контекста: собирает провайдеров вертикалей. Два провайдера с одним Kind — ошибка
// сборки, а не молчаливая победа последнего (иначе вид «переезжает» к другой вертикали незаметно).
// Выключенная вертикаль провайдера не регистрирует — её вид для спины неизвестен (kind_unknown).
public sealed class ContextKindRegistry
{
    private readonly Dictionary<string, IContextKindProvider> _byKind = new(StringComparer.Ordinal);

    public ContextKindRegistry(IEnumerable<IContextKindProvider> providers)
    {
        foreach (var provider in providers)
        foreach (var kind in provider.Kinds)
        {
            if (_byKind.TryGetValue(kind, out var owner))
                throw new InvalidOperationException(
                    $"Вид контекста «{kind}» объявили два провайдера: {owner.GetType().Name} и {provider.GetType().Name}");
            _byKind[kind] = provider;
        }
    }

    public IReadOnlyCollection<string> Kinds => _byKind.Keys;

    public bool IsRegistered(string kind) => _byKind.ContainsKey(kind);

    public IContextKindProvider? Find(string kind) => _byKind.GetValueOrDefault(kind);

    // null — Ref годится; иначе текст отказа. Незарегистрированный вид — отказ с этим же текстом
    public string? Validate(ContextScope scope, string kind, JsonObject reference) =>
        Find(kind) is { } provider
            ? provider.Validate(scope, kind, reference)
            : $"Вид контекста «{kind}» не зарегистрирован";
}
