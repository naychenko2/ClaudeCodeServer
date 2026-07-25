using ClaudeHomeServer.Services.Backup;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Журнал бэкапов (data/backup-state.json) — единственный источник для виджета на главной:
// в папку архивов он не ходит (она в облаке и может спать). Поэтому писать журнал обязан
// КАЖДЫЙ снимок, кем бы он ни был запущен — таймером сервиса, кнопкой, меню трея или
// deploy80 (последние два — отдельные процессы через exe --backup). Иначе после ручного
// бэкапа виджет показывал бы «последний вчера», и выглядело бы это как «бэкапы не идут».
public class BackupStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-backup-state-" + Guid.NewGuid().ToString("N")[..8]);

    public BackupStateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private BackupResult MakeOk(string fileName, DateTime createdAt, int chats = 3)
    {
        var archive = Path.Combine(_dir, fileName);
        File.WriteAllText(archive, "архив");
        return new BackupResult(true, archive, null, new BackupManifest
        {
            CreatedAt = createdAt,
            Summary = new BackupSummary { Chats = chats },
        });
    }

    [Fact]
    public void УспешныйСнимок_ПопадаетВЖурнал()
    {
        BackupState.Record(_dir, MakeOk("ccs-1.zip", new DateTime(2026, 7, 25, 10, 0, 0)));

        var state = BackupState.Load(_dir);
        state.LastSuccessAt.Should().Be(new DateTime(2026, 7, 25, 10, 0, 0));
        state.LastError.Should().BeNull();
        state.Recent.Should().ContainSingle().Which.FileName.Should().Be("ccs-1.zip");
        state.Recent[0].Summary.Chats.Should().Be(3);
    }

    [Fact]
    public void ХранятсяТолькоТриПоследних_СвежийСверху()
    {
        for (var i = 1; i <= 5; i++)
            BackupState.Record(_dir, MakeOk($"ccs-{i}.zip", new DateTime(2026, 7, 20).AddDays(i)));

        var state = BackupState.Load(_dir);
        state.Recent.Should().HaveCount(BackupState.RecentLimit);
        state.Recent[0].FileName.Should().Be("ccs-5.zip");
        state.Recent[^1].FileName.Should().Be("ccs-3.zip");
    }

    [Fact]
    public void Ошибка_НеЗатираетДатуПоследнегоУспеха()
    {
        // Виджет должен показать и красный статус, и когда последний раз всё получилось:
        // «сломалось сегодня, но вчерашний архив есть» — это разные новости
        BackupState.Record(_dir, MakeOk("ccs-1.zip", new DateTime(2026, 7, 25, 10, 0, 0)));
        BackupState.Record(_dir, new BackupResult(false, null, "папка недоступна", null));

        var state = BackupState.Load(_dir);
        state.LastError.Should().Be("папка недоступна");
        state.LastSuccessAt.Should().Be(new DateTime(2026, 7, 25, 10, 0, 0));
        state.Recent.Should().ContainSingle();
    }

    [Fact]
    public void УспехПослеОшибки_ЧиститОшибку()
    {
        BackupState.Record(_dir, new BackupResult(false, null, "папка недоступна", null));
        BackupState.Record(_dir, MakeOk("ccs-2.zip", new DateTime(2026, 7, 25, 12, 0, 0)));

        BackupState.Load(_dir).LastError.Should().BeNull();
    }

    [Fact]
    public void ЖурналаНет_ЧитаетсяПустоеСостояние()
    {
        var state = BackupState.Load(_dir);

        state.LastSuccessAt.Should().BeNull();
        state.Recent.Should().BeEmpty();
    }
}
