using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Шов «разрешён ли серверный tool для владельца и персоны» — узкая часть контракта
// `PersonaBindingsService.ServerToolEnabled` (deny-only по Tool-привязке), нужная
// контрибьюторам секций промпта из чужих вертикалей (CodeGraph → `codegraph` ключ).
// НЕ тот же шов, что IMcpPersonaBindings (Services/Mcp/Http): там EffectiveToolEnabled
// (есть фолбэк на Persona.Tools), здесь ServerToolEnabled (deny-only, БЕЗ фолбэка).
//
// Без шва вертикаль CodeGraph тянула бы конкретный `PersonaBindingsService` (Main
// root) — это запрещено архитектурой (вертикаль не ссылается на root Services
// напрямую), и инверсия контрибьюторов промпта ради этого ровно и затевалась.
//
// Контракт узкий и стабильный: одна функция `IsServerToolEnabled(ownerId, persona,
// toolKey)` с deny-only семантикой (нет Off-привязки = true). Расширять под новые
// рубильники — отдельной задачей с новым швом, не множить методы здесь.
//
// Этап 5, шаг 6: forwarder регистрируется в Main (Program.cs), вызовы идут через
// `IPersonaServerToolGate`, реализация — `PersonaBindingsService` в root Services.
public interface IPersonaServerToolGate
{
    bool IsServerToolEnabled(string? ownerId, Persona? persona, string toolKey);
}