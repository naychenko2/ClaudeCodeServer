using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Turn;

// Устав сферы (markdown сферы проекта чата) — СТАБИЛЬНАЯ секция системного блока: устав меняется
// только правкой сферы и не зависит от текста хода, поэтому prefix cache она не рвёт и при
// RecallInTurnText остаётся в системном блоке, а не уезжает хвостом.
//
// Гейт — «проект сессии состоит в сфере с непустым уставом» при включённом флаге spheres:
// и то и другое решает справочник сфер (ISphereDirectory.SphereOf/CharterOf отдают null иначе).
public sealed class SphereCharterContributor(ISphereDirectory spheres) : IPromptSectionContributor
{
    public string Key => "sphere-charter";
    public string Title => "Устав сферы";
    // После трейлера истории решений (100), до блоков recall (200+)
    public int Order => 150;
    public string Group => "project";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is not null && sessionContext.Session.ProjectId is not null;

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        var owner = sessionContext.OwnerId!;
        if (spheres.SphereOf(owner, sessionContext.Session.ProjectId!) is not { } sphereId
            || spheres.CharterOf(owner, sphereId) is not { } charter)
            return Task.FromResult<PromptSectionContribution?>(null);

        var name = spheres.SphereName(owner, sphereId);
        var text = $"Этот проект входит в сферу «{name ?? "—"}». Устав сферы — договорённости, " +
            $"общие для всех её проектов:\n\n{charter.Trim()}";
        return Task.FromResult<PromptSectionContribution?>(
            new PromptSectionContribution([new PromptSection(Key, text)]));
    }
}
