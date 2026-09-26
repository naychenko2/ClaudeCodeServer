using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ImageEditor;

// Всё, что чату картинки нужно от ядра сессий (ADR-018 §1, §10.1). Ручки чатов живут в модуле
// редактора, а создавать сессии и менять их поля умеет только SessionManager в Main: модуль
// типов Main не видит, поэтому ходит сюда. Реализация — адаптер над SessionManager в Main.
public interface IImageChatSessions
{
    // Новый чат картинки проекта с явным именем «hero.png · правка». Сообщений не шлёт: чат
    // создаётся по первому сообщению, само сообщение фронт отправляет следом через хаб.
    // Собеседник: personaId из композера, иначе руководитель проекта, иначе личный ассистент.
    // sourcePath уже проверен модулем: внутри проекта, файл существует.
    Task<ImageChatCreateOutcome> CreateAsync(string ownerId, Project project, string sourcePath,
        string? personaId, CancellationToken ct);

    // Перепривязать чат картинки к файлу: прежний путь уходит в Lineage. Настройка, а не
    // активность — UpdatedAt не двигается, чат не поднимается в списке и не выходит из архива.
    // null — чата нет или он не чат картинки. Владение и проект проверяет вызывающий.
    Session? SetPath(string sessionId, string path);

    // Редактор сохранил картинку в новый файл, и чат идёт за ним: смена пути как в SetPath плюс
    // запись image_file_moved в ленту. Запись — активность, она двигает UpdatedAt.
    // null — чата нет или он не чат картинки. Владение и проект проверяет вызывающий.
    Task<Session?> MoveToFileAsync(string sessionId, string path);

    // Файл переименовали или перенесли мимо редактора: пути переписываются целиком, Lineage не
    // растёт. UpdatedAt не двигается, в ленту ничего не пишется.
    Session? RewritePaths(string sessionId, string currentPath, IReadOnlyList<string> lineage);

    // Человек запустил генерацию из редактора этого чата: тихая строка image_launch в ленту.
    // Модель её не видит (история, а не транскрипт CLI) — о запуске ход узнаёт из блока
    // состояния редактора. Запись — активность, она двигает UpdatedAt.
    // null — чата нет или он не чат картинки. Владение и проект проверяет вызывающий.
    Task<Session?> AppendLaunchAsync(string sessionId, Protocol.StoredImageLaunchMessage launch);
}

// Session — созданный чат; иначе ErrorCode + Error (коды — строки REST редактора)
public sealed record ImageChatCreateOutcome(Session? Session, string? ErrorCode, string? Error)
{
    public const string InvalidRequest = "invalid_request";
    public const string Unavailable = "image_editor_unavailable";

    public static ImageChatCreateOutcome Ok(Session session) => new(session, null, null);
    public static ImageChatCreateOutcome Fail(string code, string error) => new(null, code, error);
}

public static class ImageChatDefaults
{
    // Инструменты MCP-сервера image-editor, которые чат картинки выполняет без карточки
    // разрешения (ADR-018 §2): цена видна в карточке запуска, а спрашивать «можно?» на каждый
    // вызов — шум. Имена обязаны совпадать со схемами тулсета модуля — это держит тест модуля.
    public static readonly IReadOnlyList<string> AutoAllowTools =
    [
        "mcp__image-editor__image_generate",
        "mcp__image-editor__image_suggest_prompt",
    ];

    // Имя чата: «hero.png · правка». Явное — авто-заголовок его не перепишет
    public static string ChatName(string sourcePath) => $"{Path.GetFileName(sourcePath)} · правка";
}
