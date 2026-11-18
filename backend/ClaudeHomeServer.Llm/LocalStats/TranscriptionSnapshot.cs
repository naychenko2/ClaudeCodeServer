namespace ClaudeHomeServer.Services.Llm;

// ─── Wire-контракт вкладки «Встречи» (GET /api/local-model/transcription), Б1 ───
// Снимок состояния конвейера транскрибации; собирает его TranscriptionStateReader из файлов
// самого конвейера при каждом запросе. Все уровни («Завис?», «не та машина», точка вкладки)
// считает бэкенд (proposal.md: «фронт их не выводит сам»). Времена — unix-мс.

// Общий снимок. level — точка вкладки: "ok" | "warn" | "danger" | "unknown"
// (красная: «Завис?», «Остановлен» или сбой зависимости; жёлтая: расхождение с Dify или «Пишет в» не туда).
public sealed record TranscriptionSnapshot(
    long At,
    string Level,
    TranscriptionPipelineDto Pipeline,
    TranscriptionNowDto? Now,
    TranscriptionQueueDto Queue,
    TranscriptionFailDto[] Fails,
    TranscriptionMeetingDto[] Recent,
    TranscriptionDifyDto[] Dify,
    TranscriptionDepDto[] Deps)
{
    // Пустой снимок «не проверялось»: сервиса нет в контейнере, файлов конвейера на узле нет,
    // файлы не читаются. 200, не 500 (требование устойчивости).
    public static TranscriptionSnapshot Unknown(long at) =>
        new(at, "unknown",
            new("unknown", null, null, null, null, false, "unknown", null, null, "unknown"),
            null, new(0, [], 0), [], [], [], []);
}

// Блок «Конвейер».
public sealed record TranscriptionPipelineDto(
    // Нормализованная фаза: "idle" | "working" | "waiting" (ждёт зависимость: машину с GPU, простоя
    // ComfyUI, память) | "stopped" | "unknown".
    string Phase,
    // Сырая фаза state.json русскими («ожидание», «транскрибация», …) — для строки подробностей;
    // неизвестная конвейером фаза остаётся здесь при phase="working" (процесс жив и чем-то занят).
    string? PhaseText,
    // unix-мс последней записи state.json; null — файла нет или время не читается.
    long? UpdatedAt,
    // Ключ узла, где читается состояние («Где работает»): файлы читаются локально — это узел CCS.
    string? Host,
    int? Pid,
    // Жив ли на этом узле процесс с этим pid.
    bool Alive,
    // Плашка: "ok" | "stale" («Завис?»: state.json старше 5 мин при живом pid) | "stopped" | "unknown".
    string Level,
    // «Пишет в»: ключ узла каталога вывода; null — не этот узел или не проверялось.
    string? OutputHost,
    // Общий каталог выходов папок (config.yaml folders[].output).
    string? OutputDir,
    // "ok" (каталог виден на этом узле) | "warn" («не та машина»: каталога на узле нет) |
    // "unknown" (конфига нет — не проверялось).
    string OutputLevel);

// Блок «Сейчас». null whole — ничего не обрабатывается (или фаза не помечена файлом).
public sealed record TranscriptionNowDto(
    string File,
    string? Folder,
    // "asr" (распознавание) | "summary" (сводка). Этапа «В Dify» конвейер в state.json не помечает —
    // бэкенд его не выдумывает. null — этап не опознан (напр. «остановка vLLM»).
    string? Stage,
    // unix-мс записи state, открывшей этап: конвейер перезаписывает снимок на переходе фаз,
    // поэтому updated текущего снимка = начало текущего этапа.
    long? StageStartedAt,
    double? DurationMin,
    // Место в пачке — state.json его не несёт (снимок на файле не содержит индекса), v1 всегда null.
    int? BatchIndex,
    // batch_total из state.json — есть только в снимке старта пачки (без файла); на файле конвейер
    // его не повторяет. Чаще null.
    int? BatchSize);

// Блок «Очередь». asrQueue — конвейер пишет счёт (state.json.queue), а не список файлов;
// в v1 список пуст, UI показывает счётчик.
public sealed record TranscriptionQueueDto(
    int AsrCount,
    TranscriptionQueueFileDto[] AsrQueue,
    // Ждут сводки: summary_state.json.pending (staging) — только снимок того же процесса.
    int SummaryCount);

public sealed record TranscriptionQueueFileDto(string File, string? Folder, long? Since);

// Сбой за 24 ч (journal.tsv, итог=fail).
public sealed record TranscriptionFailDto(long At, string? Folder, string? File, double? Minutes, string Comment);

// Готовая встреча за 24 ч (journal.tsv, итог=ok). mdPath — путь .md на этом узле
// (output папки из конфига + имя файла с нормализованным расширением); null — папки в конфиге нет.
public sealed record TranscriptionMeetingDto(
    long At, string? Folder, string File, double? Minutes, int? Speakers, string? Comment, string? MdPath);

// Строка сверки с Dify по папке. local — число .md в каталоге вывода (null — каталога нет на узле);
// inDify — результат сверки конвейер пишет только в systemd-журнал (не в файлы), сам бэкенд
// в Dify не ходит — в v1 null («не проверялось»).
public sealed record TranscriptionDifyDto(string Folder, int? Local, int? InDify);

// Зависимость. key: "asr" (машина распознавания), "summary" (модель сводок), "dify", "share" (сетевая папка).
// status: "ok" | "down" | "unknown". Состояние — по тому, что сам конвейер пишет о своих зависимостях
// (фазы «ждёт …» в state.json, метки «dify …» в журнале), и по наличию watch-каталогов на узле;
// «slow» из файлов не измеряется — v1 его не отдаёт; «unknown» = «не проверялось».
public sealed record TranscriptionDepDto(string Key, string Status, long CheckedAt);
