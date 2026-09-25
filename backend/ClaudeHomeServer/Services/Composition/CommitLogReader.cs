using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.Services.Composition;

// Реализация ICommitLogReader: тонкая обёртка над FileService.GetCommitsRaw.
// Сам метод остаётся в FileService — у него другие вызывающие (см. комментарий
// к ICommitLogReader в Core); наша задача — развязать Changelog от Main, а не
// вычищать FileService.
public sealed class CommitLogReader(FileService files) : ICommitLogReader
{
    public List<GitCommitRaw> GetCommitsRaw(string rootPath, string projectName = "", int limit = 200,
        IReadOnlyDictionary<string, string>? authorAliases = null) =>
        files.GetCommitsRaw(rootPath, projectName, limit, authorAliases);
}
