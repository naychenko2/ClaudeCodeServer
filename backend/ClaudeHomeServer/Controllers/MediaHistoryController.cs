using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.MediaHistory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// История медиа-генераций (ADR-030): 7 дней на диске, админ видит промпты, параметры,
// результаты и логин владельца. Обе ручки — [Authorize(Roles="admin")] (не-админ — 403) плюс
// серверная проверка флага local-model-stats (без флага — 404). Название чужого чата/проекта и
// переход в чат скрыты: наружу только логин.
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/local-model/history")]
public class MediaHistoryController(
    MediaHistoryIndex index,
    FeatureFlagService flags,
    IEnumerable<IMediaHistoryResultSource> sources,
    MediaHistoryThumbService thumbs) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Флаг local-model-stats у текущего пользователя; без него — 404 (UI-гейта мало: наружу чужие файлы)
    private bool Enabled() => UserId is { } id && flags.IsEnabled(id, FeatureFlagKeys.LocalModelStats);

    // Список: фильтры (вид, модель, исход, логин, период) + курсорная пагинация (50 на страницу)
    [HttpGet]
    public IActionResult List(
        [FromQuery] string? kind,
        [FromQuery] string? model,
        [FromQuery] string? outcome,
        [FromQuery] string? login,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? cursor,
        [FromQuery] int? limit)
    {
        if (!Enabled()) return NotFound();
        index.Load();
        var query = new MediaHistoryQuery
        {
            Kind = kind,
            Model = model,
            Outcome = outcome,
            Login = login,
            From = ParseDate(from),
            To = ParseDate(to),
            Cursor = cursor,
            Limit = limit ?? MediaHistoryQuery.PageLimit,
        };
        return Ok(index.Query(query));
    }

    // Файл результата: только id записи и номер варианта n (путь с клиента не приходит).
    // size=thumb — миниатюра (лениво), full — оригинал. 410 — файла нет (reason — причина).
    [HttpGet("{recordId}/file")]
    public async Task<IActionResult> File(string recordId, [FromQuery] int n = 0,
        [FromQuery] string size = "full", CancellationToken ct = default)
    {
        if (!Enabled()) return NotFound();
        // recordId с «..» или чужими символами — 404 (единственный способ отдать файл, не открыв
        // произвольное чтение диска)
        if (recordId.Length == 0 || recordId.Contains("..") || recordId.IndexOfAny(['/', '\\', ':']) >= 0)
            return NotFound();

        index.Load();
        var record = index.Get(recordId);
        if (record is null) return NotFound();

        var origin = record.Origin;
        if (origin is null) return Gone("missing");

        // Источник по origin.Source; у выключенной подсистемы реализации нет → source_disabled
        var source = sources.FirstOrDefault(s => s.Source == origin.Source);
        if (source is null) return Gone("source_disabled");

        var reference = new MediaHistoryRef(record.PromptId, origin.OwnerId, origin.ProjectId, origin.SourceJobId, origin.SessionId);
        var resolution = await source.ResolveAsync(reference, n, ct);
        if (resolution is null) return Gone("missing");
        if (n < 0 || n >= resolution.VariantCount) return NotFound();
        if (resolution.Reason is { } reason) return Gone(ReasonWire(reason));
        if (resolution.HostPath is not { } path || resolution.ContentType is not { } contentType)
            return Gone("missing");

        // size=thumb: миниатюра (лениво); отказ — отдаём оригинал, фронт рисует плитку
        if (size == "thumb")
        {
            var thumb = await thumbs.GetThumbAsync(recordId, n, path, contentType, ct);
            if (thumb is not null)
                return ServeFile(thumb, "image/webp");
        }

        return ServeFile(path, contentType);
    }

    // PhysicalFile с Range (звук/видео), inline, nosniff
    private IActionResult ServeFile(string path, string contentType)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = "inline";
        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    private IActionResult Gone(string reason) =>
        StatusCode(StatusCodes.Status410Gone, new { reason });

    // Wire-имена причин 410 (список открытый, фронт показывает незнакомую как «Файл недоступен»)
    private static string ReasonWire(MediaHistoryGoneReason reason) => reason switch
    {
        MediaHistoryGoneReason.Expired => "expired",
        MediaHistoryGoneReason.Missing => "missing",
        MediaHistoryGoneReason.ProjectGone => "project_gone",
        MediaHistoryGoneReason.LocalProject => "local_project",
        MediaHistoryGoneReason.SourceDisabled => "source_disabled",
        MediaHistoryGoneReason.NoResult => "no_result",
        _ => "missing",
    };

    private static DateTimeOffset? ParseDate(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, out var d) ? d : null;
}
