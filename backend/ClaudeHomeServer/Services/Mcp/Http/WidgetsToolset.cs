using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Виджеты чата (widget_show) поверх HTTP-транспорта — первый переехавший сервер (ADR-012).
/// Раньше это был mcp/widgets-server на node; в API он не ходил никогда, только валидировал
/// input, поэтому перенос ничего не теряет, а ход перестаёт поднимать ради него процесс.
///
/// Сам HTML рендерит фронт (WidgetView) из аргументов вызова — sandbox-iframe в ленте чата,
/// ответ инструмента лишь подтверждает показ.
/// </summary>
public sealed class WidgetsToolset : IMcpToolset
{
    // Лимит размера html: учит модель ретраиться компактнее. Input уже улетел в историю
    // до валидации — от первого раздутого вызова историю лимит не спасает (фронт имеет
    // свой защитный cap на рендер).
    private const int MaxHtml = 64 * 1024;
    private const int MaxTitle = 120;
    private const int MinHeight = 120;
    private const int MaxHeight = 1200;

    public string Name => "widgets";
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> Tools { get; } =
    [
        new McpToolSchema(
            "widget_show",
            "Показать пользователю интерактивный HTML-виджет прямо в ленте чата: дашборд, график, "
            + "таблицу, калькулятор, мини-игру. HTML должен быть self-contained: все стили и скрипты "
            + "inline, внешние ресурсы (CDN, картинки по URL, шрифты, fetch) заблокированы песочницей. "
            + "Виджет отображается сразу — не дублируй его содержимое текстом.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "html" },
                ["properties"] = new JsonObject
                {
                    ["html"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Self-contained HTML-фрагмент (inline CSS/JS, без внешних ресурсов "
                            + "и без <html>/<head>/<body>). Лимит 64 КБ.",
                    },
                    ["title"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Короткий заголовок карточки виджета (до 120 символов)",
                    },
                    ["height"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = MinHeight,
                        ["maximum"] = MaxHeight,
                        ["description"] = "Желаемая высота в px (опционально; иначе подстроится автоматически)",
                    },
                },
            }),
    ];

    public Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments, McpToolCallContext context,
        CancellationToken ct)
    {
        if (tool != "widget_show")
            throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));

        var html = StringArg(arguments, "html");
        if (string.IsNullOrWhiteSpace(html))
            return Task.FromResult(new McpToolCallResult(
                "Поле html пустое — передай self-contained HTML-фрагмент виджета.", IsError: true));
        if (html.Length > MaxHtml)
        {
            var kb = (int)Math.Round(html.Length / 1024.0);
            return Task.FromResult(new McpToolCallResult(
                $"HTML виджета слишком большой ({kb} КБ, лимит {MaxHtml / 1024} КБ) — упрости разметку "
                + "или сократи данные.", IsError: true));
        }

        var rawTitle = StringArg(arguments, "title");
        var title = (rawTitle.Length > MaxTitle ? rawTitle[..MaxTitle] : rawTitle).Trim();
        return Task.FromResult(new McpToolCallResult(
            $"Виджет {(title.Length > 0 ? $"«{title}» " : "")}показан пользователю в ленте чата. "
            + "НЕ дублируй его содержимое текстом — при необходимости добавь 1-2 предложения комментария."));
    }

    // Строковый аргумент вызова: нестроковое значение (число, объект) — это «не передали»,
    // а не исключение: модель получит понятный отказ валидации, а не разрыв вызова
    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
