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
// Для origin `session` (S6j) файл-источник на диске не читается: версия — sha256 самой выдержки,
// её режет WikiChatSlicer (у чата нет файла, переписанная история даёт новую выдержку — новую
// карточку). Остальные правила общие: неизменяемость, детерминированное имя, текст в карточку
// не пишется.
//
// Карточку пишет только Хранитель: этот класс — его внутренний инструмент (ADR-035, решение 2:
// «Хранитель пишет своим внутренним WikiFileWriter внутри сборки Notes»), запись — через
// WikiFileWriter (атомарный temp+Move), не прямым File.WriteAllText. Карточка неизменяема
// (решение 3): имя — детерминированная функция пары (ref, version), поэтому одна версия
// одного источника — одна карточка (повторный захват ничего не переписывает), новая версия —
// новая карточка, а два разных источника одного имени получить не могут: дискриминатор имени
// — хэш ref+version.
//
// Карточка — страница раздела, и её запись в манифесте раздела обязательна (дефект d144bb0f):
// сверка применения (WikiApplyService.CheckSectionAgainstDisk) считает любой .md раздела вне
// манифеста дрейфом — карточка без записи отбивала первое же применение после захвата, и
// цепочка «захват → разбор → применение» не работала. Поэтому запись идёт под блокировкой
// раздела (тот же замок, что у Хранителя и применения): файл и его запись атомарны относительно
// других операций раздела — иначе параллельное применение увидело бы файл до записи и
// отказало бы разделу. Записывает хэш байтов диска Хранитель (RegisterCapturedUnderLock):
// манифест — его состояние. Строки журнала захват не даёт: журнал — изменения знаний
// (словарь op — apply/adopt), карточка — данные захвата, не знание. Проекции раздела
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
    private readonly ISessionDirectory _sessions;
    // Замок раздела и запись в его манифест — у Хранителя: карточка, которую он не запишет,
    // станет дрейфом для ближайшего применения (дефект d144bb0f, см. шапку).
    private readonly WikiKeeper _keeper;

    public WikiSourceCapture(NotesService notes, IProjectManager projects, ISessionDirectory sessions,
        IConfiguration config, WikiKeeper keeper)
    {
        _notes = notes;
        _projects = projects;
        _sessions = sessions;
        _keeper = keeper;
        // Каталог данных — как у всех сервисов вертикали (DataPath указывает на projects.json).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Результат захвата. При Ok: CardRelPath — путь карточки относительно pages/
    /// (он же ключ sourceTexts правила дословности WikiGate — S6a), SourceText — текст
    /// источника для QuoteMatcher. При отказе: только Reason, на диске не появилось ничего
    /// — кроме сбоя посреди записи (файл карточки уже на диске, записи в манифесте нет):
    /// причина называет это, повторный захват той же версии дописывает запись (п. идемпотентности).</summary>
    public readonly record struct Result(bool Ok, string? Reason, string? CardRelPath, string? SourceText);

    /// <summary>Захват заметки (origin: note). target — null или "common": явный выбор человека
    /// для источника вне проекта (ADR-036, решение 3); проектному источнику — отказ там же.
    /// Заметка с `expires` — не источник: она временная, карточка временного источника
    /// навсегда осталась бы stale (триггер «заметка» в ADR-035, решение 12 — тоже «не expires»).</summary>
    public async Task<Result> CaptureNote(string ownerId, string noteId, string? target = null)
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

        return await WriteCard(ownerId, decision.SectionKey, WikiGate.OriginNote, $"note:{note.Id}",
            versionHash, note.Title, note.Id, note.Content);
    }

    /// <summary>Захват файла проекта (origin: file): только внутри корня своего проекта и не
    /// под {data}/wiki. Файл вне проекта (дом «личный») здесь не принимается: его принесёт
    /// следующий источник.</summary>
    public async Task<Result> CaptureFile(string ownerId, string projectId, string relPath)
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
        return await WriteCard(ownerId, decision.SectionKey, WikiGate.OriginFile, $"file:{projectId}/{rel}",
            versionHash, Path.GetFileNameWithoutExtension(rel), rel, sourceText);
    }

    /// <summary>Захват чата (origin: session, ADR-035, решение 3 и §12б): выдержка
    /// `session:{id}#m{a}-{b}` — её режет WikiChatSlicer (реплики человека и ответы агента,
    /// всё через SecretRedactor, без вызовов инструментов), ≤8 КБ. Сессия обязана принадлежать
    /// тому же владельцу и тому же проекту, что раздел назначения: раздел определяет дом чата
    /// (ADR-036, решение 3), и разбор раздела не цитирует чужие чаты. Чаты исполнения задач —
    /// отказ: «чаты исполнения задач не разбираются» (§12, решение Андрея), их итоги уже
    /// триггер. exactSecrets — точные значения секретов инстанса от вызывающего: их собирает
    /// InstanceSecretsProvider вертикали Dossiers (Notes его не видит), без них работает
    /// регулярковая часть SecretRedactor (известные форматы токенов). Любой неожиданный вход —
    /// отказ с причиной, не исключение (урок ревью S6h).</summary>
    public async Task<Result> CaptureSession(string ownerId, string sectionKey, string sessionId,
        int from, int to, IReadOnlyCollection<string>? exactSecrets = null)
    {
        var writable = WikiSections.IsWritable(sectionKey);
        if (!writable.Writable)
            return Refused($"раздел назначения не является разделом вики: {writable.Reason}");
        if (from < 1 || from > to)
            return Refused($"неверный диапазон сообщений: m{from}-m{to}");
        if (string.IsNullOrEmpty(sessionId))
            return Refused("id сессии пустой");

        // ref проверяется той же формой, которой будет читаться (WikiPage.TryParseSessionRef):
        // нерезолвимый ref — карточка со битой сноской, её писать нельзя (id с спецсимволами —
        // сюда же).
        var ref_ = $"session:{sessionId}#m{from}-{to}";
        if (!WikiPage.TryParseSessionRef(ref_, out _, out _, out _))
            return Refused($"ref карточки не представим формой session:{{id}}#m{{a}}-{{b}}: '{ref_}'");

        var session = _sessions.GetById(sessionId);
        if (session is null)
            return Refused("сессия не существует");
        if (_sessions.ResolveOwnerId(session) != ownerId)
            return Refused("чат принадлежит другому владельцу: источник — тому же владельцу, что вики");
        if (session.TaskExecution)
            return Refused("чаты исполнения задач не разбираются: их итоги уже источники");

        var home = session.ProjectId is not null
            ? WikiSections.ProjectsPrefix + session.ProjectId
            : WikiSections.Personal;
        if (home != sectionKey)
            return Refused($"чат принадлежит другому дому '{home}': карточка пишется в раздел дома чата");

        var history = await _sessions.GetHistoryAsync(sessionId);
        if (history is null || history.Count == 0)
            return Refused("у чата нет истории");
        var branchPoint = WikiChatSlicer.BranchPoint(history);
        if (branchPoint > 0 && from <= branchPoint)
            return Refused($"диапазон захватывает префикс ветки: префикс — ответственность родителя, " +
                $"диапазон начинается с m{branchPoint + 1}");
        if (from > history.Count)
            return Refused($"в диапазоне нет сообщений: длина истории чата {history.Count}");

        var excerpt = WikiChatSlicer.Excerpt(history, from, to, exactSecrets);
        if (excerpt.Length == 0)
            return Refused("в диапазоне нечего цитировать: всё его содержимое — служебные записи");

        // Версия источника-сессии — сама выдержка: карточка неизменяема (решение 3), а
        // переписывание истории (сброс курсора, §12б этап 1) даёт другую выдержку → новую
        // карточку, не перезапись старой. Заголовок — имя чата, при пустом — id (CardName).
        return await WriteCard(ownerId, sectionKey, WikiGate.OriginSession, ref_,
            Sha256OfBytes(Encoding.UTF8.GetBytes(excerpt)), session.Name ?? "", sessionId, excerpt);
    }

    // Карточка = шапка (поля кода: type/origin/ref/hash/captured) + заголовок; текста нет
    // (карточка — данные, не рассказ: у type: source секций нет — WikiPage). Имя файла и
    // заголовок страницы — одна строка: сноска резолвится по заголовку (WikiGate.FindPage),
    // уникальность заголовка в разделе — правило гейта (CheckUniqueness), и она же держит
    // «два разных источника не затирают карточку друг друга».
    //
    // Запись — под блокировкой раздела (тот же замок, что у Хранителя и применения): проверка
    // состояния раздела, файл карточки и его запись в манифесте — одна атомарная относительно
    // других операций раздела операция. Вызывающий захват замок не держит (разбор чатов зовёт
    // CaptureSession до очереди заявок, хронику пишет через WritePageAsync после применения) —
    // вложенной второй блокировки того же ключа здесь нет.
    private async Task<Result> WriteCard(string ownerId, string sectionKey, string origin, string ref_,
        string versionHash, string titleRaw, string idFallback, string sourceText)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var name = CardName(titleRaw, idFallback, ref_, versionHash);
        var cardRel = WikiSections.Folder(sectionKey)! + SourceDirName + "/" + name + ".md";
        try
        {
            return await _keeper.WithSectionsLockedAsync(ownerId, [sectionKey],
                () => Task.FromResult(WriteCardUnderLock(ownerId, root, sectionKey, name, cardRel, origin, ref_,
                    versionHash, sourceText)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException)
        {
            // Сбой посреди записи (файл карточки на диске, записи в манифесте нет) наружу не
            // выходит: исход — отказ с причиной, повторный захват той же версии дописывает
            // запись (файл под своим именем принимается без перезаписи).
            return Refused($"запись карточки не завершена: {e.Message}");
        }
    }

    private Result WriteCardUnderLock(string ownerId, string root, string sectionKey, string name, string cardRel,
        string origin, string ref_, string versionHash, string sourceText)
    {
        var manifest = WikiManifest.TryRead(root, sectionKey);
        if (manifest.State == WikiManifestState.Invalid)
            return Refused($"манифест раздела «{sectionKey}» невалиден — захват в него не пишет");

        // Раздел должен быть согласован с манифестом до нашей записи: писать карточку в
        // разложенный дрейфом раздел значило бы прятать чужую правку за своей записью.
        var drift = CheckSectionForCapture(root, sectionKey, manifest.Manifest, cardRel);
        if (drift is not null)
            return Refused(drift);

        var full = SafePath.JoinBelowRoot(root, "pages/" + cardRel);
        var existed = File.Exists(full);
        if (existed)
        {
            // Имя — функция (ref, version): существующий файл под этим именем — карточка именно
            // этой версии этого источника. Карточка неизменяема — не перечитываем и не переписываем
            // (в том числе captured: время первой фиксации — факт, не метка последнего обращения).
            var foreign = CheckExistingCard(root, cardRel, ref_);
            if (foreign is not null)
                return Refused(foreign);
        }
        else
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

        // Запись карточки в манифест раздела — её делает Хранитель (он её написал): карточка
        // вне манифеста — дрейф для ближайшего применения (дефект d144bb0f). Четыре случая:
        // новой карточке — записать; карточке на диске без записи (сбой посреди прежней записи) —
        // дописать; восстановленной по потерянной (запись была, файла не стало) — обновить под её
        // новые байты; карточке на диске с протухшей записью (повторный захват оборвался между
        // Write и этой регистрацией: файл — новые байты, запись — прежних) — обновить под байты
        // диска (доработка 2, п.2): оставленная протухшая запись — безвыходный дрейф, её больше
        // нечем починить. Имя — функция (ref, version), поэтому та же запись того же источника не
        // может отличаться от восстановленных байтов ничем, кроме captured (время этой фиксации;
        // версия источника в имени, и она та же): оставить старую запись — значит вернуть
        // раздел в дрейф по хэшу его собственной карточки. Совпадающую запись существующей
        // карточки не повторяем: она подтверждена сверкой выше.
        if (!existed || !manifest.Manifest.TryGetValue(cardRel, out var recorded)
            || recorded != Sha256OfBytes(File.ReadAllBytes(full)))
            _keeper.RegisterCapturedUnderLock(ownerId, sectionKey, cardRel);
        return new(true, null, cardRel, sourceText);
    }

    // Состояние раздела перед записью карточки — та же сверка, что шаг 2 применения
    // (CheckSectionAgainstDisk у него — private, файл вне права этой правки), за вычетом
    // нашей карточки: её записи в манифесте ещё нет, это ровно то, что мы записываем.
    // Служебные «_*» — не страницы (тот же контракт, что у Хранителя).
    //
    // Исключение — СВОЯ карточка в обеих ветках сверки: потерянная (её пишет сам этот захват,
    // WriteCard ниже) и лежащая на диске с протухшей записой (файл был переписан захватом,
    // оборвавшимся между записью файла и обновлением записи — запись подставится под его
    // байты ниже). Имя карточки — функция (ref, version), так что файл под ним — та же
    // версия того же источника. Считать её дрейфом значило бы закрыть разделу путь к
    // применению навсегда: захват, применение и adopt отказывают по отсутствующему файлу или
    // по расхождению хэша из манифеста, а починить это можно только ручной правкой манифеста
    // (хвост ревью S6e-2, п.1; доработка 2, п.2). Чужой файл в любой из двух веток — дрейф.
    private static string? CheckSectionForCapture(string root, string sectionKey,
        IReadOnlyDictionary<string, string> manifest, string ourCardRel)
    {
        foreach (var (page, hash) in manifest)
        {
            var full = SafePath.JoinBelowRoot(root, "pages/" + page);
            if (!File.Exists(full))
            {
                if (page == ourCardRel)
                    continue;
                return $"файл «{page}» из манифеста раздела отсутствует на диске: захват не пишет в раздел с потерянным файлом";
            }
            if (Sha256OfBytes(File.ReadAllBytes(full)) != hash)
            {
                if (page == ourCardRel)
                    continue;
                return $"файл «{page}» расходится с манифестом раздела: захват не пишет в раздел с правкой мимо Хранителя";
            }
        }

        var folder = WikiSections.Folder(sectionKey);
        if (folder is null)
            return $"«{sectionKey}» не ключ раздела вики";
        var dir = SafePath.JoinBelowRoot(root, "pages/" + folder);
        if (!Directory.Exists(dir))
            return null; // раздела на диске нет: норма до первой операции (пустой манифест проверен выше)

        var known = new HashSet<string>(manifest.Keys, StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            var pageKey = folder + Path.GetRelativePath(dir, full).Replace('\\', '/');
            if (WikiPaths.IsProjection(pageKey) || pageKey == ourCardRel)
                continue;
            if (!known.Contains(pageKey))
                return $"файл «{pageKey}» на диске, но не в манифесте раздела: захват не пишет в раздел с чужими файлами вне манифеста";
        }
        return null;
    }

    // Файл под нашим именем уже на диске (повторный захват или карточка прежней версии кода,
    // записанная без манифеста): имя — функция (ref, version), так что файл под ним — наша
    // карточка, если это вообще она: принимаем только валидную страницу type: source со своим
    // ref; чужой файл под нашим именем — отказ, а не молчаливая перезапись чужого.
    private static string? CheckExistingCard(string root, string cardRel, string ref_)
    {
        var text = WikiFileWriter.Read(root, "pages/" + cardRel);
        if (text is null)
            return $"файл «{cardRel}» исчез между проверкой и чтением: состояние раздела меняется под рукой, захват повторяется";
        var parsed = WikiPage.Parse(text);
        if (parsed.Model is null || parsed.Model.Frontmatter.Type != WikiFrontmatter.TypeSource)
            return $"файл «{cardRel}» под именем карточки не читается как карточка источника";
        if (parsed.Model.Frontmatter.Ref != ref_)
            return $"файл «{cardRel}» — карточка другого источника (ref «{parsed.Model.Frontmatter.Ref}»): имя занято";
        return null;
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
