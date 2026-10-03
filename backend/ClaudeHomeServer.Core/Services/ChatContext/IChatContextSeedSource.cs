namespace ClaudeHomeServer.Services.ChatContext;

// Засев контекста из фокусов вертикалей (ADR-023, фаза 1): у чата без файла контекста основной объект
// производный — берётся у первой вертикали (по Priority), у которой есть фокус. Реализует тот же класс,
// что и IContextKindProvider вертикали: AddContextKindProvider<T>() регистрирует и этот шов.
// Читает только своё хранилище, ничего не пишет.
public interface IChatContextSeedSource
{
    // Меньше — раньше: картинка (0) выигрывает у звука (1)
    int SeedPriority { get; }

    // Основной объект из фокуса вертикали или null, если фокуса нет
    ContextItem? SeedPrimary(ContextScope scope);
}
