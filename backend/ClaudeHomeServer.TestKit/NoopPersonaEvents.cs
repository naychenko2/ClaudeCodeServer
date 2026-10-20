using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Tests.Helpers;

// Тестовая no-op реализация IPersonaEvents: PersonaMemoryService подписывается
// на OnPersonaHandleChanged для переименования Dify-датасета. В юнит-тестах без
// реального Dify (Knowledge не настроена) подписка всё равно безопасна —
// обработчик пустой.
//
// Создан ради выноса Memory в отдельный .csproj (Этап 5): замена прямой ссылки
// на PersonaManager.OnPersonaHandleChanged в PersonaMemoryService потребовала
// нового конструктора с IPersonaEvents — в тестах проще no-op, чем поднимать
// реальный PersonaEventsImpl с его кэшем обёрток и подпиской на событие.
//
// Public ради доступа из других тестовых проектов (Knowledge.Tests, Turn.Tests):
// шов IPersonaEvents — публичный контракт Core, его реализация не может быть internal,
// иначе тесты других вертикалей не соберутся. No-op тестовый класс — единый хелпер.
public sealed class NoopPersonaEvents : IPersonaEvents
{
    public void AttachOnHandleChanged(Action<Persona, string> handler) { /* no-op */ }
    public void DetachOnHandleChanged(Action<Persona, string> handler) { /* no-op */ }
}

// Тестовая no-op реализация IDifyMetrics: MemoryDify.DiffSyncAsync принимает
// метрику параметром (Этап 5, шов IDifyMetrics). В юнит-тестах без Dify
// дифф-синк не идёт (Available == false), метрика не нужна; передаём
// no-op, чтобы сигнатура не раздваивалась.
//
// Public ради доступа из других тестовых проектов.
public sealed class NoopDifyMetrics : IDifyMetrics
{
    public void RecordSyncError(string reason) { /* no-op */ }
}
