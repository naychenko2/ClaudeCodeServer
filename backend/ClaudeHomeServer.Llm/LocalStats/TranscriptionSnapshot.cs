namespace ClaudeHomeServer.Services.Llm;

// ─── Wire-контракт вкладки «Встречи» (GET /api/local-model/transcription), Б1 ───
// Снимок состояния конвейера транскрибации; собирает его TranscriptionStateReader из файлов
// самого конвейера при каждом запросе. Все уровни («Завис?», «не та машина», точка вкладки)
// считает бэкенд (proposal.md: «фронт их не выводит сам»). Времена — unix-мс.
//
// Ответ ПЛОСКИЙ: поля блоков proposal.md на верхнем уровне, вложенных «pipeline»/«now»/«queue»
// нет — по этому типу и фронтовый TranscriptionSnapshot (frontend/src/features/localModel/types.ts).
// Множества значений — фронтовые: phase «idle»|«batch»|«stopped», stage «asr»|«summary»|«dify»,
// level «ok»|«warn»|«danger», deps[].state «ok»|«slow»|«down»|«unknown». «Не проверялось» для
// level и stage — отсутствие значения (null), а не «unknown»: таких значений во фронтовом типе
// нет, и точка/баннер не появятся. Внутренние подуровни (stale плашки, уровень вывода) живут
// только в ридере, в ответ не выходят.

public sealed record TranscriptionSnapshot
{
    // === Конвейер ===
    // Нормализованная фаза: "idle" — пачки нет, "batch" — процесс занят (идёт работа, ждёт
    // зависимость или фаза не опознана), "stopped" — сервис остановлен или pid мёртв.
    public string Phase { get; init; } = "stopped";
    // unix-мс последней записи state.json; null — файла нет или время не читается.
    public long? UpdatedAt { get; init; }
    // Ключ узла, где читается состояние («Где работает»): файлы читаются локально — это узел CCS.
    public string? Host { get; init; }
    public int? Pid { get; init; }
    // Жив ли на этом узле процесс с этим pid.
    public bool Alive { get; init; }
    // «Пишет в»: ключ узла каталога вывода; null — не этот узел или не проверялось.
    public string? OutputHost { get; init; }
    // Общий каталог выходов папок (config.yaml folders[].output).
    public string? OutputDir { get; init; }

    // Точка вкладки: "ok" | "warn" | "danger". Красная: «Завис?», «Остановлен» или сбой
    // зависимости; жёлтая: расхождение с Dify или «Пишет в» не туда. null — «не проверялось»:
    // фронта нет ни точки, ни баннера (значения «unknown» в его типе нет).
    public string? Level { get; init; }

    // === Сейчас ===. Ничего не обрабатывается или этап не опознан — поля null:
    // вкладка рисует «Ничего не обрабатывается».
    public string? File { get; init; }
    public string? Folder { get; init; }
    // "asr" (распознавание) | "summary" (сводка). Этапа «В Dify» конвейер в state.json не помечает —
    // бэкенд его не выдумывает.
    public string? Stage { get; init; }
    // unix-мс НАЧАЛА текущей операции — из поля stage_started_at снимка конвейера (unix-сек → мс,
    // М4). Метки нет, когда операции нет: тогда null, а не updatedAt — пульс обновляет только
    // updated, а подстановка метки последней правки вместо начала операции — ровно то,
    // что чинит доработка Б1.
    public long? StageStartedAt { get; init; }
    public double? DurationMin { get; init; }
    // Место в пачке — state.json его не несёт (снимок на файле не содержит индекса), v1 всегда null.
    public int? BatchIndex { get; init; }
    // batch_total из state.json — есть только в снимке старта пачки (без файла); на файле конвейер
    // его не повторяет. Чаще null.
    public int? BatchSize { get; init; }

    // === Очередь ===. asrQueue — список файлов, ждущих распознавания, из state.json queue_items
    // (М2); since — unix-мс (конвейер хранит unix-секунды). Их нет — пустой список.
    // summaryQueue — ждут сводки: summary_state.json.pending (staging) — только снимок того же процесса.
    public TranscriptionQueueFileDto[] AsrQueue { get; init; } = [];
    public int? SummaryQueue { get; init; }

    // === Сбои за 24 ч === (journal.tsv, итог=fail).
    public TranscriptionFailDto[] Fails { get; init; } = [];

    // === Готовые встречи за 24 ч === (journal.tsv, итог=ok). mdPath — путь .md на этом узле
    // (output папки из конфига + имя файла с нормализованным расширением); null — папки в конфиге нет.
    public TranscriptionMeetingDto[] Recent { get; init; } = [];

    // === Сверка с Dify === по папке — из файла самого конвейера logs/dify_state.json (М2):
    // local — записей локально, inDify — загружено в Dify (null — конвейер не получил список Dify).
    // Бэкенд в Dify не ходит и строк не выдумывает: файла нет — пустой блок. checkedAt — unix-мс
    // проверки из файла; null — метки в файле нет.
    public TranscriptionDifyDto[] Dify { get; init; } = [];
    public long? CheckedAt { get; init; }

    // === Зависимости. key: "asr" (машина распознавания), "summary" (модель сводок), "dify", "share" (сетевая папка).
    // state: "ok" | "slow" | "down" | "unknown". Состояние — по тому, что сам конвейер пишет о своих зависимостях
    // (фазы «ждёт …» в state.json, метки «dify …» в журнале), и по наличию watch-каталогов на узле;
    // «slow» из файлов не измеряется — v1 его не отдаёт; «unknown» = «не проверялось».
    public TranscriptionDepDto[] Deps { get; init; } = [];

    // Пустой снимок «не проверялось»: сервиса нет в контейнере, файлов конвейера на узле нет,
    // файлы не читаются. 200, не 500 (требование устойчивости). Phase «stopped» — живого процесса
    // не знаем; level отсутствует — «не проверялось», не тревога.
    public static TranscriptionSnapshot Unknown(long at) => new()
    {
        Deps = [.. UnknownDeps(at)],
    };

    // Зависимости «не проверялось» — тот же набор ключей, что отдаёт ридер при нечитаемом снимке.
    internal static IEnumerable<TranscriptionDepDto> UnknownDeps(long at) =>
        new[] { "asr", "summary", "dify", "share" }.Select(k => new TranscriptionDepDto(k, "unknown", at));
}

// Файл в очереди распознавания. since — unix-мс, с когда файл ждёт («ждёт N мин»).
public sealed record TranscriptionQueueFileDto(string File, string? Folder, long? Since);

// Сбой за 24 ч (journal.tsv, итог=fail).
public sealed record TranscriptionFailDto(long At, string? Folder, string? File, double? Minutes, string Comment);

// Готовая встреча за 24 ч (journal.tsv, итог=ok).
public sealed record TranscriptionMeetingDto(
    long At, string? Folder, string File, double? Minutes, int? Speakers, string? Comment, string? MdPath);

// Строка сверки с Dify по папке (dify_state.json). inDify null — конвейер не получил список Dify.
public sealed record TranscriptionDifyDto(string Folder, int? Local, int? InDify);

// Зависимость. checkedAt — unix-мс последней проверки.
public sealed record TranscriptionDepDto(string Key, string State, long CheckedAt);
