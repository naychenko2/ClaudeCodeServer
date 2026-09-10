using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Шов событий PersonaManager (Этап 5, сцепка Memory + Dossiers). Подписка
// на `OnPersonaHandleChanged` нужна PersonaMemoryService — при переименовании
// хэндла персоны переименовать Dify-датасет её памяти. Прямой доступ
// к PersonaManager.OnPersonaHandleChanged запрещён в вынесенных вертикалях.
//
// Сигнатура `Action<Persona, string>` — намеренно совпадает с источником события
// (PersonaManager.OnPersonaHandleChanged): вызов fire-and-forget, синхронная
// подписка без возвращаемого значения. Адаптация к Func<Persona, string, Task>
// заставила бы контрибьютора упаковывать «Task.CompletedTask» в каждой лямбде
// и забывать await (типичная ошибка fire-and-forget) — расширять контракт
// только ради унификации с ISessionMessageObserver, чьё событие действительно
// асинхронно (обработчик может ждать I/O), нерационально.
//
// НЕ дубль IPersonaLookup/Directory/Resolver: те про точечное чтение/каскад
// жизненного цикла. События — push-канал: другая семантика, склейка расширила
// бы права читающего контрибьютора (Lookup/Resolver не должны отдавать подписку).
public interface IPersonaEvents
{
    // Подписаться на изменение handle персоны: при переименовании хэндла —
    // нулевое состояние не вызывается, только смена. Если handler не
    // зарегистрирован — тихий no-op.
    void AttachOnHandleChanged(Action<Persona, string> handler);

    // Отписаться.
    void DetachOnHandleChanged(Action<Persona, string> handler);
}
