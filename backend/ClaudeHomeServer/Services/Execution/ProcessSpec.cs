using System.Text;

namespace ClaudeHomeServer.Services.Execution;

// Спецификация запуска процесса в среде исполнения пользователя.
// WorkingDirectory — всегда ХОСТОВЫЙ путь: драйвер среды сам переводит его
// в свой вид (IPathMapper) при запуске.
public sealed record ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    // Ключи, которые надо УБРАТЬ из унаследованного окружения (не выставить пустыми:
    // пустая строка для CLI неотличима от заданного значения). Нужно, чтобы системные
    // переменные машины не протекали в ход: глобальная ANTHROPIC_BASE_URL увела бы чат
    // «на Claude» к чужому эндпоинту вместе с токеном подписки. Применяется ДО Env,
    // так что осознанный оверрайд провайдера всегда побеждает.
    public IReadOnlyList<string>? ClearEnv { get; init; }
    public bool RedirectStdin { get; init; } = true;
    // Кодировка всех перенаправленных потоков (claude/skills — UTF-8 без BOM);
    // null — дефолт платформы (dev-серверы, терминал)
    public Encoding? StdioEncoding { get; init; }
    public bool EnableRaisingEvents { get; init; }
    // Метка убиваемости: в песочнице docker-клиент — лишь пайп к процессу,
    // и убить настоящий процесс можно только внутри контейнера — по этой метке
    public string? TurnId { get; init; }
    // Ставить ли процесс на учёт в ProcessRegistry (зачистка сирот при старте,
    // расстрел дерева при остановке приложения). Реестр нужен ДОЛГОЖИВУЩИМ процессам
    // (claude, терминалы, dev-серверы): они переживают смерть родителя на Windows.
    // Короткоживущим утилитам (git — таймаут 10 с, свой Kill по таймауту) он не нужен:
    // зачистка сирот и так фильтрует только claude/node, зато KillAll при остановке
    // ЛЮБОГО хоста расстреливает их на лету (в тестах хостов много — ловили флейки),
    // а PersistPids пишет PID-файл на каждый запуск.
    public bool Track { get; init; } = true;
}
