using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IPersonaEvents (Core) поверх PersonaManager.OnPersonaHandleChanged.
// Тонкая обёртка: подписывается на событие и пробрасывает (Persona, oldHandle) обработчику.
//
// Кэш обёрток обязателен по той же причине, что у SessionMessageObserver:
// иначе Detach не нашёл бы свой делегат в мультикасте (сравнение по Target+Method).
//
// Создан ради выноса Memory в отдельный .csproj (Этап 5): PersonaMemoryService
// переименовывает Dify-датасет памяти персоны при смене handle — единственный
// потребитель события. Прямая ссылка на PersonaManager.OnPersonaHandleChanged
// запрещена в вынесенной вертикали.
public sealed class PersonaEventsImpl(PersonaManager personas) : IPersonaEvents
{
    private readonly ConcurrentDictionary<Action<Persona, string>, Action<Persona, string>> _wrappers = new();
    private readonly object _attachLock = new();

    public void AttachOnHandleChanged(Action<Persona, string> handler)
    {
        // Лок сериализует пару «получить/создать обёртку + подписать» — иначе под
        // гонкой GetOrAdd мог бы сделать лишнюю обёртку, а подписка одной из
        // них утекла бы мимо словаря (Detach снимет не ту).
        lock (_attachLock)
        {
            if (_wrappers.ContainsKey(handler)) return;
            _wrappers[handler] = handler;
            personas.OnPersonaHandleChanged += handler;
        }
    }

    public void DetachOnHandleChanged(Action<Persona, string> handler)
    {
        if (_wrappers.TryRemove(handler, out var wrapper))
        {
            personas.OnPersonaHandleChanged -= wrapper;
        }
    }
}
