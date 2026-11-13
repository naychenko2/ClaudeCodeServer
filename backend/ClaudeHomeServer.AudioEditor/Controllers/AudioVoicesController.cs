using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Библиотека «Голоса» проекта (ADR-021 §2): список, голос из записей (загрузка и файлы проекта),
// переименование и расшифровка, образцы, удаление, модель RVC из версии нити обучения. Гейт — общий с
// модулем (флаг, свой проект — иначе 404); локальный проект отсекает атрибут FileBound до тела ручки, а
// VoiceLibrary ещё раз — до диска. Тело ручек — AudioVoiceEndpoints (общее с личным чатом, ADR-040).
// Пересоздание клона MiniMax — только этой ручкой, в две фазы: без quoteId — котировка с ценой, с quoteId
// той котировки — задача (кнопка человека). Id клона наружу не уходит: только состояние голоса.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/audio-editor/voices")]
public class AudioVoicesController(
    AudioEditScopeGate gate,
    VoiceLibrary voices,
    AudioJobThreads threads,
    AudioEditWorkspace workspace,
    AudioEditJobService jobs,
    IProjectFiles? files = null) : AudioVoiceEndpoints(voices, threads, workspace, jobs, files)
{
    [HttpGet]
    public IActionResult List(string projectId) =>
        Gate(projectId, out var scope, out var denied) ? ListIn(scope) : denied;

    [HttpGet("{slug}")]
    public IActionResult Get(string projectId, string slug) =>
        Gate(projectId, out var scope, out var denied) ? GetIn(scope, slug) : denied;

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBodyBytes)]
    public async Task<IActionResult> Create(string projectId, [FromForm] VoiceSamplesForm form, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await CreateIn(scope, form, ct) : denied;

    [HttpPost("rvc")]
    public IActionResult CreateFromRvc(string projectId, [FromBody] VoiceFromRvcRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SessionId))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Не указан чат");
        var sessionId = req.SessionId.Trim();
        if (!gate.TryProjectChat(UserId, projectId, sessionId, out var scope, out var denied)) return denied;
        return CreateFromRvcIn(scope, sessionId, req);
    }

    [HttpPatch("{slug}")]
    public IActionResult Update(string projectId, string slug, [FromBody] VoicePatchRequest req) =>
        Gate(projectId, out var scope, out var denied) ? UpdateIn(scope, slug, req) : denied;

    [HttpDelete("{slug}")]
    public IActionResult Delete(string projectId, string slug) =>
        Gate(projectId, out var scope, out var denied) ? DeleteIn(scope, slug) : denied;

    [HttpPost("{slug}/samples")]
    [RequestSizeLimit(MaxBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBodyBytes)]
    public async Task<IActionResult> AddSamples(string projectId, string slug, [FromForm] VoiceSamplesForm form, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await AddSamplesIn(scope, slug, form, ct) : denied;

    [HttpDelete("{slug}/samples/{file}")]
    public IActionResult RemoveSample(string projectId, string slug, string file) =>
        Gate(projectId, out var scope, out var denied) ? RemoveSampleIn(scope, slug, file) : denied;

    [HttpPost("{slug}/recreate")]
    public async Task<IActionResult> Recreate(string projectId, string slug, [FromQuery] string? provider,
        [FromQuery] string? quoteId, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await RecreateIn(scope, slug, provider, quoteId, ct) : denied;

    [HttpGet("{slug}/files/{file}")]
    public IActionResult VoiceFile(string projectId, string slug, string file) =>
        Gate(projectId, out var scope, out var denied) ? VoiceFileIn(scope, slug, file) : denied;

    private bool Gate(string projectId, [NotNullWhen(true)] out AudioEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProject(UserId, projectId, out scope, out denied);
}
