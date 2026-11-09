namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Параметры прогона для журнала истории: белый список (width/height/durationSec/frames/steps/
// seed/count/engine/fast). Произвольный JsonObject из запроса не пишем — только эти поля
public sealed record LocalRunParams(
    int? Width = null,
    int? Height = null,
    int? DurationSec = null,
    int? Frames = null,
    int? Steps = null,
    long? Seed = null,
    int? Count = null,
    string? Engine = null,
    bool? Fast = null);

// Детали прогона для журнала истории: модель, промпт (обрезанный до 4 КБ) и параметры (белый
// список). Промпт обрезаем в конструкторе: длинный текст не должен раздувать строку журнала
public sealed record LocalRunDetails
{
    // Потолок промпта в журнале: 4 КБ
    public const int MaxPromptLength = 4096;

    public string? Model { get; }
    public string Prompt { get; }
    public LocalRunParams? Params { get; }

    public LocalRunDetails(string? model, string prompt, LocalRunParams? @params = null)
    {
        Model = model;
        Prompt = prompt is { Length: > MaxPromptLength } p ? p[..MaxPromptLength] : prompt;
        Params = @params;
    }
}
