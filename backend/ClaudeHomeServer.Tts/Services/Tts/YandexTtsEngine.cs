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

    private static readonly IReadOnlyList<TtsEngineVoice> VoiceList =
        TtsVoiceCatalog.All.Select(v => new TtsEngineVoice(v.Voice, v.Label, v.Roles)).ToArray();

    private static readonly IReadOnlyList<string> RoleList =
        VoiceList.SelectMany(v => v.Roles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool Configured => tts.IsConfigured;

    public IReadOnlyList<TtsEngineVoice> Voices => VoiceList;

    public IReadOnlyList<string> Roles => RoleList;

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

        if (double.IsNaN(speed) || speed < MinSpeed || speed > MaxSpeed)
            return TtsSynthesis.Fail($"Скорость {speed} вне пределов {MinSpeed}–{MaxSpeed}.");

        var result = await tts.SynthesizeAsync(text, new VoiceChoice(name, wantedRole, speed), ct);
        if (result.Audio is not null) return new TtsSynthesis(result.Audio, result.Rub, null);

        // Подробная причина отказа Яндекса уже в логе сервиса; уже оплаченное едет вызывающему
        return TtsSynthesis.Fail(ct.IsCancellationRequested
            ? "Синтез отменён."
            : "Яндекс не озвучил текст: сервис недоступен или отверг запрос.", result.Rub);
    }
}
