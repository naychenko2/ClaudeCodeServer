using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов UserStore для выноса Notes (Этап 5, волна E). NotesKnowledgeService
// использует только User.Username для построения имени Dify-датасета
// `{username}:notes` (UserStore.GetById(userId)?.Username ?? userId).
// Остальные методы UserStore (жизненный цикл, аватары, сессии, права) Notes
// не нужны — они остаются внутренностью Main.
//
// Ф5: добавлен IsTokenVersionCurrent для Desktop (DevicePairingService при
// pairing сверяет версию токена веб-сессии — UserStore.IsTokenVersionCurrent).
// Desktop пока в Main, но метод попал в шов заблаговременно по факту вызова,
// а не «на будущее»: единственный потребитель уже сидит на UserStore, и
// перевод на IUserStore — задача отдельной уборки (сейчас в Desktop
// используются и другие методы UserStore, шов расширять под них — уже
// превышение 5–6 членов).
public interface IUserStore
{
    // Возвращает пользователя по id или null, если такого нет.
    User? GetById(string id);

    // Возвращает всех пользователей. Используется KnowledgeBaseCatalogService
    // для классификации датасета: отличить «без префикса = глобальная»
    // от «чужая {otheruser}:…».
    IReadOnlyList<User> GetAll();

    // Сверяет версию токена веб-сессии пользователя. Используется Desktop
    // (DevicePairingService): при pairing проверяем, что версия в куке
    // соответствует текущей — иначе токен устарел и pairing отклоняется.
    bool IsTokenVersionCurrent(string userId, int version);
}
