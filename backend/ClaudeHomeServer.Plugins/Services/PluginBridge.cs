using System.Text.Json;

namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Мост панели v1 (ADR-031 §3): диспетчер операций <see cref="IPluginCapability"/>.
/// Вертикали-владельцы (Notes, Tasks) регистрируют обработчики своих прав; мост вызывает
/// их после проверки, что право операции есть в granted. Направление: Notes → Core ← Plugins,
/// прямой ссылки вертикаль → вертикаль нет (ADR-014, инвариант 10).
///
/// Stateless — singleton; список обработчиков фиксируется при регистрации (состав
/// tools/list не зависит от хода, ADR-012).
/// </summary>
public class PluginBridge
{
    private readonly IReadOnlyList<IPluginCapability> _capabilities;

    public PluginBridge(IEnumerable<IPluginCapability> capabilities)
    {
        _capabilities = capabilities.ToList();
    }

    /// <summary>
    /// Диспетчер: найти обработчик операции {область}.{глагол} по домену (часть до первой
    /// '.'), проверить, что его право есть в granted, вызвать.
    /// Нет обработчика → <c>unavailable</c> (503, «обработчик не зарегистрирован»);
    /// право не в granted → <c>denied</c> (403).
    /// </summary>
    /// <param name="context">Владелец + id плагина + контекст слота (ADR-031 §3.3, M8).</param>
    /// <param name="capability">Операция: {область}.{глагол} (например notes.read).</param>
    /// <param name="args">Аргументы операции (сырой JSON; null — нет аргументов).</param>
    /// <param name="granted">Выданные права установки (проверка на каждом вызове, ADR-031 §4).</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<PluginCapabilityResult> DispatchAsync(
        PluginCallContext context, string capability, JsonElement? args,
        IReadOnlyList<string> granted, CancellationToken ct)
    {
        // Домен операции: часть до первой '.' (notes.read → notes).
        var domain = capability.Split('.').FirstOrDefault() ?? "";
        if (domain.Length == 0)
            return new PluginCapabilityResult(false,
                Error: new PluginCallError("invalid", $"Некорректная операция: {capability}"));

        // Обработчик: его Permission начинается с "{domain}:" (notes:read, tasks:read, net:{host}).
        var handler = _capabilities.FirstOrDefault(
            c => c.Permission.StartsWith(domain + ":", StringComparison.Ordinal));
        if (handler is null)
            return new PluginCapabilityResult(false,
                Error: new PluginCallError("unavailable", $"Обработчик не зарегистрирован: {domain}"));

        // Проверка granted (ADR-031 §4): право обработчика должно быть в выданных.
        if (!granted.Contains(handler.Permission, StringComparer.Ordinal))
            return new PluginCapabilityResult(false,
                Error: new PluginCallError("denied", $"Нет права: {handler.Permission}"));

        return await handler.HandleAsync(context, capability, args ?? default, ct);
    }
}
