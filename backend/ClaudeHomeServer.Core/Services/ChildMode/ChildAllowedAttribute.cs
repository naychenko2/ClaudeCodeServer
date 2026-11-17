namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Метка эндпоинта «это разрешено ребёнку» (ADR-041, гейт И1). Allow-list с отказом по умолчанию:
/// владельцу с детским профилем <c>ChildModeGateMiddleware</c> пропускает только эндпоинты с этой
/// метаданной, всё остальное — 403 <c>child_mode</c>. Перечня запретов нет и не заводится.
/// Список размеченных эндпоинтов заморожен в сторожe <c>ChildModeEndpointGuardTests</c>:
/// новая метка без правки сторожа краснит тест.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class ChildAllowedAttribute : Attribute;

/// <summary>
/// Метка SPA-фолбэка рядом с <see cref="ChildAllowedAttribute"/>: фолбэк совпадает с ЛЮБЫМ путём без
/// своего эндпоинта, поэтому ребёнку он открыт только на GET/HEAD к корню интерфейса. Иначе ребёнок
/// с Bearer прошёл бы гейт в ветки конвейера после него (<c>/telemetry-proxy</c>, OnlyOffice, статика модулей).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class ChildSpaFallbackAttribute : Attribute;
