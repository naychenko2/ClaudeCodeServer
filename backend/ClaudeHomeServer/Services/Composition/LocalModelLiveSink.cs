using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Composition;

// Боевая рассылка живых событий локальной модели (флаг local-model-stats): тиковый снимок
// генерации (OnLive) и итог завершённого хода (OnTurn) — события ленты чата. Живёт в Main,
// а не в вертикали Llm: нужен веер сессий (IHubContext<SessionHub>) и проверка зрителей
// (SessionManager.HasViewers) — тот же приём, что у LocalHandsNotifier.
//
// Оба события ЭФЕМЕРНЫЕ: в history.json не пишутся (нет case в OnMessageAsync и
// StoredMessage) — после F5 их подхватит следующий снимок/REST.
//
// OnLive рассылает ТОЛЬКО в session-группу (не в project/user, как BroadcastSessionMessageAsync):
// чип живёт в чате, а 1 Гц-тики в остальные вкладки проекта не несут информации.
// Без воркспейс-зрителя (HasViewers) тик не шлём: событие одноразовое, догнать его нельзя.
public sealed class LocalModelLiveSink(
    IHubContext<SessionHub> hub,
    SessionManager sessions) : ILocalModelLiveSink
{
    public void OnLive(string sessionId, string phase, double? genTps, int? waitingTotal)
    {
        if (!sessions.HasViewers(sessionId)) return;
        _ = hub.Clients.Group(sessionId).SendAsync("message",
            new LocalModelLiveMessage(phase, genTps, waitingTotal) { SessionId = sessionId });
    }

    public void OnTurn(string sessionId, LocalModelTurnSummary summary)
    {
        if (!sessions.HasViewers(sessionId)) return;
        _ = hub.Clients.Group(sessionId).SendAsync("message",
            new LocalModelTurnStatsMessage(
                summary.GenTps,
                PrefillTps: null,
                summary.TtftMs,
                summary.QueueMsApprox,
                summary.CacheHitPerc,
                summary.OutTokens)
            { SessionId = sessionId });
    }
}
