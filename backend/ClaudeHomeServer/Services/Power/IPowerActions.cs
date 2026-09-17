namespace ClaudeHomeServer.Services.Power;

/// <summary>Что можно сделать с машиной, на которой крутится бэкенд.</summary>
public enum PowerAction
{
    Shutdown,
    Restart,
    Sleep,
}

/// <summary>
/// Собственно команда машине. Интерфейс существует по той же причине, что и ITrayGate у
/// выкатки: за ним платформенный код (shutdown.exe и powrprof.dll), а бэкенд собирается и
/// тестируется на Linux. Здесь граница и точка подмены для тестов — исполнять команду в тестах
/// нельзя по очевидной причине.
/// </summary>
public interface IPowerActions
{
    /// <summary>Платформа умеет выполнять эти команды (сейчас — только Windows).</summary>
    bool Available { get; }

    /// <summary>Выполняет действие. false — команду отдать не удалось.</summary>
    bool Execute(PowerAction action);
}
