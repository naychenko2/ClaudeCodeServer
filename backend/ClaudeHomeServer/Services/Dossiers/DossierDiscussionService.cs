using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Dossiers;

// Конспекты обсуждений (ADR-004 §6): структурированный протокол чата — решения,
// аргументы, отвергнутые варианты — снимается одним вызовом CheapTextRunner (ключ
// discussion-digest) и живёт в DossierDiscussionStore до выгрузки в ветку. Дословный
// транскрипт продуктового чата в репозитории убивает откровенность обсуждений и весит
// несоразмерно пользе — потому конспект, а не лента. Снятие происходит на экспорте
// (DossierGitExporter.ExportAsync → EnsureAsync): конспект без выгрузки не нужен, а
// экспорт без конспекта терял бы половину ценности ветки.
public sealed class DossierDiscussionService
{
    // Потолок ленты для промпта (ADR-004 §6): даже профиль Large не вмещает сырьё
    // целиком — реплики обсуждения ≤ 40 000 символов, обрезка с НАЧАЛА: развязка
    // (решения, договорённости) важнее завязки.
    internal const int MaxFeedChars = 40_000;

    private readonly DossierDiscussionStore _digests;
    private readonly DossierStore _dossiers;
    private readonly SessionManager _sessions;
    private readonly ICheapTextRunner _cheap;
    private readonly InstanceSecretsProvider _secrets;
    private readonly ILogger<DossierDiscussionService>? _log;

    public DossierDiscussionService(DossierDiscussionStore digests, DossierStore dossiers,
        SessionManager sessions, ICheapTextRunner cheap, InstanceSecretsProvider secrets,
        ILogger<DossierDiscussionService>? log = null)
    {
        _digests = digests;
        _dossiers = dossiers;
        _sessions = sessions;
        _cheap = cheap;
        _secrets = secrets;
        _log = log;
    }

    public IReadOnlyList<DossierDiscussionRecord> List(string ownerId, string projectId) =>
        _digests.List(ownerId, projectId);

    // Снять недостающие конспекты чатов проекта: сырьё — только чаты, у которых есть
    // хоть один паспорт (связь по sessionId; чат без паспорта в летопись не входит и
    // конспекта не заслуживает). Дедуп — наличие записи в сторе. Ошибка одного чата
    // не роняет остальные и не роняет экспорт паспортов: записи без успеха не бывает,
    // конспект догонит следующим экспортом.
    public async Task EnsureAsync(string ownerId, Project project, CancellationToken ct = default)
    {
        var sessionIds = _dossiers.List(ownerId, project.Id)
            .Where(d => !string.IsNullOrEmpty(d.SessionId))
            .Select(d => d.SessionId!)
            .Distinct()
            .ToList();

        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            try { await EnsureOneAsync(ownerId, project, sessionId, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.LogWarning(ex,
                    "dossiers: конспект чата {Session} проекта {Project} не снялся — догонит следующим экспортом",
                    sessionId, project.Id);
            }
        }
    }

    private async Task EnsureOneAsync(string ownerId, Project project, string sessionId, CancellationToken ct)
    {
        if (_digests.Get(ownerId, project.Id, sessionId) is not null) return;

        var session = _sessions.GetById(sessionId);
        if (session is null) return;
        // Те же предохранители, что у паспортов (§6): чат этого владельца и проекта,
        // opt-out «не включать в летопись» уважается и здесь — модель не зовём вовсе
        if (!DossierCaptureService.ShouldCaptureSession(
                DossierCaptureService.SessionBelongsToProject(
                    _sessions.ResolveOwnerId(session), session.ProjectId, ownerId, project.Id),
                session.ExcludeFromDossiers))
            return;

        var history = await _sessions.GetHistoryAsync(sessionId);
        var feed = BuildFeed(history, MaxFeedChars);
        if (feed.Length == 0) return;   // лента пуста — снимать нечего, запись не заводим

        // Редакция ДО модели (секрет в ленте не уходит в промпт) и ПОСЛЕ (модель могла
        // процитировать то, что просочилось мимо первого прохода) — контракт выжимки паспорта
        var secrets = _secrets.GetExactSecrets();
        feed = SecretRedactor.Redact(feed, secrets);

        var raw = await _cheap.RunAsync(
            LocalActionCatalog.DiscussionDigest, BuildPrompt(session.Name, feed), "haiku", ownerId, ct: ct);
        var content = SecretRedactor.Redact((raw ?? "").Trim(), secrets);
        if (content.Length == 0) throw new InvalidOperationException("модель вернула пустой конспект");

        _digests.Set(ownerId, project.Id, new DossierDiscussionRecord(
            sessionId, session.Name ?? "", content, DateTimeOffset.UtcNow));
        _log?.LogInformation("dossiers: конспект чата {Session} проекта {Project} снят", sessionId, project.Id);
    }

    // Лента чата для конспекта: реплики пользователя и ответы ассистента — костяк
    // обсуждения; thinking, вызовы инструментов, файлы и тексты сабагентов не входят —
    // это протокол хода, а не позиция участника. Переполнение потолка — хвост ленты
    // (обрезка с начала, см. MaxFeedChars).
    internal static string BuildFeed(IReadOnlyList<StoredMessage> messages, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            switch (m)
            {
                case StoredUserMessage u when !string.IsNullOrWhiteSpace(u.Text):
                    sb.AppendLine("Пользователь:");
                    sb.AppendLine(u.Text.Trim());
                    sb.AppendLine();
                    break;
                case StoredTextMessage { ParentToolUseId: null } t when !string.IsNullOrWhiteSpace(t.Text):
                    sb.AppendLine("Ассистент:");
                    sb.AppendLine(t.Text.Trim());
                    sb.AppendLine();
                    break;
            }
        }
        var text = sb.ToString().Trim();
        if (text.Length <= maxChars) return text;
        return text[^maxChars..].TrimStart();
    }

    internal static string BuildPrompt(string? sessionName, string feed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ниже — лента рабочего обсуждения из чата. Составь по ней СТРУКТУРИРОВАННЫЙ КОНСПЕКТ " +
                      "для ветки «История решений»: о чём шло обсуждение, какие решения приняли и на каких " +
                      "аргументах, какие варианты отвергли и почему, какие требования и ограничения прозвучали.");
        sb.AppendLine("Пиши по-русски, сжато, в markdown (заголовки ##, списки). Это протокол сути, а НЕ " +
                      "пересказ ленты: не иди реплика за репликой и не копируй дословно. Не выдумывай факты, " +
                      "которых нет в ленте. Ответь ТОЛЬКО текстом конспекта, без вступлений и пояснений.");
        if (!string.IsNullOrWhiteSpace(sessionName))
            sb.AppendLine("Название чата: " + sessionName.Trim());
        sb.AppendLine();
        sb.AppendLine("Лента обсуждения:");
        sb.AppendLine(feed);
        return sb.ToString();
    }
}
