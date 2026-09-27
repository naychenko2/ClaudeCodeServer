using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Диспетчер выхода устройства в онлайн (ADR-016, план §5): Hello устройства раздаётся всем
// механизмам фоновой работы, поминутный проход — тоже; сбой одного не лишает остальных.
public class DeviceOnlineDispatcherTests
{
    private sealed class RecordingHandler(bool fail = false) : IDeviceOnlineHandler
    {
        public TaskCompletionSource<(string Owner, string Device)> Online { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<DateTime> Sweeps { get; } = [];

        public Task OnDeviceOnlineAsync(string ownerId, string deviceId, CancellationToken ct = default)
        {
            Online.TrySetResult((ownerId, deviceId));
            return fail ? throw new InvalidOperationException("сбой обработчика") : Task.CompletedTask;
        }

        public Task SweepAsync(DateTime nowUtc, CancellationToken ct = default)
        {
            Sweeps.Add(nowUtc);
            return fail ? throw new InvalidOperationException("сбой обработчика") : Task.CompletedTask;
        }
    }

    private static (DeviceOnlineDispatcher Sut, RecordingHandler Failing, RecordingHandler Healthy) Build()
    {
        var failing = new RecordingHandler(fail: true);
        var healthy = new RecordingHandler();
        var services = new ServiceCollection()
            .AddSingleton<IDeviceOnlineHandler>(failing)
            .AddSingleton<IDeviceOnlineHandler>(healthy)
            .BuildServiceProvider();
        return (new DeviceOnlineDispatcher(services, NullLogger<DeviceOnlineDispatcher>.Instance), failing, healthy);
    }

    [Fact]
    public async Task УстройствоПредставилось_ВсеОбработчикиПолучаютСобытие()
    {
        var (sut, failing, healthy) = Build();

        await ((IDeviceConnectionObserver)sut).OnDeviceOnlineAsync(
            new DeviceConnection("conn-1", "owner-1", "dev-1", DateTimeOffset.UtcNow));

        (await healthy.Online.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(("owner-1", "dev-1"));
        failing.Online.Task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Проход_СбойОдногоОбработчикаНеОстанавливаетДругие()
    {
        var (sut, failing, healthy) = Build();
        var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        await sut.SweepAsync(now);

        failing.Sweeps.Should().ContainSingle();
        healthy.Sweeps.Should().ContainSingle().Which.Should().Be(now);
    }
}
