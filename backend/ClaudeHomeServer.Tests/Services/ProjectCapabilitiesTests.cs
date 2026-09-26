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
        caps.Transcript.Should().Match<ProjectCapabilityGroup>(g => g.Host == CapabilityHost.Server && g.Available);
        ProjectCapabilities.TranscriptOnServer(Server()).Should().BeTrue();
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
        // Транскрипт CLI на устройстве: механики, читающие его с диска сервера, выключены с причиной
        caps.Transcript.Host.Should().Be(CapabilityHost.Off);
        caps.Transcript.Available.Should().BeFalse();
        caps.Transcript.Reason.Should().Be(ProjectCapabilities.TranscriptOnDeviceReason);
        caps.Transcript.Features.Should().BeEquivalentTo(
            [ProjectFeatures.LiveSubagents, ProjectFeatures.WorkflowView, ProjectFeatures.ChatBranch]);
        ProjectCapabilities.TranscriptOnServer(Local()).Should().BeFalse();
        caps.Exec.Available.Should().BeTrue();
    }

    [Fact]
    public void ГруппыПокрываютВсеКлючиФичРовноОдинРаз()
    {
        var caps = ProjectCapabilities.For(Server(), null);
        var all = caps.Files.Features.Concat(caps.Platform.Features).Concat(caps.ServerContent.Features)
            .Concat(caps.Transcript.Features).ToList();

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

    // --- Устаревший агент (agent-distribution AD-6): отказ хода и чтения, фоновые ждут ---

    private static DeviceExecStatus Agent(string version, DeviceAgentUpdate? update = null) =>
        Device(caps: [DeviceCapabilities.Exec, DeviceCapabilities.Files, DeviceCapabilities.Relay])
            with { AgentVersion = version, AgentUpdate = update };

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+0a1b2c3d")]
    [InlineData(" 1.0.0 ")]
    [InlineData("1.207.0")]
    public void АгентНеНижеМинимума_ХодИЧтениеРазрешены(string version)
    {
        var device = Agent(version);

        device.AgentOutdated.Should().BeFalse();
        device.CanExec.Should().BeTrue();
        ProjectCapabilities.For(Local(), device).Exec.Available.Should().BeTrue();
        ProjectCapabilities.BackgroundGate(Local(), device).IsReady.Should().BeTrue();
        ProjectCapabilities.RelayRefusal(Local(), device).Should().BeNull();
    }

    [Theory]
    [InlineData("0.9.99")]
    [InlineData("0.1.0")]
    [InlineData("1.0.0-dirty.20260926120000")]
    [InlineData("мусор")]
    public void АгентНижеМинимума_ХодИЧтениеОтказывают(string version)
    {
        var device = Agent(version);

        device.AgentOutdated.Should().BeTrue();
        device.CanExec.Should().BeFalse();
        var exec = ProjectCapabilities.For(Local(), device).Exec;
        exec.Available.Should().BeFalse();
        exec.Reason.Should().StartWith(DeviceAgentCompatibility.NotReadyPrefix).And.Contain("обновите агента");
        ProjectCapabilities.RelayRefusal(Local(), device).Should().Be(exec.Reason);
    }

    [Fact]
    public void КлиентРукБезВерсииАгента_НеСчитаетсяУстаревшим()
    {
        DeviceAgentCompatibility.IsOutdated(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(DeviceAgentUpdateStates.Downloading)]
    [InlineData(DeviceAgentUpdateStates.WaitingIdle)]
    public void BackgroundGate_УстаревшийАгентОбновляется_ЖдатьУстройство(string state)
    {
        var gate = ProjectCapabilities.BackgroundGate(Local(), Agent("0.9.0", new DeviceAgentUpdate(state, "1.207.0")));

        gate.Verdict.Should().Be(ProjectBackgroundVerdict.WaitDevice);
        gate.Reason.Should().StartWith(DeviceAgentCompatibility.NotReadyPrefix)
            .And.Contain("агент обновляется").And.Contain("1.207.0")
            .And.NotContain("обновите агента");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(DeviceAgentUpdateStates.Idle, null)]
    [InlineData(DeviceAgentUpdateStates.Failed, "нет места на диске")]
    public void BackgroundGate_УстаревшийАгентБезОбновления_ЖдатьУстройствоСПросьбойОбновить(string? state, string? reason)
    {
        var update = state is null ? null : new DeviceAgentUpdate(state, null, reason);
        var gate = ProjectCapabilities.BackgroundGate(Local(), Agent("0.9.0", update));

        gate.Verdict.Should().Be(ProjectBackgroundVerdict.WaitDevice, "фоновые механизмы ждут устройство, а не падают");
        gate.Reason.Should().StartWith(DeviceAgentCompatibility.NotReadyPrefix)
            .And.Contain("обновите агента").And.Contain(DeviceAgentCompatibility.MinVersion)
            .And.NotContain("агент обновляется");
        if (reason is not null) gate.Reason.Should().Contain(reason);
    }
}
