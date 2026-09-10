using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Dossiers;

// Владение веткой-паспортом ccs/dossiers/v1: имя рефа, его remote-зеркало и фиксированная
// идентичность коммита. Это контрактное владение Dossiers — GitService реализует generic
// plumbing через IGitRefSnapshotStore и сам про имя ветки и user.name НЕ знает.
//
// Все операции экспорта/импорта идут через IGitRefSnapshotStore с этими константами как
// аргументами. Сторонняя вертикаль, которой понадобится собственная ветка-паспорт,
// заводит свой клон этого класса и зовёт те же методы со своими значениями.
// Публичный, а не internal: константы ветки зовёт `DossiersController` (Main) — потребитель
// из ДРУГОЙ сборки после выноса вертикали в свой `.csproj` (Этап 5, волна 3). Открывать Main
// весь internal вертикали через `InternalsVisibleTo` ради одного типа было бы шире, чем нужно.
public static class DossierBranch
{
    // Полный ref ветки экспорта паспортов изменений.
    public const string Ref = "refs/heads/ccs/dossiers/v1";

    // Remote-tracking реф той же ветки: фолбэк, когда локальной ветки нет (репо стянули
    // fetch'ем/клонировали, но ветку у себя не создавали). ResolveRefAsync принимает обе
    // и выбирает ту, что реально существует.
    public static string RemoteRef => $"refs/remotes/origin/{Ref["refs/heads/".Length..]}";

    // Фиксированная идентичность коммитов ветки: commit-tree берёт автора из env/конфига,
    // а user.name на сервере может быть не задан («empty ident name not allowed»).
    public static readonly GitRefIdentity Identity = new(
        AuthorName: "AI Home",
        AuthorEmail: "dossiers@ai-home.local",
        CommitterName: "AI Home",
        CommitterEmail: "dossiers@ai-home.local");
}
