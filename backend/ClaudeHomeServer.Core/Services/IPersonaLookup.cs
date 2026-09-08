using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов PersonaManager для выноса Spend (Этап 5). Spend использует только
// `GetByIdInternal` — резолв персоны по id для отображения имени в pivot-узлах
// и паспорте хода (формат «Role (Name)»). Внешний `GetById` отфильтровывает
// неактивные персоны; Spend интересуют ВСЕ, включая архивные, поэтому берём
// именно `GetByIdInternal` — обращение напрямую к словарю реестра, минуя
// проверки доступа. Полный PersonaManager не нужен.
public interface IPersonaLookup
{
    // Возвращает персону по id или null, если такой нет. Доступ не проверяется —
    // это чтение внутреннего реестра, а не публичный API персон.
    Persona? GetByIdInternal(string id);
}
