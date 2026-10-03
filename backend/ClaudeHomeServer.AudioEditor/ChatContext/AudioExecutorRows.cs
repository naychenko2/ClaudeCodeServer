using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Jobs;

namespace ClaudeHomeServer.Services.AudioEditor.ChatContext;

// Строки «Чем» в ответе quote звука (КТ-3, решение Р2): «Авто → сейчас: …» и по строке на каждую модель
// каталога, умеющую операцию. Цена — полями ExecutorRowDto: валюта в Unit (единица тарификации fal в неё
// не попадает), «за что» — только в подписи Price; числа и подпись берутся из одной AudioContextKind.PriceParts,
// формат подписи — ContextPriceText, второго форматирования здесь нет. Серая строка несёт причину вместо Sub
public static class AudioExecutorRows
{
    public const string UnavailableReason = "Поставщик сейчас недоступен";
    public const string PriceUnknown = "цена станет известна после запуска";

    public static IReadOnlyList<ExecutorRowDto> Build(IEnumerable<IAudioEngine> engines, AudioEditScope scope, AudioOp op,
        AudioVoiceKind? voiceKind, IAudioEngine chosen, AudioModelInfo model, AudioPrice price, bool preferLocal)
    {
        var auto = Row(chosen, model, op) with
        {
            Id = CatalogId(AudioCatalog.AutoModelId, null),
            Group = "auto",
            Name = AudioCatalog.AutoModelLabel,
            Sub = $"сейчас: {chosen.Label} · {model.Label}",
            EtaSeconds = price.Eta is > 0 ? price.Eta : null,
        };
        var rows = new List<ExecutorRowDto> { auto };
        foreach (var engine in AudioCatalog.Registered(engines))
        {
            var refusal = EngineRefusal(engine, scope);
            foreach (var m in engine.Models.Where(m => m.Caps.Ops.Contains(op)))
            {
                var reason = refusal ?? m.DisabledReason
                    ?? (voiceKind is { } kind && !m.Caps.VoiceKinds.Contains(kind) ? "модель не берёт этот вид голоса" : null);
                var row = Row(engine, m, op) with
                {
                    Id = CatalogId(engine.Key, m.Id),
                    Group = engine.Key == Engines.LocalAudioEngine.ProviderKey ? "local" : "cloud",
                    Name = engine.Key == Engines.LocalAudioEngine.ProviderKey ? m.Label : $"{engine.Label} · {m.Label}",
                    Sub = null,
                };
                rows.Add(reason is null ? row : row with { Disabled = true, Reason = reason });
            }
        }
        return rows;
    }

    public static string CatalogId(string provider, string? model) => model is null ? provider : $"{provider}:{model}";

    private static string? EngineRefusal(IAudioEngine engine, AudioEditScope scope)
    {
        try { return engine.Enabled ? engine.ScopeRefusal(scope) : UnavailableReason; }
        catch { return UnavailableReason; }
    }

    private static ExecutorRowDto Row(IAudioEngine engine, AudioModelInfo model, AudioOp op)
    {
        var badges = new List<ExecutorBadgeDto>
        {
            model.Caps.SpeaksRu ? new("RU", "good") : new("без RU", "warn"),
            new(model.Caps.License.Label, model.Caps.License.Kind is AudioLicenseKind.Permissive or AudioLicenseKind.Unknown ? "neutral" : "warn"),
        };
        if (model.Caps.IsHeavy(op)) badges.Add(new("тяжёлая", "warn"));

        if (model.PriceHint is not { } hint)
            return new ExecutorRowDto("", "", "", null, PriceUnknown, Badges: badges);
        var (amount, unit, per) = AudioContextKind.PriceParts(hint);
        var free = amount == 0 || unit == AudioPriceUnits.Free;
        return new ExecutorRowDto("", "", "", null, AudioContextKind.PriceText(hint), Free: free,
            Amount: free ? null : amount, Unit: free ? AudioPriceUnits.Free : unit, Badges: badges);
    }
}
