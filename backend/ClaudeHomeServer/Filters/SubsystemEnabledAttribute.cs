using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ClaudeHomeServer.Filters;

/// <summary>
/// Гейт выключенной вертикали на уровне действия: <c>Subsystems:{key}:Enabled=false</c> →
/// 503 с причиной (ADR-014 форма 3) РАНЬШЕ фильтров прав. Причина отказа на выключенной
/// вертикали — сама вертикаль, а не отсутствие объекта или прав: иначе
/// <see cref="ClaudeHomeServer.Services.Access.ProjectScopeAttribute"/> отвечал бы 404 на
/// несуществующий id и проба матрицы вертикалей мерила бы не гейт. Утечки нет: 503 одинаков
/// для любой персоны и ничего не говорит о её существовании.
/// Единственная точка чтения тумблера — <see cref="SubsystemGate.IsEnabled"/>. Форма тела
/// совпадает с прежним 503 вертикали images (фронт читает <c>queued</c>).
/// </summary>
/// <param name="key">Ключ подсистемы.</param>
/// <param name="title">Что выключено, для текста отказа («Подсистема … выключена»).</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SubsystemEnabledAttribute(string key, string title) : Attribute, IActionFilter, IOrderedFilter
{
    public string Key { get; } = key;

    // Раньше ProjectScope (int.MinValue + 2000): гейт вертикали идёт до разбора прав и объекта
    public int Order => int.MinValue + 1500;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (SubsystemGate.IsEnabled(config, Key)) return;

        context.Result = new ObjectResult(new
        {
            error = $"{title} (Subsystems:{Key}:Enabled=false)",
            queued = false,
        })
        { StatusCode = StatusCodes.Status503ServiceUnavailable };
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
