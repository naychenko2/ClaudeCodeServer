namespace ClaudeHomeServer.Services.Media;

// Метка происхождения прогона ComfyUI: кто запустил (OwnerId), из какой сессии (SessionId,
// null — не из чата), вид (Kind: image | video | audio | train | other) и операция (Op),
// тяжёлый ли прогон (Heavy). Внутренний тип — в DTO наружу не включается; OwnerId
// обезличивает Main, REST его не отдаёт.
//
// Связь с источником (шаг 2, журнал истории): ProjectId — проект, Source — откуда поставлен
// (local-media | image-editor | audio-editor | video-editor | lora), SourceJobId — job_id
// local_* или id задачи редактора. Все необязательные: у старых прогонов и в тестах — null.
public sealed record LocalJobOrigin(
    string OwnerId,
    string? SessionId,
    string Kind,
    string Op,
    bool Heavy,
    string? ProjectId = null,
    string Source = "local-media",
    string? SourceJobId = null)
{
    // Источники прогона: инструменты local-media, три редактора, обучение LoRA
    public const string SourceLocalMedia = "local-media";
    public const string SourceImageEditor = "image-editor";
    public const string SourceAudioEditor = "audio-editor";
    public const string SourceVideoEditor = "video-editor";
    public const string SourceLora = "lora";
}
