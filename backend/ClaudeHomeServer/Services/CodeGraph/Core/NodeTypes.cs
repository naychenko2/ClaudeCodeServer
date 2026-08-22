namespace ClaudeHomeServer.Services.CodeGraph.Core;

/// <summary>
/// Тип узла графа кода — только объявления типов (не методы).
/// Методы фрагментируют граф (спайк Graphify показал, что method-узлы создают шум).
/// Последние четыре значения — TS/React-провайдер (TypeScriptGraphProvider).
/// </summary>
public enum NodeKind
{
    Class,
    Interface,
    Struct,
    Enum,

    /// <summary>React-компонент (function/класс с JSX-разметкой).</summary>
    Component,

    /// <summary>React-хук (use* функция).</summary>
    Hook,

    /// <summary>UI-примитив дизайн-системы (компонент из components/ui).</summary>
    UiPrimitive,

    /// <summary>Утилита: хелпер, константа, тип — всё, что не компонент/хук.</summary>
    Util,
}

/// <summary>
/// Узел графа кода — представляет тип (класс/интерфейс/структура/enum).
/// </summary>
public record CodeGraphNode
{
    /// <summary>
    /// Уникальный идентификатор узла (FQN с нормализацией).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Отображаемое имя (короткое, без namespace).
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Полное квалифицированное имя (с namespace).
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Файл, где объявлен тип (относительный путь от rootPath).
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Позиция в файле (строка или диапазон строк).
    /// </summary>
    public required string SourceLocation { get; init; }

    /// <summary>
    /// Вид типа: C# — класс/интерфейс/структура/enum, TS/React — component/hook/ui-примитив/util.
    /// </summary>
    public required NodeKind Kind { get; init; }
}
