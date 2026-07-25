using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Реестр процессов: учёт по «паспорту» (PID + имя + время старта). Проверяем ровно то,
// что раньше ломалось молча: вытеснение протухшей записи при переиспользовании PID
// системой, отсев завершившихся и отказ признавать своим чужой процесс с тем же номером.
// KillAll здесь не гоняется намеренно — он убил бы сам тестовый процесс.
public class ProcessRegistryTests
{
    // PID заведомо больше системных пределов (Linux max ~4 194 304, Windows меньше)
    private const int PhantomPid = 2_000_000_000;

    [Fact]
    public void Register_СтавитПроцессНаУчёт()
    {
        using var self = Process.GetCurrentProcess();
        try
        {
            ProcessRegistry.Register(self);
            ProcessRegistry.IsTracked(self.Id).Should().BeTrue();
        }
        finally { ProcessRegistry.Unregister(self); }
    }

    [Fact]
    public void Unregister_СнимаетСУчёта()
    {
        using var self = Process.GetCurrentProcess();
        ProcessRegistry.Register(self);
        ProcessRegistry.Unregister(self);
        ProcessRegistry.IsTracked(self.Id).Should().BeFalse();
    }

    [Fact]
    public void Register_ПротухшаяЗаписьПодТемЖеPid_Вытесняется()
    {
        using var self = Process.GetCurrentProcess();
        // Номер тот же, но «паспорт» от давно умершего процесса — так выглядит
        // переиспользование PID системой. Раньше TryAdd оставлял старую запись,
        // и живой процесс молча оставался вне учёта.
        ProcessRegistry.TrackForTests(
            new ProcessRegistry.TrackedProcess(self.Id, "древний-процесс", DateTime.Now.AddDays(-1)));

        try
        {
            ProcessRegistry.Register(self);

            ProcessRegistry.TryGetTracked(self.Id, out var entry).Should().BeTrue();
            entry!.Name.Should().Be(self.ProcessName);
        }
        finally { ProcessRegistry.Unregister(self); }
    }

    [Fact]
    public void PruneDead_ВычёркиваетЗавершившиеся()
    {
        ProcessRegistry.TrackForTests(
            new ProcessRegistry.TrackedProcess(PhantomPid, "фантом", DateTime.Now));

        ProcessRegistry.PruneDead();

        ProcessRegistry.IsTracked(PhantomPid).Should().BeFalse();
    }

    [Fact]
    public void PruneDead_ЖивогоНеТрогает()
    {
        using var self = Process.GetCurrentProcess();
        ProcessRegistry.Register(self);
        try
        {
            ProcessRegistry.PruneDead();
            ProcessRegistry.IsTracked(self.Id).Should().BeTrue();
        }
        finally { ProcessRegistry.Unregister(self); }
    }

    [Fact]
    public void Matches_СвойПроцесс_Да()
    {
        using var self = Process.GetCurrentProcess();
        var entry = new ProcessRegistry.TrackedProcess(self.Id, self.ProcessName, self.StartTime);

        ProcessRegistry.Matches(entry, self).Should().BeTrue();
    }

    [Fact]
    public void Matches_ЧужойПроцессПодТемЖеPid_Нет()
    {
        using var self = Process.GetCurrentProcess();
        var byName = new ProcessRegistry.TrackedProcess(self.Id, "чужак", self.StartTime);
        var byTime = new ProcessRegistry.TrackedProcess(self.Id, self.ProcessName, self.StartTime.AddHours(-1));

        ProcessRegistry.Matches(byName, self).Should().BeFalse();
        ProcessRegistry.Matches(byTime, self).Should().BeFalse();
    }

    [Fact]
    public void Matches_ВремяСтартаНеизвестно_СверкаПоИмени()
    {
        using var self = Process.GetCurrentProcess();
        var entry = new ProcessRegistry.TrackedProcess(self.Id, self.ProcessName, DateTime.MinValue);

        ProcessRegistry.Matches(entry, self).Should().BeTrue();
    }
}
