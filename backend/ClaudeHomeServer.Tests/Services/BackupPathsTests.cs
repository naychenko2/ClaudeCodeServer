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
    // Снимки промпта ходов — диагностический лог, восстанавливать нечего
    [InlineData("prompt-snapshots/chat1/1700000000000-abcd.json.gz")]
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
    public void СтатусыMcpСерверов_Исключены()
    {
        // Наблюдение, а не настройка: восстановленное из архива, оно описывает состояние
        // чужой машины в прошлом. Заново приедет из первого же system/init
        BackupPaths.ShouldInclude("mcp-status.json").Should().BeFalse();
        // А сам реестр серверов — настройка, он в архив едет (секретов в нём нет)
        BackupPaths.ShouldInclude("mcp-servers.json").Should().BeTrue();
    }

    [Fact]
    public void РеестрPid_Исключен()
    {
        // Восстановленный список PID заставил бы следующий старт убить по протухшим
        // номерам всё, что зовётся claude/node — включая чужие dev-серверы на машине
        BackupPaths.ShouldInclude("server-pids.txt").Should().BeFalse();
    }

    [Theory]
    [InlineData("code-graphs/ab12cd/cache/compilation.bin")]
    [InlineData("code-graphs/ab12cd/cache/sub/tree.bin")]
    public void КешCodeGraph_Исключён(string path)
    {
        // Кеш графа пересобирается из исходников — в облачный архив не едет
        BackupPaths.ShouldInclude(path).Should().BeFalse();
    }

    [Fact]
    public void ГрафCodeGraph_ПопадаетВАрхив()
    {
        // А вот сам graph.json невосстановим без перестроения Roslyn — его бэкапим
        BackupPaths.ShouldInclude("code-graphs/ab12cd/graph.json").Should().BeTrue();
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

    // Пути строим от временной папки, а не литералами «C:\…»: Path.IsPathRooted
    // платформозависим, и на Linux (там гоняется CI) Windows-путь абсолютным
    // не считается — валидация отвергала бы его раньше проверяемого правила,
    // а тесты «путь годен» падали бы по постороннему поводу.
    private static readonly string Root = Path.GetTempPath();
    private static readonly string BaseDir = Path.Combine(Root, "ccs-app");
    private static readonly string DataDir = Path.Combine(BaseDir, "data");
    private static readonly string ProjectRoot = Path.Combine(Root, "ccs-repo");
    private static readonly string SandboxRoot = Path.Combine(Root, "ccs-sandbox");

    [Fact]
    public void ПапкаВнутриПроекта_Отклоняется()
    {
        // Файловый ватчер проиндексировал бы архивы документами в базу знаний Dify
        var error = BackupPaths.ValidateBackupPath(
            Path.Combine(ProjectRoot, "backups"), DataDir, BaseDir, [ProjectRoot], null);

        error.Should().NotBeNull();
    }

    [Fact]
    public void ПапкаВнутриПесочницы_Отклоняется()
    {
        // Корень песочницы монтируется в контейнер целиком — архив со всеми данными
        // всех пользователей стал бы читаемым изнутри
        var error = BackupPaths.ValidateBackupPath(
            Path.Combine(SandboxRoot, "backups"), DataDir, BaseDir, [], SandboxRoot);

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
        // Сравнение по сегментам, а не по префиксу строки: «…/dataBackup» лежит
        // рядом с «…/data», а не внутри
        BackupPaths.ValidateBackupPath(
            Path.Combine(BaseDir, "dataBackup"), DataDir, BaseDir, [], null)
            .Should().BeNull();
    }

    [Fact]
    public void ОтносительныйПуть_Отклоняется()
    {
        BackupPaths.ValidateBackupPath("backups", DataDir, BaseDir, [], null)
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
