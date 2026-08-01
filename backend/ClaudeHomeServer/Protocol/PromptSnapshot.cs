namespace ClaudeHomeServer.Protocol;

// Снимок промпта одного хода — то, что CCS собрал и передал claude CLI. Живёт отдельным
// файлом (PromptSnapshotStore), а не в history.json: та грузится целиком при открытии чата,
// и сотня ходов по 10-40 КБ утопила бы открытие.

// Секция промпта. Kind:
//   system — часть --append-system-prompt (участвует в склейке и в долях от общего);
//   turn   — текст самого сообщения хода с обвязками (SessionManager.BuildCliTurnText,
//            разворот скилла, инлайн вложений). В системный промпт НЕ входит: показывается
//            отдельным блоком, иначе «скопировать промпт целиком» отдавало бы склейку,
//            которой модели не отправляли.
// Size — длина ОРИГИНАЛЬНОГО текста. Нужна, когда сам текст в выдаче опущен: файлы слоя
// CLI (CLAUDE.md — десятки КБ) отдаются по требованию, а размер и доли нужны сразу.
//
// Stable — кусок одинаков от хода к ходу (встроенный промпт, подсказки MCP, слой персоны):
// такие живут в кэшируемом префиксе запроса. False — пересчитывается под текст хода
// (auto-recall заметок и памяти, привязки, сам текст сообщения): начиная с первого
// изменившегося куска кэш префикса не переиспользуется. Что именно попало в кэш, знает
// только API — фактические cache_read/cache_creation приходят в usage хода.
//
// Group — чья это часть промпта: persona | mcp | project | recall | turn | cli | misc.
// Нужна, чтобы отвечать на вопрос «сколько стоит персона» (её слой размазан по пяти
// секциям: контракт, память, привязки, упоминания, подсказка про инструменты персон).
public record PromptSectionDto(string Key, string Title, string Text, string Kind = "system",
    int? Size = null, bool Stable = true, string Group = "misc");

// Скилл, видимый CLI на этом ходу (описание идёт моделью в каталоге скиллов).
// Source — откуда взят: "profile" (корень CLAUDE_CONFIG_DIR) или "project" (.claude/skills)
public record CliSkillDto(string Name, string? Description, string Source);

// Слой claude CLI — та его часть, которую можно достать честно. Недоступны и в снимок
// не попадают: текст встроенного системного промпта Anthropic, текстовые описания
// инструментов, цепочка родительских CLAUDE.md вверх по дереву.
//   Tools/McpServers — из события system/init (приходят ПОСЛЕ старта процесса, дописываются
//     в готовый файл через AttachCliLayer);
//   Files — CLAUDE.md рабочей папки и профиля с раскрытыми @-импортами (наша реконструкция);
//   Skills — каталог скиллов; Transcript* — вес истории, которую подтягивает --resume.
public record CliLayerDto(
    IReadOnlyList<string>? Tools = null,
    IReadOnlyList<McpServerInfo>? McpServers = null,
    IReadOnlyList<PromptSectionDto>? Files = null,
    IReadOnlyList<CliSkillDto>? Skills = null,
    long? TranscriptBytes = null,
    int? TranscriptMessages = null);

// Черновик снимка: то, что отдаёт ClaudeSession. Id и CreatedAt проставляет стор —
// вызывающий их не придумывает (иначе пришлось бы передавать заглушку).
public record PromptSnapshotDraft(
    bool Applied,
    string? InheritedFromId,
    IReadOnlyList<PromptSectionDto> Sections,
    IReadOnlyList<string> CliArgs,
    IReadOnlyList<string> McpServers,
    string? Model,
    string? Mode,
    CliLayerDto? CliLayer = null);

// Снимок на диске и в выдаче эндпоинта.
//   Applied=false — ход доигрывался в живом процессе (same-process), и этот промпт модели
//     НЕ уходил: она работает с промптом старта прогона, его id — InheritedFromId.
//   CliLayerFrom — файловая часть слоя CLI не продублирована, а лежит в снимке с этим id
//     (CLAUDE.md весит десятки КБ и меняется редко). Load разворачивает ссылку сам.
public record PromptSnapshotDto(
    string Id,
    long CreatedAt,
    bool Applied,
    string? InheritedFromId,
    IReadOnlyList<PromptSectionDto> Sections,
    IReadOnlyList<string> CliArgs,
    IReadOnlyList<string> McpServers,
    string? Model,
    string? Mode,
    CliLayerDto? CliLayer = null,
    string? CliLayerFrom = null);
