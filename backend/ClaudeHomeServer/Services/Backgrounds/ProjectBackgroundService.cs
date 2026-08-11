using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Backgrounds;

/// <summary>
/// Итог генерации для ответа API: состояние фона, версия тайла (имя файла — оно же
/// cache-buster) и судьба предложенного моделью цвета.
/// </summary>
public sealed record BackgroundResult(
    ProjectBackgroundKind Kind, string? TileVersion, string? SuggestedColorKey,
    bool ColorApplied, string? FailReason);

/// <summary>
/// Состояние фона для фронта (в DTO проекта и в ответах эндпоинтов): версия тайла — имя
/// файла, оно же cache-buster у GET tile.svg. null у проекта = генерацию не пробовали.
/// </summary>
public sealed record ProjectBackgroundView(string Kind, string? TileVersion, string? FailReason)
{
    public static ProjectBackgroundView? Of(Project project) =>
        project.Background is { } b
            ? new ProjectBackgroundView(Name(b.Kind),
                b.Kind == ProjectBackgroundKind.Generated ? b.TileFile : null, b.FailReason)
            : null;

    public static string Name(ProjectBackgroundKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>
/// Генерация фона проекта: промпт по смыслу проекта → JSON с фигурами → собранный
/// сервером тайл (ADR-008). Любой сбой оставляет проект на стандартном фоне — пустого
/// экрана без фона не бывает ни в одном состоянии.
/// </summary>
public sealed class ProjectBackgroundService(
    ProjectManager projects, ICheapTextRunner cheap, ILogger<ProjectBackgroundService> log)
{
    // В модель уходят только имя и промпт проекта: место работает под учёткой владельца,
    // но модель может быть сторонним провайдером — фон не стоит расширения поверхности утечки
    private const int PromptBudget = 800;

    /// <param name="candidateOnly">
    /// Массовый прогон (ADR-008 §10): брать только проекты, которых генерация никогда не
    /// касалась, и протухший Pending. Generated / Standard / Failed не трогаются никогда —
    /// вызов на них возвращает текущее состояние, модель не дёргается.
    /// </param>
    public async Task<BackgroundResult> GenerateAsync(Project project, CancellationToken ct = default,
        bool candidateOnly = false)
    {
        if (!projects.TryBeginBackground(project.Id, candidatesOnly: candidateOnly))
            return Describe(projects.GetById(project.Id));

        string raw;
        try
        {
            raw = await cheap.RunAsync(LocalActionCatalog.ProjectBackground, BuildPrompt(project),
                ownerId: project.OwnerId, ct: ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Фон проекта {Project}: модель не ответила", project.Id);
            return Fail(project.Id, "no-model");
        }

        var tile = ProjectDoodleTile.Build(raw);
        if (!tile.Ok)
        {
            log.LogInformation("Фон проекта {Project}: ответ модели отвергнут ({Reason})",
                project.Id, tile.FailReason);
            return Fail(project.Id, tile.FailReason ?? "bad-json");
        }

        string fileName;
        try
        {
            fileName = await WriteTileAsync(project.Id, tile.Svg!, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Фон проекта {Project}: тайл не записался", project.Id);
            return Fail(project.Id, "io");
        }

        // Ссылка появляется в сторе только после успешного переименования временного файла
        var saved = projects.SetBackgroundGenerated(project.Id, fileName);

        // Цвет: одна точка истины — Icon.Color. Автоматический (null) заполняем молча,
        // выбранный руками не трогаем — только предлагаем замену через ответ API
        var colorApplied = false;
        var suggested = tile.ColorKey;
        if (suggested is not null)
        {
            if (saved.Icon.Color is null)
            {
                projects.Update(project.Id, name: null, rootPath: null, color: suggested);
                colorApplied = true;
                suggested = null;
            }
            else if (string.Equals(saved.Icon.Color, suggested, StringComparison.OrdinalIgnoreCase))
            {
                suggested = null;   // предлагать нечего — цвет уже такой
            }
        }

        return new BackgroundResult(ProjectBackgroundKind.Generated, fileName, suggested, colorApplied, null);
    }

    /// <summary>«Вернуть стандартный»: тайл удаляется, автопрогон проект больше не берёт.</summary>
    public BackgroundResult Reset(string projectId)
    {
        projects.SetBackgroundStandard(projectId);
        return new BackgroundResult(ProjectBackgroundKind.Standard, null, null, false, null);
    }

    private BackgroundResult Fail(string projectId, string reason)
    {
        var project = projects.SetBackgroundFailed(projectId, reason);
        return new BackgroundResult(project.Background?.Kind ?? ProjectBackgroundKind.Failed,
            project.Background?.TileFile, null, false, reason);
    }

    private static BackgroundResult Describe(Project? project) =>
        new(project?.Background?.Kind ?? ProjectBackgroundKind.Pending,
            project?.Background?.TileFile, null, false, project?.Background?.FailReason);

    private async Task<string> WriteTileAsync(string projectId, string svg, CancellationToken ct)
    {
        var dir = Path.Combine(projects.BackgroundsDir, projectId);
        Directory.CreateDirectory(dir);
        // Имя иммутабельно (guid как cache-busting) — приём icon-{guid:N} из ProjectsController
        var name = $"tile-{Guid.NewGuid():N}.svg";
        var tmp = Path.Combine(dir, name + ".tmp");
        await File.WriteAllTextAsync(tmp, svg, new UTF8Encoding(false), ct);
        File.Move(tmp, Path.Combine(dir, name), overwrite: true);
        return name;
    }

    // Промпт: просим ДАННЫЕ, а не разметку. Запреты перечислены явно, чтобы модель не
    // тратила на них бюджет вывода — схема их всё равно не принимает
    private static string BuildPrompt(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты придумываешь фоновый узор для рабочего пространства проекта — россыпь мелких");
        sb.AppendLine("контурных символов (дудлов) по смыслу проекта: у финансов это монеты, графики и чеки,");
        sb.AppendLine("у поездок — чемоданы, самолёты и карты, у кода — скобки, терминал и ветки git.");
        sb.AppendLine();
        sb.AppendLine($"Проект называется «{project.Name}».");
        if (!string.IsNullOrWhiteSpace(project.SystemPrompt))
        {
            var about = project.SystemPrompt.Trim();
            sb.AppendLine("О чём этот проект (из его настроек):");
            sb.AppendLine(about.Length <= PromptBudget ? about : about[..PromptBudget] + "…");
        }
        sb.AppendLine();
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом такого вида, без пояснений и без markdown-обвязки:");
        sb.AppendLine("""
            {
              "colorKey": "green",
              "shapes": [
                { "x": 18, "y": 20, "rotate": -7,
                  "paths": ["M0 0h34v25H0z", "M7 9l4.5 4-4.5 4M17 17h10"],
                  "circles": [{ "cx": 5, "cy": 5, "r": 4 }] }
              ]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Правила:");
        sb.AppendLine("- colorKey — ровно один ключ из: " + string.Join(", ", ProjectDoodleTile.ColorKeys) + ".");
        sb.AppendLine($"- shapes — от {ProjectDoodleTile.MinShapes} до {ProjectDoodleTile.MaxShapes} фигур"
            + " (лучше 12), равномерно рассыпанных по площади без наложений.");
        sb.AppendLine("- x, y — левый верхний угол фигуры на холсте 260×260, от 0 до 250, не больше одного");
        sb.AppendLine("  знака после запятой; фигура рисуется от своего нуля и должна умещаться в 40×40,");
        sb.AppendLine("  то есть x + размер фигуры не больше 250 (то же для y). rotate — от -12 до 12.");
        sb.AppendLine($"- paths — до {4} строк атрибута d, каждая не длиннее {ProjectDoodleTile.MaxPathLength} символов;");
        sb.AppendLine("  команды только M m L l H h V v C c S s Q q T t A a Z z, числа обычные (0.5, а не .5");
        sb.AppendLine("  и не 5e2), не больше трёх цифр до и после точки.");
        sb.AppendLine($"- circles — до {4} кругов: r от 1 до 30, cx и cy от -2 до 46.");
        sb.AppendLine("- В каждой фигуре хотя бы один путь или круг. Круги рисуй кругом, а не дугой A.");
        sb.AppendLine("- Только контуры: НИКАКИХ fill, opacity, style, stroke, text, градиентов и вообще");
        sb.AppendLine("  никакой SVG-разметки в ответе — цвет и толщину линий задаёт сам продукт.");
        return sb.ToString();
    }
}
