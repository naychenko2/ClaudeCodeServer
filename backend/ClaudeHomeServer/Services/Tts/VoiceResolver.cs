using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tts;

// Чем озвучивать этот текст: ЕДИНСТВЕННАЯ точка склейки голоса, по образцу
// UserModelTierResolver.ModelFor. Дублировать цепочку по вызывающим нельзя — иначе
// «голос персоны» и «голос инстанса» разъедутся между чатом, петлей разговора и превью.
//
// Цепочка: голос персоны → голос из конфига инстанса. Пользовательского «голоса по
// умолчанию» здесь пока нет — его негде задать, звено появится вместе с настройкой.
//
// Отказов не бывает: любое кривое значение (незнакомый голос, чужая роль, скорость 0,
// протухший personaId) молча вырождается в дефолт. Причина — в поведении фронта: 400
// или 502 уводят ОСТАТОК фразы на голос браузера, то есть опечатка в сторе стоила бы
// человеку куска озвучки, а не одной неверной интонации.
public class VoiceResolver(PersonaManager personas, IConfiguration config, ILogger<VoiceResolver> logger)
{
    // Границы SpeechKit: вне их запрос отвергается с 400
    private const double MinSpeed = 0.1;
    private const double MaxSpeed = 3.0;

    // Дефолты инстанса читаются один раз: сервис Singleton, поэтому предупреждение об
    // опечатке в конфиге печатается на старте, а не на каждую фразу
    private readonly string _defaultVoice = ResolveConfiguredVoice(config["Yandex:SpeechKit:Voice"], logger);
    private readonly double _defaultSpeed = Clamp(config.GetValue<double?>("Yandex:SpeechKit:Speed") ?? 1.0);

    // Голос для озвучки текста в чате. personaId приходит от клиента и может быть чужим
    // или протухшим — Get проверяет владельца и отдаёт null, чего достаточно: берём дефолт
    public VoiceChoice Resolve(string? personaId, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(personaId)) return Default();
        var persona = personas.Get(personaId, ownerId);
        return persona?.Voice is null ? Default() : ForPersonaVoice(persona.Voice);
    }

    private VoiceChoice Default() => new(_defaultVoice, null, _defaultSpeed);

    private VoiceChoice ForPersonaVoice(PersonaVoice voice)
    {
        // Незнакомый голос (переименовали в SpeechKit, правили json руками) — дефолтный:
        // роль при этом теряет смысл, её набор у каждого голоса свой
        var name = TtsVoiceCatalog.IsKnown(voice.Voice) ? voice.Voice!.Trim() : _defaultVoice;
        var role = TtsVoiceCatalog.SupportsRole(name, voice.Role) ? voice.Role!.Trim() : null;
        var speed = voice.Speed is { } s ? Clamp(s) : _defaultSpeed;
        return new VoiceChoice(name, role, speed);
    }

    private static double Clamp(double speed) => Math.Clamp(speed, MinSpeed, MaxSpeed);

    private static string ResolveConfiguredVoice(string? configured, ILogger logger)
    {
        var voice = TtsVoiceCatalog.Resolve(configured);
        if (!string.IsNullOrWhiteSpace(configured) && !TtsVoiceCatalog.IsKnown(configured))
            logger.LogWarning("Голос синтеза «{Voice}» не значится среди голосов SpeechKit v3 — " +
                              "озвучка пойдёт голосом «{Fallback}». Проверь Yandex:SpeechKit:Voice.",
                configured, voice);
        return voice;
    }
}

// Что уходит в hints запроса синтеза: голос всегда есть, роль — только поддержанная
public record VoiceChoice(string Voice, string? Role, double Speed);
