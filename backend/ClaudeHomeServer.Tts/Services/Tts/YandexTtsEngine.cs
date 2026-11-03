using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Tts;

// Адаптер шва ITtsEngine для модуля «Звук» (ADR-021, §2) поверх YandexTtsService: нарезка под лимит
// запроса, разбор ответа и учёт оплаченных запросов — там, здесь только проверка входа.
//
// В отличие от VoiceResolver голосового режима, здесь кривой вход — отказ, а не тихий дефолт:
// человек выбрал голос и амплуа в операции явно, и подменить их молча значит отдать ему не то,
// за что он заплатил.
public sealed class YandexTtsEngine(YandexTtsService tts) : ITtsEngine
{
    // Предел одной операции модуля; запросы SpeechKit по 249 символов нарезает сам сервис
    public const int MaxTextChars = 3000;

    private readonly IReadOnlyList<TtsEngineVoice> _voices =
        TtsVoiceCatalog.All
            .Select(v => new TtsEngineVoice(v.Voice, v.Label, v.Roles, v.Model, TtsVoiceCatalog.SupportsSpeed(v.Voice)))
            .ToArray();

    private IReadOnlyList<string>? _roles;

    public bool Configured => tts.IsConfigured;

    public IReadOnlyList<TtsEngineVoice> Voices => _voices;

    public IReadOnlyList<string> Roles =>
        _roles ??= _voices.SelectMany(v => v.Roles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public int MaxChars => MaxTextChars;

    public double MinSpeed => VoiceResolver.MinSpeed;

    public double MaxSpeed => VoiceResolver.MaxSpeed;

    public async Task<TtsSynthesis> SynthesizeAsync(string text, string voice, string? role, double speed,
        CancellationToken ct)
    {
        if (!Configured) return TtsSynthesis.Fail("Синтез Яндекса не настроен: нет ключа или каталога SpeechKit.");
        if (string.IsNullOrWhiteSpace(text)) return TtsSynthesis.Fail("Пустой текст.");
        if (text.Length > MaxTextChars)
            return TtsSynthesis.Fail($"Текст длиннее {MaxTextChars} символов ({text.Length}).");

        var name = TtsVoiceCatalog.Canonical(voice);
        if (name is null) return TtsSynthesis.Fail($"Голос «{voice}» Яндексу незнаком.");

        var wantedRole = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        if (wantedRole is not null && !TtsVoiceCatalog.SupportsRole(name, wantedRole))
            return TtsSynthesis.Fail($"Голос «{name}» не умеет амплуа «{wantedRole}».");

        // Скорость проверяем только у голосов со скоростью: LiveTTS её не принимает, и BuildPayload
        // её не отправит, так что отказ по ней был бы отказом ни за что
        if (TtsVoiceCatalog.SupportsSpeed(name) && (double.IsNaN(speed) || speed < MinSpeed || speed > MaxSpeed))
            return TtsSynthesis.Fail($"Скорость {speed} вне пределов {MinSpeed}–{MaxSpeed}.");

        var result = await tts.SynthesizeAsync(text, new VoiceChoice(name, wantedRole, speed), ct);
        if (result.Audio is not null) return new TtsSynthesis(result.Audio, result.Rub, null);

        // Подробная причина отказа Яндекса уже в логе сервиса; уже оплаченное едет вызывающему
        return TtsSynthesis.Fail(ct.IsCancellationRequested
            ? "Синтез отменён."
            : "Яндекс не озвучил текст: сервис недоступен или отверг запрос.", result.Rub);
    }
}
