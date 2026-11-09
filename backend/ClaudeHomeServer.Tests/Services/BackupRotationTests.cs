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

    [Fact]
    public void НесколькоАрхивовВСутки_ОстаётсяОдинНаДень_ОкноТриДня()
    {
        // 6 архивов за сегодня (выкатки) и по одному за каждый из предыдущих дней:
        // за день остаётся один (самый свежий), окно — последние 3 дня, глубже — удаляется
        var today = Enumerable.Range(0, 6)
            .Select(i => new BackupRotation.Candidate($"t{i}.zip", new DateTime(2026, 7, 25, 23 - i, 0, 0)));
        var history = Enumerable.Range(1, 6).Select(Day);
        var all = today.Concat(history).ToList();

        var deleted = BackupRotation.SelectForDeletion(all);

        deleted.Should().BeEquivalentTo(new[]
        {
            "t1.zip", "t2.zip", "t3.zip", "t4.zip", "t5.zip",
            "ccs-3.zip", "ccs-4.zip", "ccs-5.zip", "ccs-6.zip",
        });
    }
}
