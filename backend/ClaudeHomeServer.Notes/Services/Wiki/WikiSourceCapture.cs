using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Захват источника — создание карточки источника (ADR-035, решение 3): страница `type: source`
// в папке `Источники/` своего раздела; раздел выбирает WikiSourceHome по дому источника, не
// вызывающий. Карточка несёт только данные — origin, ref, hash, captured: текст заметки или
// файла не копируется, а version hash — sha256 БАЙТОВ файла на диске (та же форма, что у
// манифеста и сверки дрейфа — WikiKeeper.Sha256OfBytes): хэш строки и хэш файла — разные
// вещи при BOM или чужой кодировке, а сверять надо файл.
//
// Карточку пишет только Хранитель: этот класс — его внутренний инструмент (ADR-035, решение 2:
// «Хранитель пишет своим внутренним WikiFileWriter внутри сборки Notes»), запись — через
// WikiFileWriter (атомарный temp+Move), не прямым File.WriteAllText. Карточка неизменяема
// (решение 3): имя — детерминированная функция пары (ref, version), поэтому одна версия
// одного источника — одна карточка (повторный захват ничего не переписывает), новая версия —
// новая карточка, а два разных источника одного имени получить не могут: дискриминатор имени
// — хэш ref+version. Общего изменяемого состояния здесь нет и блокировка раздела не нужна:
// пишется новый файл, состояние раздела (его страницы) не трогается; проекции раздела
// обновляет транзакция применения (шаг 6), не захват.
//
// Чтение заметки — только публичными средствами NotesService с владельцем (GetDetail для
// текста, ResolveAttachmentPath для пути файла): источник принадлежит тому же владельцу, что
// вики; заметка другого владельца в модели этого владельца не существует — отказ. Файл —
// только внутри КАНОНИЧЕСКОГО корня своего проекта: SafePath (лексический) плюс ProjectLinkGuard
// (сегмент-ссылка наружу — не файл проекта, ADR-017 §8), и не под {data} целиком: там живут
// {data}/wiki (страницы самой вики) и notes/ чужих владельцев — источник не выходит за своего
// (корень проекта может быть предком data/, ADR-035, решение 2). Текст — только валидный UTF-8
// в пределах размера источника; хэш и текст — одних байт одного чтения.
//
// Данные источника — недоверенный ввод (урок ревью S2b-3): заголовок нормализуется в безопасную
// форму — управляющие символы и разделители, "|" (разделитель подписи ссылки), "[[]]" (чужие
// ссылки: файл карточки граф читает как страницу источника wiki), "---" (ограда frontmatter),
// символы, недопустимые в имени файла на Windows («:*?"<>») → "_": карточка пишется на обеих
// платформах (CI — Linux, разработка — Windows), и имя, которое одна из них отвергает при
// записи, — не имя карточки.
public sealed class WikiSourceCapture
{
    // Папка карточек источников внутри раздела (ADR-035, решение 3).
    public const string SourceDirName = "Источники";

    // Потолок безопасной формы имени: полное имя карточки = форма + "-" + дискриминатор (8 hex),
    // с запасом под лимит имени файла (255).
    private const int MaxNameLength = 80;

    // Предел файла-источника: его байты идут целиком и в хэш, и в текст источника (sourceText
    // правила дословности WikiGate живёт в памяти прогона). Файл больше — не заметка для вики,
    // а документ/архив: захват его не читает вовсе (отказ до чтения, а не «прочитал и отказал»).
    // internal: предел проверяется тестом напрямую, чтобы тест не зависел от угаданного числа.
    internal const long MaxSourceBytes = 2 * 1024 * 1024;

    // Строгая декодировка: невалидная последовательность — исключение, а не U+FFFD в тексте
    // источника (дословность QuoteMatcher по разрушенному тексту лгала бы).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _dataDir;
    private readonly NotesService _notes;
    private readonly IProjectManager _projects;

