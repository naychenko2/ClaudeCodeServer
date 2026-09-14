using ClaudeHomeServer.Services.Reader;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Reader;

public class ReaderQuotaServiceTests
{
    [Fact]
    public void Concurrency_ТретийСлот_Отклоняется()
    {
        var quota = new ReaderQuotaService();
        using var s1 = quota.TryAcquireConcurrency("owner");
        using var s2 = quota.TryAcquireConcurrency("owner");
        var s3 = quota.TryAcquireConcurrency("owner");

        s1.Should().NotBeNull();
        s2.Should().NotBeNull();
        s3.Should().BeNull();
    }

    [Fact]
    public void Concurrency_ОсвобождённыйСлот_МожноЗанятьСнова()
    {
        var quota = new ReaderQuotaService();
        var s1 = quota.TryAcquireConcurrency("owner");
        s1!.Dispose();

        using var s2 = quota.TryAcquireConcurrency("owner");
        s2.Should().NotBeNull();
    }

    [Fact]
    public void Concurrency_РазныеВладельцы_НеМешаютДругДругу()
    {
        var quota = new ReaderQuotaService();
        using var a1 = quota.TryAcquireConcurrency("a");
        using var a2 = quota.TryAcquireConcurrency("a");
        using var b1 = quota.TryAcquireConcurrency("b");

        a1.Should().NotBeNull();
        a2.Should().NotBeNull();
        b1.Should().NotBeNull();
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Rate_ПревышениеЛимитаВОкне_Отклоняется()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var quota = new ReaderQuotaService(time);

        for (var i = 0; i < ReaderQuotaService.MaxPerMinutePerOwner; i++)
            quota.TryAcquireRate("owner").Should().BeTrue();

        quota.TryAcquireRate("owner").Should().BeFalse();
    }

    [Fact]
    public void Rate_ПослеОкна_СчётчикСбрасывается()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var quota = new ReaderQuotaService(time);

        for (var i = 0; i < ReaderQuotaService.MaxPerMinutePerOwner; i++)
            quota.TryAcquireRate("owner").Should().BeTrue();
        quota.TryAcquireRate("owner").Should().BeFalse();

        time.Now = time.Now.AddMinutes(1).AddSeconds(1);
        quota.TryAcquireRate("owner").Should().BeTrue();
    }
}
