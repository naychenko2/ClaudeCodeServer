using System.Text.Json;

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
    // context — владелец + id плагина (из JWT) + контекст слота; op — имя операции;
    // args — аргументы операции (сырой JSON из моста, M8).
    Task<PluginCapabilityResult> HandleAsync(PluginCallContext context, string op, JsonElement args, CancellationToken ct);
}

/// <summary>
/// Контекст вызова моста (ADR-031 §3.3, M8): <see cref="OwnerId"/> и <see cref="PluginId"/>
/// — из JWT (владелец, чей плагин, и сам плагин), <see cref="ProjectId"/>/<see cref="ObjectId"/>
/// — контекст слота (проект и выбранный объект, если есть). Стор, JWT и данные других
/// владельцев сюда не попадают.
/// </summary>
public sealed record PluginCallContext(string OwnerId, string PluginId, string? ProjectId, string? ObjectId);

/// <summary>
/// Итог операции для моста: <see cref="Ok"/> + <see cref="Data"/> (успех) либо
/// <see cref="Error"/> (сбой). Хост пересылает это во фрейм как <c>result</c>.
/// </summary>
public sealed record PluginCapabilityResult(bool Ok, object? Data = null, PluginCallError? Error = null);

/// <summary>Ошибка операции: код (denied/not_found/invalid/unavailable/upstream/timeout/limit) + текст.</summary>
public sealed record PluginCallError(string Code, string Message);
