using ClaudeHomeServer.Services.Backup;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Состав архива и допустимость папки назначения. Обе вещи тихо опасны: лишний файл в
// архиве уезжает в облако (креденшалы), а неудачная папка либо утекает архив в песочницу
// и в базу знаний, либо кладёт бэкап внутрь того, что бэкапим.
public class BackupPathsTests
{
    [Theory]
    [InlineData("users.json")]
    [InlineData("sessions.json")]
    [InlineData("sessions/abc/history.json")]
    [InlineData("personas.json")]
    [InlineData("notes/user1/Заметка.md")]
    [InlineData("instance-id.txt")]
    [InlineData("handle-migration-v1.done")]
    [InlineData(".personas-project-bindings-migrated")]
    public void СтейтИМаркерыМиграций_ПопадаютВАрхив(string path)
    {
        BackupPaths.ShouldInclude(path).Should().BeTrue();
    }

    [Theory]
    // OAuth-токены аккаунтов — основной архив уезжает в облако
    [InlineData("claude-profiles/deepseek/.credentials.json")]
    [InlineData("sandbox-profiles/user1/.credentials.json")]
    // Синканные плагины и кеши профиля восстанавливаются сами
    [InlineData("claude-profiles/glm/plugins/omc/skill.md")]
    [InlineData("claude-profiles/glm/settings.json")]
    public void ВнутриПрофилей_ВсеКромеТранскриптов_Исключено(string path)
    {
        BackupPaths.ShouldInclude(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("claude-profiles/deepseek/projects/C--GIT-repo/abc.jsonl")]
    [InlineData("sandbox-profiles/user1/projects/-projects-demo/def.jsonl")]
    public void ТранскриптыПрофилей_ПопадаютВАрхив(string path)
    {
        // Без них у чатов сторонних провайдеров и песочницы не работает --resume,
        // а восстановить их больше неоткуда
        BackupPaths.ShouldInclude(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("logs/server.log")]
    [InlineData("sandbox-tmp/turn-1/mcp.json")]
    [InlineData("backups/ccs-old.zip")]
    [InlineData("backups-secrets/ccs-secrets-1.zip")]
    [InlineData(".backup-staging/users.json")]
    [InlineData("backup-state.json")]
    [InlineData("projects.json.abc123.tmp")]
    [InlineData("users.json.corrupt-20260101-000000.bak")]
    public void МусорИСлужебное_Исключены(string path)
    {
        BackupPaths.ShouldInclude(path).Should().BeFalse();
    }

    [Fact]
    public void РеестрPid_Исключен()
    {
        // Восстановленный список PID заставил бы следующий старт убить по протухшим
        // номерам всё, что зовётся claude/node — включая чужие dev-серверы на машине
        BackupPaths.ShouldInclude("server-pids.txt").Should().BeFalse();
    }

    [Theory]
    [InlineData("jwt-secret.txt")]
    [InlineData("vapid-keys.json")]
    [InlineData("module-keys.json")]
    public void Секреты_ВОсновнойАрхивНеПопадают(string path)
    {
        BackupPaths.ShouldInclude(path).Should().BeFalse();
    }

    // --- Валидация папки назначения ---

    private const string DataDir = @"C:\deploy\claude\data";
    private const string BaseDir = @"C:\deploy\claude";

    [Fact]
    public void ПапкаВнутриПроекта_Отклоняется()
    {
        // Файловый ватчер проиндексировал бы архивы документами в базу знаний Dify
        var error = BackupPaths.ValidateBackupPath(
            @"C:\GIT\repo\backups", DataDir, BaseDir, [@"C:\GIT\repo"], null);

        error.Should().NotBeNull();
    }

    [Fact]
    public void ПапкаВнутриПесочницы_Отклоняется()
    {
        // Корень песочницы монтируется в контейнер целиком — архив со всеми данными
        // всех пользователей стал бы читаемым изнутри
        var error = BackupPaths.ValidateBackupPath(
            @"C:\ClaudeSandbox\backups", DataDir, BaseDir, [], @"C:\ClaudeSandbox");

        error.Should().NotBeNull();
    }

    [Fact]
    public void ПапкаВнутриData_Отклоняется_КромеДефолтной()
    {
        BackupPaths.ValidateBackupPath(
            Path.Combine(DataDir, "my-backups"), DataDir, BaseDir, [], null)
            .Should().NotBeNull();

        BackupPaths.ValidateBackupPath(
            Path.Combine(DataDir, "backups"), DataDir, BaseDir, [], null)
            .Should().BeNull();
    }

    [Fact]
    public void ПапкаПриложения_Отклоняется()
    {
        // Деплой публикует поверх неё
        BackupPaths.ValidateBackupPath(BaseDir, DataDir, BaseDir, [], null)
            .Should().NotBeNull();
    }

    [Fact]
    public void СоседняяПапкаСПохожимИменем_НеСчитаетсяВложенной()
    {
        // Сравнение по сегментам, а не по префиксу строки: «...\dataBackup» лежит
        // рядом с «...\data», а не внутри
        BackupPaths.ValidateBackupPath(
            @"C:\deploy\claude\dataBackup", DataDir, BaseDir, [], null)
            .Should().BeNull();
    }

    [Fact]
    public void ОтносительныйПуть_Отклоняется()
    {
        BackupPaths.ValidateBackupPath(@"backups", DataDir, BaseDir, [], null)
            .Should().NotBeNull();
    }

    [Fact]
    public void ПапкаСекретов_ВнутриПриложения_Разрешена_ВнутриData_Нет()
    {
        // Дефолт секретов — подпапка каталога приложения: она переживает restore,
        // который уносит data целиком
        BackupPaths.ValidateSecretsPath(
            Path.Combine(BaseDir, "backups-secrets"), DataDir, BaseDir)
            .Should().BeNull();

        BackupPaths.ValidateSecretsPath(
            Path.Combine(DataDir, "backups-secrets"), DataDir, BaseDir)
            .Should().NotBeNull();
    }
}
