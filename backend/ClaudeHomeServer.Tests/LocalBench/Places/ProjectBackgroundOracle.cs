using ClaudeHomeServer.Services.Backgrounds;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Машинный оракул контракта места <c>project-background</c> (Батарея II, ось A).
///
/// Контракт задан промптом в <c>ProjectBackgroundService.BuildPrompt</c> и разбором
/// <see cref="ProjectDoodleTile.Build"/>: JSON с массивом фигур дудла (путь(и) и круги,
/// позиция, поворот) плюс ключ цвета из палитры. Разметки от модели не бывает — только
/// данные (ADR-008).
///
/// Судится ПРОДУКТОВЫМ разбором: тайл собрался — контракт соблюдён. Порог годности
/// (не меньше <see cref="ProjectDoodleTile.MinShapes"/> фигур) — часть контракта, а не
/// придирка: полупустой дудл продукт отбрасывает как брак и оставляет проект на
/// стандартном фоне.
///
/// Ключ цвета судится отдельно и строго: он необязателен для сборки тайла (негодный
/// продукт молча игнорирует), но место обязано его предложить — цвет проекта
/// проставляется из этого же ответа, и его молчаливая потеря и есть тот отказ, ради
/// измерения которого замер затеян.
/// </summary>
public static class ProjectBackgroundOracle
{
    /// <summary>Чем нарушен контракт. null — ответ валиден.</summary>
    public static string? Violation(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return "пустой ответ";

        var tile = ProjectDoodleTile.Build(rawAnswer);
        if (!tile.Ok)
            return tile.FailReason switch
            {
                "bad-json" => "в ответе нет годного JSON с фигурами",
                "rejected" => $"годных фигур меньше {ProjectDoodleTile.MinShapes}",
                var other => $"тайл не собрался: {other}",
            };

        return tile.ColorKey is null
            ? "нет ключа цвета из палитры (colorKey)"
            : null;
    }

    /// <summary>Сколько фигур приняла сборка и какой цвет предложен — для результата.</summary>
    public static (int Shapes, string? ColorKey) Summary(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return (0, null);
        var tile = ProjectDoodleTile.Build(rawAnswer);
        // Число фигур считаем по собранному документу: сколько групп <g> в нём осталось
        // после отбраковки — ровно столько фигур место и приняло.
        var shapes = tile.Svg is null
            ? 0
            : tile.Svg.Split("<g ", StringSplitOptions.None).Length - 1;
        return (shapes, tile.ColorKey);
    }
}
