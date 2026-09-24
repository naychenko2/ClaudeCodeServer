using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Матрица возможностей проекта (ADR-016 §4, задача 3.1): три группы и их состояние у
/// серверного и локального проекта, ключ папки «устройство + путь», условия привязки.
/// </summary>
public class ProjectCapabilitiesTests
{
    private static DeviceExecStatus Device(bool online = true, bool harnessReady = true, params string[] caps) =>
        new("dev-1", "home", online, "linux-x64", "1.0.0", "2.0.0", "2.0.0",
            caps.Length == 0 ? [DeviceCapabilities.Exec] : caps, harnessReady,
            harnessReady ? null : "Нет управляемой копии CLI");

    private static Project Server() => new() { Name = "s", RootPath = "/srv/p" };
    private static Project Local() => new() { Name = "l", RootPath = "/home/u/p", DeviceId = "dev-1" };

    [Fact]
    public void СерверныйПроект_ВсеГруппыНаСервереИДоступны()
    {
        var caps = ProjectCapabilities.For(Server(), null);

        caps.Host.Should().Be(CapabilityHost.Server);
        caps.DeviceId.Should().BeNull();
        caps.Files.Should().Match<ProjectCapabilityGroup>(g => g.Host == CapabilityHost.Server && g.Available);
        caps.Platform.Should().Match<ProjectCapabilityGroup>(g => g.Host == CapabilityHost.Server && g.Available);
        caps.ServerContent.Should().Match<ProjectCapabilityGroup>(g => g.Host == CapabilityHost.Server && g.Available);
        caps.Exec.Available.Should().BeTrue();
    }

    [Fact]
    public void ЛокальныйПроект_ТриГруппыADR()
    {
        var caps = ProjectCapabilities.For(Local(), Device(caps: [DeviceCapabilities.Exec, DeviceCapabilities.Files]));

        caps.Host.Should().Be(CapabilityHost.Device);
        caps.DeviceId.Should().Be("dev-1");
        caps.Files.Host.Should().Be(CapabilityHost.Device);
        caps.Files.Available.Should().BeTrue();
        caps.Platform.Host.Should().Be(CapabilityHost.Server);
        caps.Platform.Available.Should().BeTrue();
        caps.ServerContent.Host.Should().Be(CapabilityHost.Off);
        caps.ServerContent.Available.Should().BeFalse();
        caps.ServerContent.Reason.Should().NotBeNullOrEmpty();
        caps.Exec.Available.Should().BeTrue();
    }

    [Fact]
    public void ГруппыПокрываютВсеКлючиФичРовноОдинРаз()
    {
        var caps = ProjectCapabilities.For(Server(), null);
        var all = caps.Files.Features.Concat(caps.Platform.Features).Concat(caps.ServerContent.Features).ToList();

        all.Should().OnlyHaveUniqueItems();
        all.Should().Contain([ProjectFeatures.Files, ProjectFeatures.Git, ProjectFeatures.Terminal,
            ProjectFeatures.Chat, ProjectFeatures.Tasks, ProjectFeatures.Knowledge, ProjectFeatures.CodeGraph]);
    }

    [Fact]
    public void ЛокальныйПроект_УстройствоОфлайн_ХодИФайлыНедоступныСПричиной()
    {
        var caps = ProjectCapabilities.For(Local(), Device(online: false));

        caps.Exec.Available.Should().BeFalse();
        caps.Exec.Reason.Should().Be(ProjectCapabilities.DeviceOfflineReason);
        caps.Files.Available.Should().BeFalse();
        caps.Files.Reason.Should().Be(ProjectCapabilities.DeviceOfflineReason);
        // Чат и задачи видны отовсюду (ADR §5)
        caps.Platform.Available.Should().BeTrue();
    }

    [Fact]
    public void ЛокальныйПроект_УстройстваНет_ОтказСПричиной()
    {
        var caps = ProjectCapabilities.For(Local(), null);

        caps.Exec.Should().Be(new ProjectExecCapability(false, ProjectCapabilities.DeviceMissingReason));
        caps.Files.Available.Should().BeFalse();
    }

    [Fact]
    public void ЛокальныйПроект_ХарнесНеГотов_ПричинаОтУстройства()
    {
        var caps = ProjectCapabilities.For(Local(), Device(harnessReady: false));

        caps.Exec.Available.Should().BeFalse();
        caps.Exec.Reason.Should().Be("Нет управляемой копии CLI");
    }

