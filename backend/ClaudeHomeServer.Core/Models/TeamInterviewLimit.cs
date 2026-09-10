namespace ClaudeHomeServer.Models;

// Потолок раундов вопросов на одну вводную в режиме «Командная реализация» и проверка
// его исчерпания. Живёт в Core, а не в текстах штаба, потому что это КОНТРАКТ ДВУХ
// подсистем, а не формулировка: одно и то же правило применяют
//   - штаб (`Services.Team.TeamImplementPrompts`) — тем, ЧТО сказать модели
//     («раундов осталось N» / «лимит исчерпан, больше не спрашивай»);
//   - ход (`Services.Llm.Claude.ClaudeSession`) — тем, ЧТО реально позволить:
//     permission-гейт денаит AskUserQuestion сверх лимита, иначе 3-й раунд уходил бы
//     в карточку вопреки тексту.
// Пока значение и предикат лежали в `TeamImplementPrompts`, вертикаль Llm тянула
// вертикаль Team ради одной константы. `TeamImplementPrompts` теперь форвардит сюда,
// так что источник правды по-прежнему один.
public static class TeamInterviewLimit
{
    // Интервью — вход в работу, а не допрос (риск «интервью превращается в допрос»
    // из продуктового плана).
    public const int MaxRounds = 2;

    public static bool Exhausted(SessionTeamImplement team) => team.InterviewRounds >= MaxRounds;
}
