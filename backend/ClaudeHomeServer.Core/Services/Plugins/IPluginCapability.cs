namespace ClaudeHomeServer.Services.Plugins;

// Шов «обработчик операций одного права» (ADR-031 §2.4, §3.4, §4).
//
// Вертикаль-владелец данных регистрирует по одному обработчику на своё право:
// Notes — notes:read, Tasks — tasks:read. Вертикаль Plugins (мост) вызывает обработчик
// после проверки, что право операции есть в granted. Направление: Notes -> Core <- Plugins,
// прямой ссылки вертикаль -> вертикаль нет (ADR-014).
//
// Право проверяет сервер на каждом вызове, без кэша (ADR-031 §4): отозвал право — со
// следующего вызова denied.
public interface IPluginCapability
{
    // Право, за которое отвечает обработчик (например "notes:read").
    string Permission { get; }

    // Обработать операцию моста {область}.{глагол}, требующую этого права.
    // context — контекст слота (projectId, id объекта); op — имя операции; args — аргументы.
    Task<PluginCapabilityResult> HandleAsync(PluginCallContext context, string op, object? args, CancellationToken ct);
}

/// <summary>
/// Контекст слота, который хост добавляет к вызову (ADR-031 §3.3): только то, что слот
/// явно отдаёт — id проекта и id выбранного объекта, если он есть. Стор, JWT и данные
/// других владельцев сюда не попадают.
/// </summary>
public sealed record PluginCallContext(string ProjectId, string? ObjectId);

/// <summary>
/// Итог операции для моста: <see cref="Ok"/> + <see cref="Data"/> (успех) либо
/// <see cref="Error"/> (сбой). Хост пересылает это во фрейм как <c>result</c>.
/// </summary>
public sealed record PluginCapabilityResult(bool Ok, object? Data = null, PluginCallError? Error = null);

/// <summary>Ошибка операции: код (denied/not_found/invalid/unavailable/upstream/timeout/limit) + текст.</summary>
public sealed record PluginCallError(string Code, string Message);