    [Fact]
    public void ЛокальныйПроект_АгентБезFiles_ФайлыНедоступны()
    {
        var caps = ProjectCapabilities.For(Local(), Device());

        caps.Files.Available.Should().BeFalse();
        caps.Files.Reason.Should().Be(ProjectCapabilities.NoFilesReason);
        caps.Exec.Available.Should().BeTrue();
    }

    [Fact]
    public void КлючПапки_ОдинПутьНаСервереИУстройстве_РазныеКлючи()
    {
        var path = "/home/u/p";
        ProjectCapabilities.FolderKey(null, path).Should()
            .NotBe(ProjectCapabilities.FolderKey("dev-1", path));
        ProjectCapabilities.FolderKey("dev-1", path).Should()
            .NotBe(ProjectCapabilities.FolderKey("dev-2", path));
        ProjectCapabilities.FolderKey("dev-1", "C:\\Work\\X\\").Should()
            .Be(ProjectCapabilities.FolderKey("dev-1", "c:/work/x"));
    }

    [Fact]
    public void КлючЗнаний_УЛокальногоНет()
    {
        ProjectCapabilities.KnowledgeRoot(Server()).Should().Be("/srv/p");
        ProjectCapabilities.KnowledgeRoot(Local()).Should().BeNull();
    }

    [Theory]
    [InlineData("C:\\Work\\Proj\\", "C:\\Work\\Proj")]
    [InlineData("c:/work//proj", "c:\\work\\proj")]
    [InlineData("C:\\", "C:\\")]
    [InlineData("/home/u//p/", "/home/u/p")]
    public void ПутьНаУстройстве_Каноничный(string raw, string expected) =>
        ProjectCapabilities.NormalizeDevicePath(raw).Should().Be(expected);

    [Theory]
    [InlineData("relative/path")]
    [InlineData("C:relative")]
    [InlineData("/home/u/../etc")]
    [InlineData("C:\\a\\.\\b")]
    public void ПутьНаУстройстве_ОтносительныйИлиСТочками_Отказ(string raw) =>
        FluentActions.Invoking(() => ProjectCapabilities.NormalizeDevicePath(raw))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Привязка_БезФлага_Отказ() =>
        ProjectCapabilities.BindRefusal(false, Device()).Should().NotBeNull();

    [Fact]
    public void Привязка_БезExec_Отказ() =>
        ProjectCapabilities.BindRefusal(true, Device(caps: [DeviceCapabilities.Files]))
            .Should().Be(ProjectCapabilities.NoExecReason);

    [Fact]
    public void Привязка_УстройстваНет_Отказ() =>
        ProjectCapabilities.BindRefusal(true, null).Should().Be(ProjectCapabilities.DeviceMissingReason);

    [Fact]
    public void Привязка_ФлагИExec_ДажеОфлайн_Можно() =>
        ProjectCapabilities.BindRefusal(true, Device(online: false)).Should().BeNull();

    // --- Вердикт для фоновой работы (ADR-016, вариант А плана §5) ---

    [Fact]
    public void BackgroundGate_СерверныйПроект_Готов()
    {
        ProjectCapabilities.BackgroundGate(Server(), device: null).IsReady.Should().BeTrue();
    }

    [Fact]
    public void BackgroundGate_ГотовоеУстройство_Готов()
    {
        var gate = ProjectCapabilities.BackgroundGate(Local(), Device(caps: DeviceCapabilities.Exec));

        gate.IsReady.Should().BeTrue();
        gate.DeviceId.Should().Be("dev-1");
    }

    [Fact]
    public void BackgroundGate_ОфлайнИлиНеготовыйХарнес_Ждать()
    {
        var offline = ProjectCapabilities.BackgroundGate(Local(), Device(online: false, caps: DeviceCapabilities.Exec));
        offline.MustWait.Should().BeTrue();
        offline.Reason.Should().Be(ProjectCapabilities.DeviceOfflineReason);

        ProjectCapabilities.BackgroundGate(Local(), Device(harnessReady: false, caps: DeviceCapabilities.Exec))
            .MustWait.Should().BeTrue();
    }

    [Fact]
    public void BackgroundGate_УстройстваНет_ЖдатьНечего()
    {
        var gate = ProjectCapabilities.BackgroundGate(Local(), device: null);

        gate.Verdict.Should().Be(ProjectBackgroundVerdict.DeviceGone);
        gate.MustWait.Should().BeFalse();
    }
}
