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
    IReadOnlyDictionary<string, string> SubsystemTitles);
