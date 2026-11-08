using ClaudeHomeServer.Services.Backup;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Ротация архивов. Цена ошибки несимметрична: лишний оставленный архив стоит гигабайты
// места, а лишний удалённый — это удалённый бэкап.
public class BackupRotationTests
{
    private static BackupRotation.Candidate Day(int daysAgo) =>
        new($"ccs-{daysAgo}.zip", new DateTime(2026, 7, 25).AddDays(-daysAgo));

    [Fact]
    public void МеньшеЛимита_ОстаютсяВсе()
    {
        var candidates = Enumerable.Range(0, BackupRotation.KeepLast).Select(Day).ToList();

        BackupRotation.SelectForDeletion(candidates).Should().BeEmpty();
    }

    [Fact]
    public void СверхЛимита_ОстаютсяТолькоПоследние()
    {
        var candidates = Enumerable.Range(0, 60).Select(Day).ToList();

        var deleted = BackupRotation.SelectForDeletion(candidates);
        var kept = candidates.Select(c => c.FileName).Except(deleted).ToList();

        kept.Should().BeEquivalentTo(
            Enumerable.Range(0, BackupRotation.KeepLast).Select(d => Day(d).FileName));
    }

    [Fact]
    public void ПорядокВходаНеВажен_РешаетВремяСоздания()
    {
        // Перемешанный вход: сортировка по имени или по позиции дала бы другой набор
        var candidates = new[] { 5, 0, 9, 2, 1, 7 }.Select(Day).ToList();

        BackupRotation.SelectForDeletion(candidates)
            .Should().BeEquivalentTo(new[] { 5, 9, 7 }.Select(d => Day(d).FileName));
    }

    [Fact]
    public void СамыйСвежийАрхив_НеУдаляетсяНикогда()
    {
        var candidates = Enumerable.Range(0, 200).Select(Day).ToList();

        BackupRotation.SelectForDeletion(candidates).Should().NotContain(Day(0).FileName);
    }
}
