namespace ClaudeHomeServer.Services.Llm;

// Секция Transcription (вкладка «Встречи», Б1): путь к конвейеру транскрибации на этом узле.
// Значение по умолчанию — установка на worker (тот же приём, что у LocalModelStatsOptions.Gddr6HelperFile:
// машинный путь живёт в дефолте опций). На другом узле переопределяется Transcription:PipelineRoot
// в appsettings.Local.json — в отслеживаемые appsettings его не пишем.
public sealed class TranscriptionOptions
{
    public const string Section = "Transcription";

    // Корень конвейера: под ним logs/state.json, logs/summary_state.json, logs/journal.tsv и config.yaml.
    public string PipelineRoot { get; set; } = "/home/an/services/transcription-pipeline";
}
