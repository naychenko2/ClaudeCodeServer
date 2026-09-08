namespace ClaudeHomeServer.Services.Composition;

// Реализация шва IUserStore (Core) поверх корневого UserStore в Main.
// Полный `UserStore` содержит ~25 методов (жизненный цикл, токены, аватары,
// права, фич-флаги, настройки пользователя); отдельные .csproj (Knowledge,
// Notes) видят через шов только 3 метода чтения (GetById/GetAll +
// IsTokenVersionCurrent для Desktop).
//
// Полный `User` возвращается как есть: NotesKnowledgeService читает Username,
// KnowledgeBaseCatalogService — UserName/Role, новые потребители будут
// читать поля по необходимости. Вводить DTO на этом шаге — лишняя работа
// (потребует перелопатить несколько мест, где сейчас идёт прямое чтение).
// Если в будущем понадобится узкая форма — расширим шов, сейчас контракт
// узкий по числу методов, а не по форме возвращаемого типа.
//
// Раньше IUserStore регистрировался фабрикой `sp => sp.GetRequiredService<UserStore>()`,
// что неявно делало один объект интерфейсом двух типов. Явный класс-адаптер:
//   - даёт точку для подмены в тестах (мутационная проверка адаптера);
//   - формализует, что IUserStore — это узкая проекция UserStore
//     (только 3 из ~25 публичных методов), а не синоним.
public sealed class UserStoreAdapter : IUserStore
{
    private readonly UserStore _users;

    public UserStoreAdapter(UserStore users)
    {
        _users = users;
    }

    public Models.User? GetById(string id) =>
        _users.GetById(id);

    public IReadOnlyList<Models.User> GetAll() =>
        _users.GetAll();

    public bool IsTokenVersionCurrent(string userId, int version) =>
        _users.IsTokenVersionCurrent(userId, version);
}
