using System.Diagnostics;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Git-фикстура для хостовых тестов общих проектов. Добавление участника делает git init корня
/// (ProjectMembershipService), а гейт F-4 (file_uncommitted) отказывает редактору на файле,
/// которого нет в коммите. В реальном общем проекте файлы уже закоммичены, поэтому фикстурные
/// файлы тестов фиксируются коммитом ДО добавления участника.
/// </summary>
public static class GitFixture
{
    /// <summary>Коммитит всё содержимое каталога (init при необходимости). Идентичность задана
    /// ключами -c, чтобы тест не зависел от глобальной настройки git на машине (CI).</summary>
    public static void CommitAll(string dir, string message = "fixture")
    {
        Run(dir, "init", "-q");
        Run(dir, "add", "-A");
        Run(dir, "-c", "user.name=fixture", "-c", "user.email=fixture@example.com",
            "commit", "-q", "--allow-empty", "-m", message);
    }

    private static void Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000)) throw new InvalidOperationException("git не завершился: " + string.Join(' ', args));
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} в {dir}: {err}");
    }
}
