using ClaudeHomeServer.Services.Backup;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Ротация архивов. Цена ошибки несимметрична: лишний оставленный архив стоит мегабайт,
// а лишний удалённый — это удалённый бэкап.
public class BackupRotationTests
{
    private static BackupRotation.Candidate Day(int daysAgo) =>
        new($"ccs-{daysAgo}.zip", new DateTime(2026, 7, 25).AddDays(-daysAgo));

    [Fact]
    public void СвежиеДни_ОстаютсяВсе()
    {
        var candidates = Enumerable.Range(0, 5).Select(Day).ToList();

        BackupRotation.SelectForDeletion(candidates).Should().BeEmpty();
    }

    [Fact]
    public void СтарыеЕжедневные_УдаляютсяКромеНедельныхИМесячных()
    {
        var candidates = Enumerable.Range(0, 60).Select(Day).ToList();

        var deleted = BackupRotation.SelectForDeletion(candidates);
        var kept = candidates.Select(c => c.FileName).Except(deleted).ToList();

        // 7 дневных + по одному на каждую из 4 недель + по одному на 3 месяца,
        // с пересечениями — точное число зависит от календаря, важна вилка
        kept.Count.Should().BeInRange(7, BackupRotation.KeepDaily
            + BackupRotation.KeepWeekly + BackupRotation.KeepMonthly);
        deleted.Should().NotBeEmpty();
    }

    [Fact]
    public void СамыйСвежийАрхив_НеУдаляетсяНикогда()
    {
        var candidates = Enumerable.Range(0, 200).Select(Day).ToList();

        BackupRotation.SelectForDeletion(candidates).Should().NotContain(Day(0).FileName);
    }

    [Fact]
    public void ПоследнийАрхивКаждойНедели_Сохраняется()
    {
        // Ровно по одному архиву в неделю на протяжении трёх недель — удалять нечего
        var candidates = new List<BackupRotation.Candidate>
        {
            new("w0.zip", new DateTime(2026, 7, 25)),
            new("w1.zip", new DateTime(2026, 7, 18)),
            new("w2.zip", new DateTime(2026, 7, 11)),
        };

        BackupRotation.SelectForDeletion(candidates).Should().BeEmpty();
    }

    [Fact]
    public void НесколькоАрхивовВСутки_НеВытесняютИсториюПредыдущихДней()
    {
        // 6 архивов за сегодня (выкатки) и по одному за каждый из 7 предыдущих дней
        var today = Enumerable.Range(0, 6)
            .Select(i => new BackupRotation.Candidate($"t{i}.zip", new DateTime(2026, 7, 25, 23 - i, 0, 0)));
        var history = Enumerable.Range(1, 6).Select(Day);
        var all = today.Concat(history).ToList();

        var deleted = BackupRotation.SelectForDeletion(all);

        // за день остаётся один (самый свежий), каждый предыдущий день сохранён
        deleted.Should().BeEquivalentTo(new[] { "t1.zip", "t2.zip", "t3.zip", "t4.zip", "t5.zip" });
    }
}
