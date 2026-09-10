using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов PersonaManager для выноса Turn (Этап 5). PersonaLayerContributor зовёт
// только `Get(id, userId)` — резолв персоны по id с проверкой владельца (нужна для
// группового чата и онбординга: чат идёт от её лица, а чужая персона — null).
//
// Не дубль:
//   - `IPersonaLookup.GetByIdInternal(id)` — без проверки владельца, для внутренних
//     сервисов (Spend читает паспорт хода по чужому id);
//   - `IPersonaDirectory.GetByOwner/ Delete` — для каскада жизненного цикла
//     владельца (Knowledge чистит персоны при удалении пользователя).
//
// Склейка дала бы читающему контрибьютору доступ к Delete (Directory) или сняла бы
// проверку доступа (Lookup) — оба варианта расширяют права ради экономии одного
// файла. Разделение осознанное — прецедент в Spend.
//
// Сигнатура минимальная: один метод, ровно то, что использует контрибьютор.
public interface IPersonaResolver
{
    // Персона по id с проверкой владельца. null — нет такой или чужой.
    Persona? Get(string id, string userId);
}