    public WikiSourceCapture(NotesService notes, IProjectManager projects, IConfiguration config)
    {
        _notes = notes;
        _projects = projects;
        // Каталог данных — как у всех сервисов вертикали (DataPath указывает на projects.json).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Результат захвата. При Ok: CardRelPath — путь карточки относительно pages/
    /// (он же ключ sourceTexts правила дословности WikiGate — S6a), SourceText — текст
    /// источника для QuoteMatcher. При отказе: только Reason, на диске не появилось ничего.</summary>
    public readonly record struct Result(bool Ok, string? Reason, string? CardRelPath, string? SourceText);

    /// <summary>Захват заметки (origin: note). target — null или "common": явный выбор человека
    /// для источника вне проекта (ADR-036, решение 3); проектному источнику — отказ там же.
    /// Заметка с `expires` — не источник: она временная, карточка временного источника
    /// навсегда осталась бы stale (триггер «заметка» в ADR-035, решение 12 — тоже «не expires»).</summary>
    public Result CaptureNote(string ownerId, string noteId, string? target = null)
    {
        var note = _notes.GetDetail(ownerId, noteId);
        if (note is null)
            return Refused("заметка не найдена: источник принадлежит тому же владельцу, что вики");
        // Страница вики — не внешний источник: она уже результат работы Хранителя, и её цитата
        // была бы петлёй «вики → вики» (анти-петля, ADR-035, решение 12).
        if (note.Source == NotesService.WikiKey)
            return Refused("страница вики — не источник");
        if (note.ExpiresAt is not null)
            return Refused("заметка с 'expires' — не источник");

        // Ключ личного источника и ключ личного раздела — одна строка ("personal",
        // NotesService.PersonalKey = WikiSections.Personal); проектный ключ собирается из id
        // и проверяется домашней функцией (невалидный id — отказ, не тихий «common»).
        var home = note.Source == WikiSections.Personal
            ? WikiSections.Personal
            : WikiSections.ProjectsPrefix + note.Source;
        var decision = WikiSourceHome.Decide(home, target);
        if (decision.SectionKey is null)
            return Refused(decision.Reason!);

        // Текст — из модели (GetDetail), путь файла — через ResolveAttachmentPath: оба —
        // публичные средства, владение проверяет ResolveRoot. Хэш —
        // байтов диска: следующая сверка должна сравнивать то, что реально лежит в файле.
        string versionHash;
        try
        {
            versionHash = Sha256OfBytes(File.ReadAllBytes(
                _notes.ResolveAttachmentPath(ownerId, note.Source, note.Path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return Refused($"файл заметки не читается: {e.Message}");
        }

        return WriteCard(ownerId, decision.SectionKey, WikiGate.OriginNote, $"note:{note.Id}",
            versionHash, note.Title, note.Id, note.Content);
    }

    /// <summary>Захват файла проекта (origin: file): только внутри корня своего проекта и не
    /// под {data}/wiki. Файл вне проекта (дом «личный») здесь не принимается: его принесёт
    /// следующий источник.</summary>
    public Result CaptureFile(string ownerId, string projectId, string relPath)
    {
        var project = _projects.GetById(projectId);
        if (project is null || project.OwnerId != ownerId)
            return Refused("файл чужого владельца или проекта не существует");
        // Файлы локального проекта живут на устройстве — сервер к ним не обращается (ADR-016).
        if (ProjectCapabilityGuard.Refusal(project, ProjectCapabilityArea.FileBound) is { } local)
            return Refused(local);

        // Путь — чистый относительный: пустые сегменты, "."/"..", управляющие символы — отказ
        // до диска: ref карточки обязан быть разрешимым, а «..» в пути — второй путь к тому же
        // файлу (две карточки одной версии).
        var rel = relPath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0 || rel.Any(c => c < ' ') ||
            rel.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
            return Refused("путь файла должен быть чистым относительным путём внутри проекта");

        // Путь вне корня — отказ с причиной, не исключение наружу: на Windows форма «C:x»
        // доходит до SafePath (на Linux её ловит проверка сегментов).
        string full;
        try { full = SafePath.JoinBelowRoot(project.RootPath, rel); }
        catch (UnauthorizedAccessException) { return Refused("файл за пределами корня проекта"); }

        // SafePath сравнивает строки и символьную ссылку наружу не видит: «docs/link.md →
        // ~/.ssh/id_rsa» лексически лежит в корне и читался бы целиком как файл проекта.
        // Инвариант: файл проекта читается только изнутри канонического корня (ProjectLinkGuard,
        // ADR-017 §8; тип — в сборке Core, шов вертикали к спинке).
        if (ProjectLinkGuard.HasLink(project.RootPath, full))
            return Refused("файл в проекте идёт через символическую ссылку — это не файл проекта");

        // Всё {data} — служебное пространство сервера, а не файлы проекта: {data}/wiki — страницы
        // самой вики, {data}/notes/{другойOwner}/ — чужой владелец (источник не выходит за своего).
        // {data}/wiki — канонически (IsWikiPath разыменывает ссылки со стороны data/: «data/wiki»
        // как ссылка задаёт корень своей целью); остальное — по абсолютному пути: после проверки
        // выше ни один сегмент ниже корня не является ссылкой, поэтому здесь лексическая форма
        // и есть физическая.
        if (WikiPaths.IsWikiPath(full, _dataDir))
            return Refused("файл под {data}/wiki — страница вики, а не источник");
        if (IsUnderData(full))
            return Refused("файл под {data} — служебные данные сервера, а не файл проекта");
        if (!File.Exists(full))
            return Refused("файл не найден");

        // Хэш и текст — ОДНИХ байт одного чтения: ReadAllBytes + ReadAllText описывали бы
        // разные состояния, если файл изменился между ними, а карточка обязана описывать одно.
        // Предел — до чтения: большой файл — память и время, которых захват не берёт.
        byte[] bytes;
        string sourceText;
        try
        {
            var length = new FileInfo(full).Length;
            if (length > MaxSourceBytes)
                return Refused($"файл больше предела источника ({MaxSourceBytes} байт): {length}");
            bytes = File.ReadAllBytes(full);
            sourceText = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Refused("файл не является текстом UTF-8");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refused($"файл не читается: {e.Message}");
        }

        // BOM — маркер кодировки файла, а не символ текста: в текст источника он не идёт
        // (GetString, в отличие от ReadAllText, преамбулу не снимает; дословность QuoteMatcher
        // сверяет то, что человек видит в тексте). Хэш при этом остаётся байтов файла, включая
        // BOM: карточка описывает файл, а не его текст.
        if (sourceText.Length > 0 && sourceText[0] == (char)0xFEFF) sourceText = sourceText[1..];

        var versionHash = Sha256OfBytes(bytes);
        var decision = WikiSourceHome.Decide(WikiSections.ProjectsPrefix + projectId);
        if (decision.SectionKey is null)
            return Refused(decision.Reason!);

        // ref файла несёт проект: карточка может быть скопирована в раздел выше при повышении
        // (ADR-036, решение 5), и по одному только пути файла источник не опознать.
        return WriteCard(ownerId, decision.SectionKey, WikiGate.OriginFile, $"file:{projectId}/{rel}",
            versionHash, Path.GetFileNameWithoutExtension(rel), rel, sourceText);
    }

    // Карточка = шапка (поля кода: type/origin/ref/hash/captured) + заголовок; текста нет
    // (карточка — данные, не рассказ: у type: source секций нет — WikiPage). Имя файла и
    // заголовок страницы — одна строка: сноска резолвится по заголовку (WikiGate.FindPage),
    // уникальность заголовка в разделе — правило гейта (CheckUniqueness), и она же держит
    // «два разных источника не затирают карточку друг друга».
    private Result WriteCard(string ownerId, string sectionKey, string origin, string ref_,
        string versionHash, string titleRaw, string idFallback, string sourceText)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var name = CardName(titleRaw, idFallback, ref_, versionHash);
        var cardRel = WikiSections.Folder(sectionKey)! + SourceDirName + "/" + name + ".md";
        var full = SafePath.JoinBelowRoot(root, "pages/" + cardRel);

        // Имя — функция (ref, version): существующий файл под этим именем — карточка именно
        // этой версии этого источника. Карточка неизменяема — не перечитываем и не переписываем
        // (в том числе captured: время первой фиксации — факт, не метка последнего обращения).
        if (!File.Exists(full))
        {
            var fm = new WikiFrontmatter
            {
                Type = WikiFrontmatter.TypeSource,
                Origin = origin,
                Ref = ref_,
                Hash = versionHash,
                Captured = DateTimeOffset.UtcNow.ToString("O"),
            };
            WikiFileWriter.Write(root, "pages/" + cardRel, fm.Render() + "\n\n# " + name + "\n");
        }
        return new(true, null, cardRel, sourceText);
    }

    /// <summary>Безопасная форма имени карточки: из заголовка, при пустом — из id источника
    /// (noteId / имя файла), и дискриминатор версии. internal: проверяется тестом напрямую —
    /// форма недоверенного ввода не должна зависеть от того, с чьей карточки её вызвали.</summary>
    internal static string CardName(string titleRaw, string idFallback, string ref_, string versionHash)
    {
        var safe = SafeName(titleRaw);
        if (safe.Length == 0)
            safe = SafeName(idFallback);
        // Дискриминатор — хэш пары (ref, version): два разных источника с одним заголовком
        // получают разные имена; та же версия того же источника — то же имя (идемпотентность).
        var disc = Sha256Hex(ref_ + ":" + versionHash)[..8];
        return safe.Length > 0 ? safe + "-" + disc : disc;
    }

    // Управляющие символы и разделители путей → пробел (имя — не путь), "|" выпадается
    // (разделитель подписи в [[цель|подпись]]), парные "[["/"]]" выпадается (иначе граф,
    // читающий файл карточки как страницу источника wiki, нашёл бы в заголовке чужую ссылку),
    // последовательности "---" сворачиваются в "-" (одиночная "---" — ограда frontmatter),
    // «:*?"<>» → "_" (имя файла недопустимо на Windows: «План: Q4» не записался бы вовсе, и
    // исключение записи шло бы наружу захвата), пробелы и точки
    // схлопываются, хвостовые "." и пробел снимаются (имя не «..» и не «x. »), длина ограничена.
    //
    // Path.GetInvalidFileNameChars() (образец NotesService.SanitizeFileName) здесь НЕ образец:
    // он платформозависим — на Linux отдаёт только "/" и NUL, а карточка пишется и на Linux (CI),
    // и на Windows (разработка): набор недопустимого явный, форма имени одна на обеих платформах.
    private static readonly char[] InvalidOnWindows = ['<', '>', ':', '"', '?', '*'];

    private static string SafeName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            if (ch < ' ' || ch is '/' or '\\') { sb.Append(' '); continue; }
            if (ch == '|') continue;
            if (Array.IndexOf(InvalidOnWindows, ch) >= 0) { sb.Append('_'); continue; }
            sb.Append(ch);
        }
        var s = sb.ToString();
        while (s.Contains("[[") || s.Contains("]]")) s = s.Replace("[[", "").Replace("]]", "");
        while (s.Contains("---")) s = s.Replace("---", "-");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        while (s.Contains("..")) s = s.Replace("..", ".");
        s = s.Trim().TrimEnd('.', ' ');
        if (s.Length > MaxNameLength)
            s = s[..MaxNameLength].TrimEnd('.', ' ');
        return s;
    }

    // Файл под {data} — служебное пространство сервера, а не файл проекта (чужие владельцы,
    // реестры, сама вики). Сравнение по абсолютному пути, компаратор — как у SafePath: регистр
    // значим там, где его значим ФС хоста. Вызывается только после ProjectLinkGuard, поэтому
    // разыменование ссылок здесь не нужен: ниже корня проекта ссылок уже нет.
    private bool IsUnderData(string fullPath)
    {
        var data = Path.GetFullPath(_dataDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cmp = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        return fullPath.Equals(data, cmp)
            || fullPath.StartsWith(data + Path.DirectorySeparatorChar, cmp);
    }

    // Одна форма хэша на всю вики (манифест, baseHashes, сверка диска, карточка): hex в нижнем
    // регистре. Здесь — байтов (та же Sha256OfBytes, что у WikiKeeper), не строки.
    private static string Sha256OfBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256Hex(string s) => Sha256OfBytes(Encoding.UTF8.GetBytes(s));

    private static Result Refused(string reason) => new(false, reason, null, null);
}
