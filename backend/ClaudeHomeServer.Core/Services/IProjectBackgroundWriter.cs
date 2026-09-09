using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Шов для вертикали Backgrounds (Этап 5, волна C, шаг 2): ProjectBackgroundService
// в отдельной сборке пишет фон/цвет проекта, и пять методов `ProjectManager` плюс
// `BackgroundsDir` (виртуальный путь стора тайлов) выделены в узкий интерфейс.
// Прежде Backgrounds прямо ссылался на полный `ProjectManager` из Main, что делало
// вынос невозможным — пятая сторона цикла, лишний допуск в стороже границ.
//
// Контракт минимальный: ровно те шесть операций, что вызывает ProjectBackgroundService
// сегодня. Расширять под новых потребителей — отдельным швом; держать "полный
// ProjectManager" здесь — анти-паттерн (он раздует вертикаль и сломает сторож).
//
// Реализация-форвардер `ProjectBackgroundWriterAdapter` живёт в Main рядом с
// `ProjectManager.cs` — один метод-форвард на пункт, без своей логики.
public interface IProjectBackgroundWriter
{
    // Захват фона под генерацию (ADR-008 §10): возвращает false, если проект
    // уже в свежем Pending или не подходит под `candidatesOnly` отбор.
    bool TryBeginBackground(string id, bool candidatesOnly);

    // Успешная генерация: ссылка на новый тайл, прежний удаляется.
    Project SetBackgroundGenerated(string id, string tileFile);

    // Сброс на стандартный фон (кнопка «Вернуть стандартный»).
    Project SetBackgroundStandard(string id);

    // Прогон провалился (no-model/bad-json/io/...) — фиксируется причина,
    // автопрогон проект больше не берёт.
    Project SetBackgroundFailed(string id, string reason);

    // Точечная мутация цвета (Backgrounds использует только поле `color`,
    // остальные аргументы null). Префикс не открываем — вертикаль получает
    // только то, что ей реально нужно.
    Project Update(string id, string? name, string? rootPath, string? color);

    // Каталог тайлов фона (`<DataDir>/project-backgrounds/{projectId}/`) —
    // InternalStorePath проектов, рядом с projects.json. Используется
    // `ProjectBackgroundService.WriteTileAsync` для записи нового svg-тайла.
    string BackgroundsDir { get; }
}
