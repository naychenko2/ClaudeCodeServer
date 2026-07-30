namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Файловые утилиты для уборки в тестах.
/// </summary>
public static class TestFs
{
    /// <summary>
    /// Рекурсивно удаляет временную директорию, переживая гонку с in-flight записью на диск.
    ///
    /// <para>Проблема: тесты, гоняющие реальный ход сессии (напр. <c>SendMessageAsync</c>),
    /// оставляют фоновую запись истории чата (<c>history.json</c>) — она fire-and-forget и
    /// может дописать файл уже после того, как <c>Directory.Delete(recursive)</c> перечислил
    /// содержимое. На Windows тайминги это скрывают, а на Linux (CI) уборка падает с
    /// <c>IOException: Directory not empty</c>. Простой одноразовый Delete тут хрупок.</para>
    ///
    /// <para>Поэтому — несколько попыток с короткой паузой: в промежутке in-flight запись
    /// добегает, и следующая попытка застаёт директорию уже стабильной. Не смогли снести за
    /// все попытки — молча оставляем temp-мусор: это уборка, а не суть теста, и ронять
    /// зелёный прогон из-за неё нельзя.</para>
    /// </summary>
    public static void DeleteDirectoryResilient(string path, int attempts = 10, int delayMs = 50)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Кто-то ещё дописывает файл в директорию — ждём и пробуем снова
                if (i == attempts - 1) return;
                Thread.Sleep(delayMs);
            }
        }
    }
}
