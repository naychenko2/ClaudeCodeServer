using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Шов PersonaManager для выноса Images (Этап 5): догоняющая генерация аватара
// (ImageBackfillService) зовёт два члена:
//   - `AssetsDir` — путь к каталогу аватаров персоны (data/personas/);
//   - `SetAvatarImage(id, userId, imageFile)` — перевод аватара в режим картинки.
//
// НЕ дубль:
//   - `IPersonaLookup.GetByIdInternal(id)` — точечное чтение по id (без владельца);
//   - `IPersonaResolver.Get(id, userId)` — чтение с проверкой владельца;
//   - `IPersonaDirectory.GetByOwner/Delete` — каскад жизненного цикла владельца.
// Этот шов — запись: «положить файл аватара и переключить режим персоны».
public interface IPersonaAvatarStore
{
    // Каталог ассетов персон (аватаров): data/personas/
    string AssetsDir { get; }

    // Перевести аватар персоны в режим картинки (Kind=Image, ImageFile=fileName).
    // Удаляет прежний avatar-файл и original. Бросает KeyNotFoundException, если персона
    // не найдена или принадлежит чужому владельцу.
    Persona SetAvatarImage(string id, string userId, string imageFile);
}
