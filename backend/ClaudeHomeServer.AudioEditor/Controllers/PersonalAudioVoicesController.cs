using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Библиотека «Голоса» личного чата: voices/ в папке «Личного» (ADR-040 Ф2-5), тело ручек общее с проектом
// (AudioVoiceEndpoints). Чтение папку не создаёт: Find через гейт, без папки список пуст. Создаёт её
// только добавление голоса (запись, RVC) — PersonalFolderResolver.EnsureAsync; правка и удаление
// несуществующего голоса остаются 404 и мутации диска не делают. Файлов проекта у личного чата нет —
// образцы только загрузкой.
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/audio-editor/chats/{sessionId}/voices")]
public class PersonalAudioVoicesController(
    AudioEditScopeGate gate,
    PersonalFolderResolver personal,
    VoiceLibrary voices,
    AudioJobThreads threads,
    AudioEditWorkspace workspace,
    AudioEditJobService jobs) : AudioVoiceEndpoints(voices, threads, workspace, jobs, files: null)
{
    [HttpGet]
    public IActionResult List(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? ListIn(scope) : denied;

    [HttpGet("{slug}")]
    public IActionResult Get(string sessionId, string slug) =>
        Gate(sessionId, out var scope, out var denied) ? GetIn(scope, slug) : denied;

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBodyBytes)]
    public async Task<IActionResult> Create(string sessionId, [FromForm] VoiceSamplesForm form, CancellationToken ct)
    {
        var (scope, denied) = await GateWriteAsync(sessionId, ct);
        return scope is null ? denied! : await CreateIn(scope, form, ct);
    }

    [HttpPost("rvc")]
    public async Task<IActionResult> CreateFromRvc(string sessionId, [FromBody] VoiceFromRvcRequest req, CancellationToken ct)
    {
        var (scope, denied) = await GateWriteAsync(sessionId, ct);
        return scope is null ? denied! : CreateFromRvcIn(scope, sessionId, req);
    }

    [HttpPatch("{slug}")]
    public IActionResult Update(string sessionId, string slug, [FromBody] VoicePatchRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? UpdateIn(scope, slug, req) : denied;

    [HttpDelete("{slug}")]
    public IActionResult Delete(string sessionId, string slug) =>
        Gate(sessionId, out var scope, out var denied) ? DeleteIn(scope, slug) : denied;

    [HttpPost("{slug}/samples")]
    [RequestSizeLimit(MaxBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBodyBytes)]
    public async Task<IActionResult> AddSamples(string sessionId, string slug, [FromForm] VoiceSamplesForm form, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await AddSamplesIn(scope, slug, form, ct) : denied;

    [HttpDelete("{slug}/samples/{file}")]
    public IActionResult RemoveSample(string sessionId, string slug, string file) =>
        Gate(sessionId, out var scope, out var denied) ? RemoveSampleIn(scope, slug, file) : denied;

    [HttpPost("{slug}/recreate")]
    public async Task<IActionResult> Recreate(string sessionId, string slug, [FromQuery] string? provider,
        [FromQuery] string? quoteId, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await RecreateIn(scope, slug, provider, quoteId, ct) : denied;

    [HttpGet("{slug}/files/{file}")]
    public IActionResult VoiceFile(string sessionId, string slug, string file) =>
        Gate(sessionId, out var scope, out var denied) ? VoiceFileIn(scope, slug, file) : denied;

    private bool Gate(string sessionId, [NotNullWhen(true)] out AudioEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryPersonalChat(UserId, sessionId, out scope, out denied);

    // Для добавления голоса: если папки «Личного» ещё нет — создать её; не вышло — отказ
    private async Task<(AudioEditScope? Scope, IActionResult? Denied)> GateWriteAsync(string sessionId, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return (null, denied);
        if (scope.HasFolder) return (scope, null);
        if (await personal.EnsureAsync(scope.TenantOwnerId, ct) is not { } project)
            return (null, Error(StatusCodes.Status409Conflict, PersonalFolderResolver.UnavailableCode, PersonalFolderResolver.UnavailableText));
        return (scope with { Project = project }, null);
    }
}
