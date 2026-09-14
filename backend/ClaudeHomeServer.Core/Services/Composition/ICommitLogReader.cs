using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Шов чтения git-лога для вертикали «Что нового» (`Services.Changelog`):
// ChangelogService собирает коммиты продукта для ежедневной сводки. Метод живёт
// в `FileService.GetCommitsRaw` и не имеет ничего общего с файловыми операциями
// (там запуск `git log` через `Process.Start` с разбором через unit/record-separator).
//
// Контракт узкий: один метод под единственный потребитель. Адаптер в Main
// (`CommitLogReader`) тонкий и просто пробрасывает вызов в `FileService`.
// Сам `GetCommitsRaw` из `FileService` НЕ переносим — у него другие вызывающие,
// это отдельная уборка.
//
// Возврат `Models.GitCommitRaw` (DTO Core) — без зависимости на полную модель `Project`,
// чтобы вертикаль Changelog не тащила чужие модели в шов.
public interface ICommitLogReader
{
    List<GitCommitRaw> GetCommitsRaw(string rootPath, string projectName = "", int limit = 200,
        IReadOnlyDictionary<string, string>? authorAliases = null);
}
