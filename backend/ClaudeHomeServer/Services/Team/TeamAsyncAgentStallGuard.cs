namespace ClaudeHomeServer.Services.Team;

// Чистая логика подавления гарда молчаливого тупика async-агентом (задача b63fd8ea).
// Гард живёт в TeamTurnCompletionService.HandleTeamTurnEndAsync: координатор завершил ход
// без маркера team:work/talk/escalate, и если при этом у прогона остался живой фоновый
// субагент (entry.Process.HasPendingBg == true), то это работа, а не тупик — координатор
// ждёт собственного результата, и следующий ход (пробуждение по task-notification) почти
// наверняка принесёт маркер. Это подавление продержалось в проде бессрочно: BgLingerTimeout
// грейс тишины, а не потолок длительности, и агент с heartbeat'ами мог висеть часами.
//
// Решение — СВОЙ потолок подавления, независимый от BgLingerTimeout. Метка
// AsyncAgentStallSince на SessionEntry фиксирует момент, когда гард ВПЕРВЫЕ увидел
// подавление для текущего захода в stalledStage; при каждом конце хода гард сравнивает
// длительность подавления с порогом. Как только async-агент уходит (HasPendingBg == false),
// метка обнуляется: новый всплеск считается с нуля, а не копит время от НЕсвязанного
// прошлого агента.
//
// Вынесена в отдельный статический класс по образцу ClaudeSession.ResolveWatchdog (вызовы
// напрямую тестируются без реального процесса/CLI).
internal static class TeamAsyncAgentStallGuard
{
    // 10 минут: больше потолка планирования (300 с у TeamPlanningInFlight — координатор мог
    // запустить разведку ДО планирования), но далеко от 30 минут BgLingerTimeout.
    // Пользователь видит карточку молчаливого тупика через 10 минут тишины async-агента,
    // а не через полчаса. 5 минут слишком мало (агент реально мог работать), 30 минут —
    // бессрочность, против которой и написан баг.
    public static readonly TimeSpan DefaultSuppressionTimeout = TimeSpan.FromMinutes(10);

    // Возвращает true, если гард молчаливого тупика должен молчать: async-агент жив И
    // длительность подавления ещё в окне порога. Подавление НЕ зависит от стадии —
    // решение о применимости гарда принимает вызывающий (по TeamImplementStage).
    //
    // Параметры вынесены явно: функция чистая, детерминированная по входам, легко
    // тестируется без реального процесса/CLI/DateTime.UtcNow.
    public static bool ShouldSuppress(bool hasAsyncAgent, DateTime? stallSince, DateTime now,
        TimeSpan timeout)
    {
        if (!hasAsyncAgent) return false;
        if (stallSince is null) return true;
        return (now - stallSince.Value) < timeout;
    }
}
