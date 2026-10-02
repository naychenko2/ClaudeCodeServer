using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Media;

// Шов «локальные аудио-модели» для модуля «Звук» (ADR-021, §2 «Швы в Core»): движок local-media
// (ComfyUI на своей GPU, узел CcsAudioWorker) живёт в вертикали Images, модуль на неё не ссылается.
// Реализация — адаптер в Images; нет подсистемы images — нет и регистрации.
//
// Шов байтовый: входы — байты звука, образцов и модели голоса, выход — байты файлов с ролями.
// В папку проекта он ничего не пишет, граф — только из шаблонов ComfyWorkflows. Ticket — prompt_id
// ComfyUI: он приходит только от драйвера в процессе, снаружи его не передают.
public interface ILocalAudioMedia
{
    // Тумблеры LocalMedia:Enabled и LocalMedia:AudioEnabled включены и ComfyUI отвечал недавно.
    // Не блокирует: проверка живости кешируется
    bool Available { get; }

    // Оба тумблера включены — поставщик заведён, даже если ComfyUI сейчас лежит
    bool Configured { get; }

    // Длина общей очереди ComfyUI (идущая плюс ждущие, с чужими прогонами); null — недоступен
    Task<int?> QueueLengthAsync(CancellationToken ct);

    // Ожидаемое время запуска по таблице замеров local-media; null — не замерено или запрос неверен
    int? EtaSeconds(LocalAudioRequest request);

    Task<LocalAudioSubmitted> SubmitAsync(LocalAudioRequest request, CancellationToken ct);

    Task<LocalAudioPoll> PollAsync(string ticket, CancellationToken ct);

    // Снять задачу: ждущую — из очереди, идущую — прервать адресно по её prompt_id (чужой прогон
    // не задевается). Задачи в очереди уже нет — false
    Task<bool> CancelAsync(string ticket, CancellationToken ct);
}

// Операции — те же, что у инструментов local-media (local_speech, local_music_generate …)
public enum LocalAudioOp
{
    Speech,
    MusicGenerate,
    MusicEdit,
    VoiceConvert,
    VoiceTrain,
    Separate,
    ToMidi,
    Enhance,
    Transcribe,
}

// Prompt — стиль и содержание у музыки и правки трека. Args — параметры операции под теми же
// именами, что у инструментов local-media (text, engine, language, voice, speaker, task, track,
// mode, lyrics …); белые списки и пределы проверяет реализация. Ссылки на входы (audio,
// reference, audios, voice_model, voice_index) в Args не передаются — их место занимают байты:
// Audio — исходный звук, Reference — образец голоса или трек-эталон мастеринга, Clips — записи
// для обучения голоса, VoiceModel/VoiceIndex — .pth и .index для RVC
public sealed record LocalAudioRequest(
    LocalAudioOp Op,
    string? Prompt = null,
    JsonObject? Args = null,
    byte[]? Audio = null,
    byte[]? Reference = null,
    IReadOnlyList<byte[]>? Clips = null,
    byte[]? VoiceModel = null,
    byte[]? VoiceIndex = null,
    long? Seed = null);

// Busy — очередь GPU занята или ComfyUI не ответил: это «попробуй позже», а не ошибка запроса
public sealed record LocalAudioSubmitted(string? Ticket, int? QueuePosition, int? EtaSeconds, string? Error, bool Busy = false)
{
    public static LocalAudioSubmitted Fail(string error, bool busy = false) => new(null, null, null, error, busy);
}

public enum LocalAudioState { Queued, Running, Completed, Failed }

// Warning — временный сбой опроса (ComfyUI не ответил), задача при этом жива
public sealed record LocalAudioPoll(
    LocalAudioState State,
    int? QueuePosition,
    IReadOnlyList<LocalAudioFile> Files,
    string? Error,
    string? Warning = null);

// Role — из LocalAudioRoles; Extension — с точкой («.mp3», «.srt»)
public sealed record LocalAudioFile(string Role, byte[] Bytes, string ContentType, string Extension);

// Роли файлов версии звука (ADR-021, §2)
public static class LocalAudioRoles
{
    public const string Main = "main";
    public const string StemPrefix = "stem:";
    public const string Score = "score";
    public const string Subtitles = "subtitles";
    public const string Lyrics = "lyrics";
    public const string Text = "text";
    public const string Midi = "midi";
    public const string Model = "model";
    public const string Index = "index";

    public static string Stem(string name) => StemPrefix + name;
}
