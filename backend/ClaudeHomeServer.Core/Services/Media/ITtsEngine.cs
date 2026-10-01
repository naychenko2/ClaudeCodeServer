namespace ClaudeHomeServer.Services.Media;

// Шов «облачный синтез речи» для модуля «Звук» (ADR-021, §2 «Швы в Core»): Яндекс SpeechKit живёт
// в вертикали Tts, модуль на неё не ссылается. Реализация — адаптер в Tts поверх того же сервиса,
// что озвучивает голосовой режим чата; нет подсистемы tts — нет и регистрации.
//
// Шов байтовый: на выходе mp3 целиком, в папку проекта он ничего не пишет. Отказы — значением
// с причиной, а не исключением: модулю нужна строка для человека и рубли за уже принятые запросы.
public interface ITtsEngine
{
    // Ключ и каталог SpeechKit заданы. Живость Яндекса не проверяется — её покажет сам синтез
    bool Configured { get; }

    // Голоса с их амплуа; Voice — каноническое имя, его и передавать в SynthesizeAsync
    IReadOnlyList<TtsEngineVoice> Voices { get; }

    // Все амплуа, которые знает движок; какой голос что тянет — в Voices
    IReadOnlyList<string> Roles { get; }

    // Предел длины текста одного синтеза, в символах
    int MaxChars { get; }

    double MinSpeed { get; }
    double MaxSpeed { get; }

    // role — null или пусто: нейтрально. Незнакомый голос, чужое голосу амплуа, скорость вне
    // [MinSpeed; MaxSpeed] и текст длиннее MaxChars — отказ ДО запроса к поставщику, без оплаты
    Task<TtsSynthesis> SynthesizeAsync(string text, string voice, string? role, double speed, CancellationToken ct);
}

public sealed record TtsEngineVoice(string Voice, string Label, IReadOnlyList<string> Roles);

// Audio — mp3 (null при отказе). Rub — за запросы, которые поставщик уже принял: тарификация идёт
// за запрос, и при обрыве на середине оплаченное не пропадает, поэтому Rub бывает и у отказа
public sealed record TtsSynthesis(byte[]? Audio, double Rub, string? Error)
{
    public const string ContentType = "audio/mpeg";
    public const string Extension = ".mp3";

    public static TtsSynthesis Fail(string error, double rub = 0) => new(null, rub, error);
}
