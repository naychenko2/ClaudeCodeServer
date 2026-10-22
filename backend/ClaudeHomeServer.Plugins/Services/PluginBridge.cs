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
    /// право не в granted → <c>denied</c> (403): точное право названо ключом,
    /// параметризованное (<c>net:</c>) — хостом запроса (см. <see cref="DeniedError"/>).
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
        // Точное право (notes:read) — Contains; параметризованное (net:{host}, Permission
        // = префикс "net:") — любое выданное право с этим префиксом. Точное сверение
        // хоста делает сам обработчик (у него granted).
        var covered = handler.Permission.EndsWith(":", StringComparison.Ordinal)
            ? granted.Any(g => g.StartsWith(handler.Permission, StringComparison.Ordinal))
            : granted.Contains(handler.Permission, StringComparer.Ordinal);
        if (!covered)
            return new PluginCapabilityResult(false, Error: DeniedError(domain, handler, args ?? default));

        return await handler.HandleAsync(context, capability, args ?? default, granted, ct);
    }

    // Формулировка отказа по праву. Точное право (notes:read) называем ключом — по нему
    // панель предлагает «Выдать…». Параметризованное (net:{host}, Permission = префикс
    // "net:") с пустым параметром человеку ничего не говорит: называем хост из аргументов
    // операции (для net.fetch — хост url), а если его не вытащить — осмысленный текст по
    // домену, а не «Нет права: net:». Хост — единственное, чего панель не знает: она его
    // и так запросила, URL целиком не несём.
    private static PluginCallError DeniedError(string domain, IPluginCapability handler, JsonElement args)
    {
        if (!handler.Permission.EndsWith(":", StringComparison.Ordinal))
            return new PluginCallError("denied", $"Нет права: {handler.Permission}");

        if (domain == "net")
        {
            var host = HostFromArgs(args);
            if (host is not null)
                return new PluginCallError("denied", $"Нет права: {handler.Permission}{host}");
            return new PluginCallError("denied", "Нет права на сетевые запросы");
        }

        return new PluginCallError("denied", $"Нет права на операции домена: {domain}");
    }

    // Хост из аргументов net.fetch: поле "url" (http/https), в нижнем регистре — как
    // грамматика хоста права net:{host} (PluginNetProxy.HostOf). Другая форма аргументов
    // или не-URL → null (отказ по домену, не по хосту).
    private static string? HostFromArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return null;
        if (!Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return host.Length > 0 ? host : null;
    }
}
