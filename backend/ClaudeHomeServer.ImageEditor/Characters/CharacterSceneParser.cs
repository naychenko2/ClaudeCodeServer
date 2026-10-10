using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.ImageEditor;

// Разбор сюжета запроса в словари меток фото (ADR-027, §6): дешёвая бесплатная модель,
// при любом сбое — словарь правил. Платный claude здесь не используется никогда.
public sealed class CharacterSceneParser
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private const int CacheLimit = 256;

    private const string Instruction =
        "Из текста запроса на картинку определи, какой нужен кадр лица. Ответь JSON вида " +
        "{\"angle\":…,\"pitch\":…,\"emotion\":…}. angle: front | three-quarter | profile; " +
        "pitch: level | up | down; emotion: neutral | smile | laugh | serious | sad | surprised. " +
        "Если текст молчит об этом признаке — null. Ничего, кроме JSON.";

    private readonly ICheapTextRunner _runner;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, CharacterScene> _cache = new();

    public CharacterSceneParser(ICheapTextRunner runner, TimeSpan? timeout = null)
    {
        _runner = runner;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<CharacterScene> ParseAsync(string? prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new CharacterScene(null, null, null);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var scene = await TryModelAsync(prompt, ct);
        if (scene is null) return ParseByRules(prompt); // сбой не кэшируем: он может быть временным

        if (_cache.Count >= CacheLimit) _cache.Clear();
        _cache[key] = scene;
        return scene;
    }

    private async Task<CharacterScene?> TryModelAsync(string prompt, CancellationToken ct)
    {
        try
        {
            // Раннер может не уважать токен, поэтому потолок держим ещё и ожиданием снаружи
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            var call = _runner.RunFreeAsync(LocalActionCatalog.CharacterSceneParse,
                $"{Instruction}\n\nТЕКСТ ЗАПРОСА:\n{prompt}", jsonFormat: "json", cts.Token);
            var done = await Task.WhenAny(call, Task.Delay(_timeout, ct));
            if (done != call) return null;
            return ParseModelAnswer(await call);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    internal static CharacterScene? ParseModelAnswer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return new CharacterScene(
                Field(doc.RootElement, "angle", PhotoLabelValues.Angles),
                Field(doc.RootElement, "pitch", PhotoLabelValues.Pitches),
                Field(doc.RootElement, "emotion", PhotoLabelValues.Emotions));
        }
        catch (JsonException) { return null; }
    }

    // Значение вне словаря и «unknown» читаются как «сюжет молчит»
    private static string? Field(JsonElement obj, string name, IReadOnlySet<string> dictionary)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var value = v.GetString()?.Trim().ToLowerInvariant();
        return value is not null && value != PhotoLabelValues.Unknown && dictionary.Contains(value) ? value : null;
    }

    // Знак pitch у фото (ADR-027, §5) не сверен: «сверху» — лицо запрокинуто (up),
    // «снизу» — опущено (down). Пересмотреть вместе с результатом повторной пробы
    public static CharacterScene ParseByRules(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new CharacterScene(null, null, null);
        var text = prompt.ToLowerInvariant().Replace('ё', 'е');
        return new CharacterScene(
            First(text, ("profile", ["в профиль", "профиль"]),
                ("three-quarter", ["вполоборота", "в пол-оборота", "три четверти", "трех четвертей"]),
                ("front", ["анфас", "в камеру"])),
            First(text, ("up", ["сверху"]), ("down", ["снизу"])),
            First(text, ("laugh", ["смеется", "смех"]), ("smile", ["улыбается", "улыбк"]),
                ("serious", ["серьезн"]), ("sad", ["грустн"]), ("surprised", ["удивл"])));
    }

    private static string? First(string text, params (string Value, string[] Words)[] rules) =>
        rules.FirstOrDefault(r => r.Words.Any(w => text.Contains(w, StringComparison.Ordinal))).Value;
}
