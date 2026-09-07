using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

// Драйвер среды исполнения процессов пользователя: local (машина сервера)
// или docker-песочница. Единственная точка, через которую бэкенд запускает
// процессы от имени пользователя (claude, терминал, dev-серверы, npx skills).
public interface IProcessLauncher
{
    bool IsSandboxed { get; }
    // ОС целевой среды — от неё зависит обвязка запуска (cmd.exe /c npx vs npx,
    // powershell vs pty-bridge). У песочницы всегда Linux независимо от хоста.
    bool TargetIsWindows { get; }
    IPathMapper Paths { get; }
    // Команда claude CLI в целевой среде
    string ClaudeCliCommand { get; }
    // Хостовый путь temp-каталога, ВИДИМОГО в целевой среде (one-shot cwd,
    // временные MCP-конфиги хода). Для локальной среды — обычный %TEMP%.
    string HostTempDir { get; }
    // URL API бэкенда, достижимый ИЗ целевой среды (для MCP *_API_URL);
    // null — среда видит Kestrel как localhost, обычная резолюция
    string? McpApiUrlOverride { get; }
    Process Start(ProcessSpec spec);
    // Останавливает процесс вместе с деревом потомков. В песочнице убийство
    // docker-клиента не трогает процесс в контейнере — драйвер добивает его по TurnId.
    void Kill(Process process, string? turnId = null);
    // ВЕРХНЯЯ оценка длины итоговой командной строки в широких символах, которую соберёт
    // .NET при ProcessStart по этому spec. Учитывает СВОЮ обвязку раннера: Local — ничего
    // (только cli+args), Docker — docker exec -i -w … cc-sandbox run-turn.sh turnId claude.
    // TurnPromptAssembler.ApplyBudget сравнивает результат с CmdlineLimit, чтобы стартовать
    // ход или отказать заранее с PromptOverflowException (ревью dc641949, волна 3).
    int EstimateCommandLineLength(ProcessSpec spec);
}
