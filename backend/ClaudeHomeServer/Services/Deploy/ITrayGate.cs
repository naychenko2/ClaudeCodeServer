namespace ClaudeHomeServer.Services.Deploy;

/// <summary>
/// Связь с трей-раннером: жив ли он и как попросить его выкатить продукт.
///
/// Интерфейс существует не ради абстракции как таковой, а потому что реализация опирается на
/// именованные объекты ядра Windows, а бэкенд собирается и тестируется на Linux (CI). Здесь —
/// граница, за которой платформенный код, и точка подмены для тестов.
/// </summary>
public interface ITrayGate
{
    /// <summary>Трей жив и слушает событие с таким именем.</summary>
    bool IsAlive(string eventName);

    /// <summary>Просит трей выполнить действие. false — трея нет, никто команду не принял.</summary>
    bool Signal(string eventName);
}
