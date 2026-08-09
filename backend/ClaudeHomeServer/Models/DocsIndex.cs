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
// SectionFolder — папка, для которой этот документ является страницей раздела
// («docs/decisions.md» при наличии «docs/decisions/»). Правило пары «страница + папка»
// пришло из code wiki, где раздел существует только так: файл несёт содержание раздела,
// одноимённая папка — его дочерние страницы. Считает бэкенд: только он знает точный состав
// области и решает вопрос регистра путей один раз.
// Properties — свойства «шапки» документа; заполнены только у документов, чей тип описан
// схемой (индекс перезапрашивается на каждое изменение файлов, и возить свойства всех
// документов подряд ради метки у типизированных незачем — полный набор едет в DocDetail).
// Type — идентификатор типа из схемы .docs; null — тип не описан.
public record DocEntry(string Path, string Title, DateTime Modified, long Size,
    IReadOnlyList<DocHeading> Headings, bool Binary = false, string? SectionFolder = null,
    IReadOnlyList<DocProperty>? Properties = null, string? Type = null);

// Входящая ссылка: кто ссылается на документ и на какой его якорь
public record DocBacklink(string Path, string Title, string? Anchor);

// Документ целиком: исходный markdown + связи в обе стороны.
// У бинарного (Binary) Content пуст — панель показывает вместо превью предложение
// открыть его в центре, где есть просмотрщики pdf/office/visio/картинок/звука.
// PropsRange — отрезок Content, занятый шапкой свойств: по нему панель вырезает эти строки
// из превью, чтобы они не задвоились с блоком «Свойства». Отдаёт его разбор — только он
// точно знает, что съел, и «**Важно:**» посреди прозы под нож не попадёт.
public record DocDetail(string Path, string Title, string Content,
    IReadOnlyList<DocLink> Links, IReadOnlyList<DocBacklink> Backlinks, bool Binary = false,
    IReadOnlyList<DocProperty>? Properties = null, string? Type = null,
    DocPropsRange? PropsRange = null);

// Отрезок сырого текста документа: [Start, End)
public record DocPropsRange(int Start, int End);

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
// ВНИМАНИЕ: это группы типов ФАЙЛОВ (markdown/pdf/visio) — что продукт умеет открыть.
// Смысловой тип документа (ADR, спека, runbook) — это DocTypeDef ниже, их легко перепутать.
public record DocTypeGroup(string Key, string Title, IReadOnlyList<string> Extensions, bool Text);

// ---------- типы документов и их свойства ----------

// Свойство документа как оно записано в его шапке: «**Статус:** Принято» → («Статус», «Принято»).
// Значение СЫРОЕ, схема к нему не применялась: корпус разбирается без .docs, иначе правка
// схемы не инвалидировала бы кеш индекса (его отпечаток считается только по документам).
// Link заполнен, когда значение содержит markdown-ссылку: путь от корня проекта, чтобы
// панель могла перейти по нему, не разбирая markdown второй раз.
public record DocProperty(string Key, string Value, string? Link = null);

// Вид свойства = вид редактора в панели. Сериализуется строкой (camelCase):
// "choice" | "date" | "text" | "docLink".
public enum DocPropertyKind { Choice, Date, Text, DocLink }

// Значение свойства-выбора. Color — имя РОЛИ дизайн-системы («success», «danger»), а не цвет
// и не hex: сырой цвет запрещён, роль фронт кладёт на токен (--c-success…). Именно роль, а не
// «green»: зелёного как такового в токенах нет, и фронту пришлось бы его изобретать.
public record DocPropertyChoice(string Value, string Color = "gray", string? Title = null);

// Описание свойства в схеме типа. Key — ровно тот текст, что стоит в файле перед двоеточием.
// AutoUpdate имеет смысл только у Date: «дата смены» переписывается сама при правке ЛЮБОГО
// другого свойства этого документа.
public record DocPropertyDef(
    string Key,
    DocPropertyKind Kind,
    string? Title = null,
    IReadOnlyList<DocPropertyChoice>? Choices = null,
    bool AutoUpdate = false,
    bool Required = false);

// Тип документа: папки + необязательная маска ИМЕНИ файла («docs/adr» + «ADR-*.md»).
// В самом документе тип не указан — иначе его пришлось бы проставлять руками в каждом файле.
// BadgeProperty — какое свойство показывать плашкой в шапке и точкой в дереве; null — никакое.
public record DocTypeDef(
    string Id,
    string Title,
    IReadOnlyList<string> Folders,
    string? Match,
    string? BadgeProperty,
    IReadOnlyList<DocPropertyDef> Properties);

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
    string? Home,
    // Откуда взята область: «file» — файл .docs в репозитории (общий для всех, кто его
    // открыл), «project» — настройка продукта, своя у каждого владельца. Диалог по этому
    // полю решает, что он правит и предлагать ли вынести настройку в репозиторий.
    string ScopeSource = "project",
    // Заполнено, когда .docs есть, но не разобран: область при этом взята из настройки
    // проекта, и без этой строки расхождение «в файле одно, в панели другое» необъяснимо
    string? ScopeFileError = null,
    // Схема типов документов из .docs. Едет вместе с областью, а не отдельной ручкой:
    // панель запрашивает настройку при загрузке, и второй запрос ради того же файла лишний
    IReadOnlyList<DocTypeDef>? DocTypes = null,
    // Секция docTypes есть, но не разобрана: сама область при этом продолжает работать —
    // схема не имеет права утащить её за собой
    string? DocTypesError = null,
    // Роли, которыми можно красить значения выбора. Отдаёт сервер (как TypeGroups выше),
    // чтобы редактор типов строил палитру из ответа, а не хардкодом
    IReadOnlyList<string>? PropertyColors = null);
