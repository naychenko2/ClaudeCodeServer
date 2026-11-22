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

/// <summary>
/// Метка эндпоинта «нужен читаемый детский профиль» (ADR-041 ред. 9, Г14-д1): ребёнок с нечитаемым
/// профилем (<c>DenyAll</c>) получает от <c>ChildModeGateMiddleware</c> детский отказ
/// 503 <c>policy_unavailable</c> ДО тела ручки — платная операция не оплачена, запись не написана.
/// Маркер стоит на платных операциях и записи вне «своего экрана»: синтез речи, голос чата,
/// согласие правил, запись заметок/файлов/задач. Читающие ручки, bootstrap и стоп-сигнал не
/// помечены намеренно: ребёнок без настроенных занятий читает свои предметы-предметы и может
/// позвать взрослого. Пара «список [ChildAllowed] + список [ChildRequiresProfile]» заморожена
/// в стороже <c>ChildModeEndpointGuardTests</c>: новая метка без правки сторожа краснит тест.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class ChildRequiresProfileAttribute : Attribute;
