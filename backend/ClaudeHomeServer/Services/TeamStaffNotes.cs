namespace ClaudeHomeServer.Services;

// Подписи плашек ⚑ (staffNote) молчаливых ходов координатора в чате-штабе. Эти строки
// шлются как user_message со staffNote=…; фронт гасит их в ленте набором suppressedByTeamNoise
// (см. frontend/src/components/ChatPanel.tsx) — показывать в ленте их не нужно: всё
// содержательное координатор уже сказал репликой персоны чата (карточку CoordinatorTurnCard
// убрали в пользу обычной реплики), а триггер нужен только как якорь для фазы и метрик.
// Контракт: каждый штабный триггер (WaveClosed / EscalationResolved / InterviewReturn)
// приходит именно как staffNote=true (без auto — авто-репорты исполнителей несут auto,
// но НЕ staffNote, и гасить их нельзя). Сторож — TeamStaffNotesTests.
public static class TeamStaffNotes
{
    // Волна закрыта: бэкенд отдал координатору факты закрытия, тот публикует сводку.
    public static string WaveClosed(int wave) => $"Волна {wave} закрыта — сводка передана координатору";

    // Человек ответил на карточку эскалации — решение уходит координатору обычным ходом.
    public const string EscalationResolved = "Ответ на карточку передан координатору";

    // Возврат из практики в интервью: дальше координатор задаёт вопросы человеку.
    public const string InterviewReturn = "Возврат в интервью — координатор задаст вопросы";
}
