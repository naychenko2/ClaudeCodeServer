namespace ClaudeHomeServer.Services.Architecture;

// Нейтральные входы генератора. Вертикаль не знает про CodeGraph: снимок графа
// приходит через Core-шов IArchitectureCodeSource, и в эти записи его перекладывает
// ArchitectureController.ToInput, так что связи «вертикаль → вертикаль» нет.

/// <summary>Тип из снимка графа кода: id, имя, файл (относительно корня проекта), вид.</summary>
public sealed record CodeTypeInfo(string Id, string Name, string SourceFile, string Kind);

/// <summary>Ребро графа кода между двумя типами (Calls | Implements | References).</summary>
public sealed record CodeEdgeInfo(string Source, string Target, string Relation);

/// <summary>Снимок графа кода для генератора; BuiltAt — время снимка, им помечается карта.</summary>
public sealed record CodeSnapshotInput(
    IReadOnlyList<CodeTypeInfo> Types,
    IReadOnlyList<CodeEdgeInfo> Edges,
    DateTimeOffset? BuiltAt);

/// <summary>
/// Проект-маркер в дереве: .csproj или package.json. RelDir — каталог маркера
/// относительно корня, References — каталоги проектов, на которые он ссылается.
/// </summary>
public sealed record SourceUnit(
    string RelDir,
    string Name,
    string Technology,
    bool IsTest,
    IReadOnlyList<string> References);

/// <summary>Всё, из чего собирается стартовая модель.</summary>
public sealed record ArchitectureInput(
    string SystemName,
    IReadOnlyList<SourceUnit> Units,
    CodeSnapshotInput Snapshot,
    // Подсистемы (IAppSubsystem у CCS): id типа → заголовок (Title).
    IReadOnlyDictionary<string, string> SubsystemTitles,
    // Кандидаты во внешние системы L1 (ArchitectureExternalScanner); null — не искали.
    IReadOnlyList<ExternalCandidate>? Externals = null);

/// <summary>
/// Кандидат во внешнюю систему уровня L1, найденный в конфиге или коде. В модель едут только
/// имя, эвристика технологии и человекочитаемый источник — НИ ОДНОГО значения конфига
/// (адреса, ключи): сканер значений не читает вовсе.
/// </summary>
public sealed record ExternalCandidate(
    string Name,          // имя секции/сервиса/клиента: "Dify", "Perplexity", "signoz"
    string? Technology,   // эвристика: "HTTP API", "docker-сервис", null
    string Source,        // человекочитаемый источник: "appsettings.json: секция Dify"
    int Confidence);      // 0..100 — для сортировки и отсечки по потолку
