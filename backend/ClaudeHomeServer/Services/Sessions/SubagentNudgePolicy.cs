using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Sessions;

// Политика добивания оборванного сабагента (S3-4, docs/research/session-manager-split-2026-11.md):
// чистые функции без состояния и без зависимостей — вся политика в одном месте, под тестом
// (Llm.Tests/Services/SubagentNudgeTests.cs). Вынесено из SessionManager дословно; фасад
// держит тонкие делегирующие обёртки: на них сидят внешние вызовы (TaskExecutionService)
// и существующие тесты.
internal static class SubagentNudgePolicy
{
    // Потолок добиваний подряд. Две попытки — сознательный предел: бесконечный цикл добивания
    // хуже честного отказа, после него судьбу работы решает человек.
    internal const int MaxSubagentNudges = 2;

    /// <summary>
    /// Слать ли добивание оборванного сабагента. Чистая функция — вся политика в одном месте
    /// (под тестом). Уступаем всем, у кого свой протокол продолжения: цикл «до готово» и штаб
    /// продолжат ход сами, непустая очередь продолжит его сообщением, а параллельный ход цикла
    /// (LoopTurnInFlight) уже в пути — второй systemDirective ушёл бы в тот же процесс.
    ///
    /// <paramref name="isInterruptedRun"/> — признак обрыва класса «убит прерыванием хода»
    /// (паспорт FinishedBy == "interrupted"): такие обрывы счётчик MaxSubagentNudges не
    /// расходуют (причина в нашем коде и не свидетельствует о неисправном агенте), а
    /// автоматически слать сообщение «продолжи» означало бы действовать за человека — он
    /// сам нажал «Стоп», решение о продолжении принадлежит ему.
    /// </summary>
    internal static bool ShouldNudgeSubagent(int nudgesSent, bool workLoopActive, bool teamActive,
        bool hasPending, bool loopTurnInFlight, bool isInterruptedRun = false) =>
        !isInterruptedRun && nudgesSent < MaxSubagentNudges
        && !workLoopActive && !teamActive && !hasPending && !loopTurnInFlight;

    /// <summary>
    /// Ход координатора в полёте: сообщение ему уже отдано в процесс и result ещё не пришёл.
    /// Пока это так, второй systemDirective-ход слать нельзя (он уйдёт в тот же процесс) —
    /// добивание ждёт конца хода, координатору достаётся только пометка.
    /// </summary>
    internal static bool TurnInFlight(SessionStatus status) =>
        status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting;

    /// <summary>
    /// Снимает ли штатный отчёт агента серию добиваний. Счётчик общий на сессию, а агентов
    /// в ходе несколько — поэтому серию закрывает ТОТ ЖЕ агент, которого добивали (либо любой,
    /// пока серии нет). Чужой отчёт счётчик не трогает: иначе потолок MaxSubagentNudges
    /// не достигается вовсе и чат крутит системные директивы автономно.
    /// </summary>
    internal static bool ResetsNudgeSeries(string? nudgeAgentId, string reportedAgentId) =>
        nudgeAgentId is null || nudgeAgentId == reportedAgentId;

    /// <summary>
    /// Начинает ли оборвавшийся агент новую серию: попытки считаются per-agentId, поэтому
    /// другой агент получает свои две, а не остаток чужих.
    /// </summary>
    internal static bool StartsNudgeSeries(string? nudgeAgentId, string truncatedAgentId) =>
        nudgeAgentId != truncatedAgentId;

    /// <summary>
    /// Гасит ли штатный отчёт агента пометку обрыва. Пометка бывает ложной: bg_agent_done
    /// обгоняет дозапись финала в транскрипт, и по хвосту tool_use агент числится оборванным,
    /// хотя дописал end_turn. Опровергает пометку только отчёт ТОГО ЖЕ агента.
    /// </summary>
    internal static bool RefutesTruncation(string? markedAgentId, string reportedAgentId) =>
        markedAgentId == reportedAgentId;
}
