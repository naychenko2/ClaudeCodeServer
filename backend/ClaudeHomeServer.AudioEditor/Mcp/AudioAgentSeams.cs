using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Jobs;

namespace ClaudeHomeServer.Services.AudioEditor.Mcp;

// Швы тулсета агента под этапы, которых в ветке ещё нет (ADR-021 §7): монтаж без ИИ (5.2) и библиотека
// «Голоса» (7.1). Схема инструментов фиксирована уже сейчас — ни состав, ни описание не поменяются, когда
// реализации появятся; пока шва нет, вызов отвечает внятным отказом, а не пропадает из tools/list.

// Монтаж без ИИ над версией нити (обрезка, фейды и громкость, нормализация, сведение стемов): итог —
// новая версия нити, бесплатно и без очереди GPU. Range — кусок в секундах, Params — параметры операции
// (проверяет реализация)
public interface IAudioAgentEdits
{
    Task<AudioEditCallResult<AudioAgentEditDto>> ApplyAsync(string ownerId, AudioEditScope scope, string sessionId,
        string threadId, string? versionId, AudioOp op, double? startSec, double? endSec, JsonObject? parameters,
        CancellationToken ct);
}

public sealed record AudioAgentEditDto(string ThreadId, string VersionId);

// Голоса проекта из библиотеки «Голоса»: slug — то, что агент передаёт в voice у audio_generate
public interface IAudioVoiceLibrary
{
    IReadOnlyList<AudioLibraryVoice> List(string ownerId, AudioEditScope scope);
}

public sealed record AudioLibraryVoice(string Slug, string Name, string? Description);
