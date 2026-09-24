namespace ClaudeHomeServer.Services.Composition;

// Шов доставки событий ватчера в веб-морду (ADR-016, задача 4.2): у локального проекта
// дерево наблюдает агент устройства, донесение приходит в хаб устройств (вертикаль
// Desktop), а в браузер уходит тем же методом «filesChanged» той же project-группы, что у
// серверного FileWatcherService. Реализация — в Main рядом с SessionHub.
public interface IProjectFilesChangedNotifier
{
    Task FilesChangedAsync(string projectId, IReadOnlyList<string> paths, bool full);
}
