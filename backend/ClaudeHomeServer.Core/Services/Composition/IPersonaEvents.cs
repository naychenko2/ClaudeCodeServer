using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Шов событий PersonaManager (Этап 5, сцепка Memory + Dossiers). Подписка
// на `OnPersonaHandleChanged` нужна PersonaMemoryService — при переименовании
// хэндла персоны переименовать Dify-датасет её памяти. Прямой доступ
// к PersonaManager.OnPersonaHandleChanged запрещён в вынесенных вертикалях.
//
// НЕ дубль IPersonaLookup/Directory/Resolver: те про точечное чтение/каскад
// жизненного цикла. События — push-канал: другая семантика, склейка расширила
// бы права читающего контрибьютора (Lookup/Resolver не должны отдавать подписку).
//
// Прецедент — `ISessionMessageObserver`: тот же паттерн Attach/Detach для
// SessionManager.OnSessionMessage, реализация прячет кэш обёрток от гонки
// GetOrAdd (см. SessionMessageObserver).
public interface IPersonaEvents
{
    // Подписаться на изменение handle персоны: при переименовании хэндла —
    // нулевое состояние не вызывается, только смена. Если handler не
    // зарегистрирован — тихий no-op.
    void AttachOnHandleChanged(Func<Persona, string, Task> handler);

    // Отписаться.
    void DetachOnHandleChanged(Func<Persona, string, Task> handler);
}
