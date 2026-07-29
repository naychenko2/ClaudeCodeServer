namespace ClaudeHomeServer.Models;

// Модель документации проекта для панели «Доки»: README.md в корне + docs/**/*.md.
// Enum'ы сериализуются строками (JsonStringEnumConverter с camelCase в Program.cs),
// поэтому на фронте kind приходит как "doc" | "repo" | "external".

// Класс ссылки из документа:
//   Doc      — на другой документ области (навигация внутри панели);
//   Repo     — на файл проекта вне области, обычно код (открывается в центре);
//   External — http/https/mailto, в графе связей не участвует.
public enum DocLinkKind { Doc, Repo, External }

// Ссылка из документа. Target — путь от корня проекта с прямыми слэшами (Doc/Repo)
// либо исходный URL (External). Anchor — слаг после «#», уже нормализованный.
public record DocLink(string Target, string? Anchor, DocLinkKind Kind, string Text);

// Заголовок документа. Slug считается от текста, очищенного от markdown-разметки, —
// тот же контракт повторяет фронт (lib/docsLinks.ts), иначе переход по якорю не найдёт цель.
public record DocHeading(int Level, string Text, string Slug);

// Документ в индексе. Содержимое отдельно (GET /doc): индекс должен оставаться лёгким,
// его перезапрашивают на каждое изменение файлов проекта.
public record DocEntry(string Path, string Title, DateTime Modified, long Size,
    IReadOnlyList<DocHeading> Headings);

// Входящая ссылка: кто ссылается на документ и на какой его якорь
public record DocBacklink(string Path, string Title, string? Anchor);

// Документ целиком: исходный markdown + связи в обе стороны
public record DocDetail(string Path, string Title, string Content,
    IReadOnlyList<DocLink> Links, IReadOnlyList<DocBacklink> Backlinks);

// Совпадение поиска. Slug — якорь ближайшего заголовка над совпадением (null для
// совпадения в заголовке документа или до первого подзаголовка).
public record DocSearchHit(string Path, string Title, string? Slug, string Snippet);

// Кандидат в папки документации: путь от корня проекта, сколько .md внутри (рекурсивно)
// и есть ли папка на диске (выбранная, но удалённая папка остаётся в списке — иначе
// галка молча пропала бы, и причина пустого корпуса была бы не видна).
public record DocFolderCandidate(string Path, int Count, bool Exists);

// Настройка области документации: что выбрано сейчас, что можно выбрать и что было бы
// по умолчанию (кнопка «вернуть docs/» на фронте строится из Defaults, а не хардкодом).
public record DocsFoldersInfo(IReadOnlyList<string> Selected, IReadOnlyList<DocFolderCandidate> Candidates,
    IReadOnlyList<string> Defaults);
