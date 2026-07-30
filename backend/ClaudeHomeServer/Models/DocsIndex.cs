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
// Binary — файл без текста (pdf, visio, картинка, аудио): он занимает своё место в
// документации, но заголовков, ссылок и поиска по телу у него нет, а открывается он
// только в центральной области.
public record DocEntry(string Path, string Title, DateTime Modified, long Size,
    IReadOnlyList<DocHeading> Headings, bool Binary = false);

// Входящая ссылка: кто ссылается на документ и на какой его якорь
public record DocBacklink(string Path, string Title, string? Anchor);

// Документ целиком: исходный markdown + связи в обе стороны.
// У бинарного (Binary) Content пуст — панель показывает вместо превью предложение
// открыть его в центре, где есть просмотрщики pdf/office/visio/картинок/звука.
public record DocDetail(string Path, string Title, string Content,
    IReadOnlyList<DocLink> Links, IReadOnlyList<DocBacklink> Backlinks, bool Binary = false);

// Совпадение поиска. Slug — якорь ближайшего заголовка над совпадением (null для
// совпадения в заголовке документа или до первого подзаголовка).
public record DocSearchHit(string Path, string Title, string? Slug, string Snippet);

// Кандидат в папки документации: путь от корня проекта, сколько файлов поддерживаемых
// типов внутри (рекурсивно, по ВСЕМ типам — иначе цифра врала бы, пока выбор типов
// редактируется) и есть ли папка на диске (выбранная, но удалённая остаётся в списке —
// иначе галка молча пропала бы, и причина пустого корпуса была бы не видна).
public record DocFolderCandidate(string Path, int Count, bool Exists);

// Кандидат в корневые файлы: имя файла и есть ли он на диске (тот же приём с выбранными,
// которых уже нет)
public record DocRootFileCandidate(string Name, bool Exists);

// Область документации: три независимые оси. Пустой список любой из них — «ничего отсюда».
// Types — ключи групп типов («markdown», «pdf», «visio»…), а не расширения: расширений
// три десятка, выбирают их всё равно группами, и хранить россыпь незачем.
// Home — документ, который панель показывает «Началом»; null — авто (README в корне).
public record DocsScope(
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> RootFiles,
    IReadOnlyList<string> Types,
    string? Home = null);

// Группа типов файлов для настройки. Расширений три десятка, и списком они не читаются —
// выбирают группами: «Markdown», «PDF», «Visio», «Аудио»…
// Text — содержимое разбирается в корпус (заголовки, ссылки, поиск); иначе файл только
// числится в списке и открывается в центре.
public record DocTypeGroup(string Key, string Title, IReadOnlyList<string> Extensions, bool Text);

// Документ области как вариант выбора (для «Начала»): путь и заголовок
public record DocOption(string Path, string Title);

// Настройка области: что выбрано, что можно выбрать и что было бы по умолчанию (кнопка
// «вернуть как было» на фронте строится из Defaults, а не хардкодом).
public record DocsScopeInfo(
    DocsScope Selected,
    IReadOnlyList<DocFolderCandidate> FolderCandidates,
    IReadOnlyList<DocRootFileCandidate> RootFileCandidates,
    // Всё, что продукт умеет показывать, — из этих групп и выбирают
    IReadOnlyList<DocTypeGroup> TypeGroups,
    DocsScope Defaults,
    // Документы области — варианты для «Начала»
    IReadOnlyList<DocOption> Documents,
    // Что сейчас работает «Началом»: выбранный документ либо README по умолчанию.
    // null — начального документа нет вовсе (пустая область или README удалён)
    string? Home);
