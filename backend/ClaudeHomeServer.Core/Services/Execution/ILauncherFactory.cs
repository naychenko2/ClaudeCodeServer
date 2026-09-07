namespace ClaudeHomeServer.Services.Execution;

// Резолв драйвера среды исполнения по владельцу процесса.
// Контракт в Core: вертикали (Skills/ProjectServices/Terminal/Deploy/…) держат ссылку
// только на интерфейс, а конкретная фабрика (с UserStore/SandboxManager) живёт в Main.
public interface ILauncherFactory
{
    // Локальная среда — для системных вызовов бэкенда (каталог моделей, changelog)
    IProcessLauncher Local { get; }
    // Среда владельца: container-пользователь → docker-песочница; null/неизвестный → local
    IProcessLauncher ForOwner(string? ownerId);
}
