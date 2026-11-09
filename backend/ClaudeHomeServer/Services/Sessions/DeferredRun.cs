namespace ClaudeHomeServer.Services.Sessions;

// Общий статический хелпер fire-and-forget (S3-0 §3 «что сняли переносом, а не швом»):
// опущен в Services.Sessions как статик без состояния и конструктора, потому что его первые
// носители — SessionHistory (SavePlanCardAsync) и CliRoute (TryPoolFailover), а вызывать его
// должен и остающийся в SessionManager код (штаб, процесс, восстановление). Дублировать
// хелпер в каждой роли или звать фасад — то самое «никогда от SessionManager», которого
// решение не допускает. Тело перенесено дословно из SessionManager, включая префикс лога.
internal static class DeferredRun
{
    // Для fire-and-forget задач: ошибку логируем, а не теряем молча
    internal static void FireAndForget(Task task, string context) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[SessionManager] {context}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
}
