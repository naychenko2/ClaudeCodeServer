namespace ClaudeHomeServer.Services.Git;

// Человек-автор коммита в рабочем дереве проекта (S3-1, «Общие проекты»).
// Живёт в Core по той же причине, что и GitRefIdentity: автор конструируется на стороне
// вызывающего (GitController, MCP git_commit, автокоммит — S3-1b), а вертикаль Git не должна
// знать ни пользователя, ни его реестр: здесь ровно три строки, ссылок на другие вертикали нет.
// Email `{Username}@users.invalid` — техническая оболочка для git, не адрес для связи:
// .invalid — зарезервированный домен (RFC 2606), письмо на него не доставляется.
// Источник правды о человеке — трейлер `CCS-User: <UserId>` в теле коммита, а не email:
// email может не совпасть (чужой git config, amend), а трейлер читается тем же форматом,
// что и CCS-Session/CCS-Task (CommitTrailers).
public sealed record GitCommitAuthor(string UserId, string DisplayName, string Username);
