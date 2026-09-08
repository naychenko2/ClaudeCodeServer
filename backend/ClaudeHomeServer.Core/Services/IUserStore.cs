using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов UserStore для выноса Notes (Этап 5, волна E). NotesKnowledgeService
// использует только User.Username для построения имени Dify-датасета
// `{username}:notes` (UserStore.GetById(userId)?.Username ?? userId).
// Остальные методы UserStore (жизненный цикл, аватары, сессии, права) Notes
// не нужны — они остаются внутренностью Main.
public interface IUserStore
{
    // Возвращает пользователя по id или null, если такого нет.
    User? GetById(string id);

    // Возвращает всех пользователей. Используется KnowledgeBaseCatalogService
    // для классификации датасета: отличить «без префикса = глобальная»
    // от «чужая {otheruser}:…».
    IReadOnlyList<User> GetAll();
}
