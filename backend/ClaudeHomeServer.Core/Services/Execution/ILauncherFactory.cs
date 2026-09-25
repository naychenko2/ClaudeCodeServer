using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Execution;

// Резолв драйвера среды исполнения по владельцу процесса или по проекту.
// Контракт в Core: вертикали (Skills/ProjectServices/Terminal/Deploy/…) держат ссылку
// только на интерфейс, а конкретная фабрика (с UserStore/SandboxManager) живёт в Main.
public interface ILauncherFactory
{
    // Локальная среда — для системных вызовов бэкенда (каталог моделей, changelog)
    IProcessLauncher Local { get; }
    // Среда владельца: container-пользователь → docker-песочница; null/неизвестный → local.
    // Только для мест БЕЗ проекта (системные one-shot, чат вне проекта, реестр MCP владельца):
    // в проектном контексте — ForProject, иначе ход локального проекта уедет на сервер
    // (сторож G8, LauncherProjectContextGuardTests)
    IProcessLauncher ForOwner(string? ownerId);
    // Среда проекта (ADR-016): проект на устройстве → исполнение на устройстве владельца,
    // серверный проект → среда владельца, как ForOwner. Неготовое устройство — отказ при
    // Start (DeviceExecRefusedException с текстом для человека), не при резолве
    IProcessLauncher ForProject(Project project);
}
