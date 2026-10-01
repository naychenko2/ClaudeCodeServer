namespace ClaudeHomeServer.Services.AudioEditor;

// Голос из библиотеки «Голоса» в запуске (ADR-021 §5). Поле «Голос» панели несёт его значением
// voice:<slug>; исполнитель читает папку голоса и отдаёт драйверу образец, расшифровку, пару RVC и кеш
// id у поставщиков. Драйвер превращает это в своё (reference, эмбеддинг, элемент, custom_voice_id) и
// возвращает созданные привязки в AudioResult.VoiceCache — на диск их пишет исполнитель, не драйвер.
// Cached — ключ кеша (VoiceProviders.*) → id; наружу эти id не уходят никогда
public sealed record AudioVoiceUse(
    string Slug,
    AudioBytes? Sample,
    string? Transcript,
    byte[]? RvcModel,
    byte[]? RvcIndex,
    IReadOnlyDictionary<string, string> Cached)
{
    public bool IsRvc => RvcModel is not null && RvcIndex is not null;

    public string? CachedId(string key) => Cached.TryGetValue(key, out var id) ? id : null;
}

// Привязка, созданная поставщиком в этом запуске: Provider — ключ кеша (VoiceProviders.*), Id — значение
public sealed record AudioVoiceCacheEntry(string Provider, string Id);

public static class AudioVoiceRefs
{
    public const string Prefix = "voice:";

    // «voice:anya» → «anya»; null — значение не из библиотеки
    public static string? SlugOf(string? value) =>
        value is not null && value.Trim() is var v && v.StartsWith(Prefix, StringComparison.Ordinal) && v.Length > Prefix.Length
            ? v[Prefix.Length..]
            : null;
}
