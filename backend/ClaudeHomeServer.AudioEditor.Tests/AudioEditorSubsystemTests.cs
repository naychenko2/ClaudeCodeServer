using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.AudioEditor.Tests;

public class AudioEditorSubsystemTests
{
    // Ключ подсистемы обязан совпасть с ключом записи DynamicModules: по нему ModuleLoader читает
    // гейт Subsystems:AudioEditor, а фронт сверяет ActiveKeys
    [Fact]
    public void Ключ_подсистемы_совпадает_с_ключом_динамического_модуля()
    {
        new AudioEditorSubsystem().Key.Should().Be("audioeditor");
    }

    // Поставщик fal заведён драйвером звука и отдаётся как IAudioEngine
    [Fact]
    public void Регистрация_заводит_поставщика_fal()
    {
        var services = new ServiceCollection();

        new AudioEditorSubsystem().Register(services, new ConfigurationBuilder().Build());

        services.Should().Contain(d => d.ServiceType == typeof(FalAudioEngine));
        // local, Higgsfield, Яндекс, fal
        services.Count(d => d.ServiceType == typeof(IAudioEngine)).Should().Be(4);
    }

    // Монтаж без ИИ у агента: шов тулсета заведён реализацией поверх движка «Без ИИ»
    [Fact]
    public void Регистрация_заводит_монтаж_без_ИИ_у_агента()
    {
        var services = new ServiceCollection();

        new AudioEditorSubsystem().Register(services, new ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(ClaudeHomeServer.Services.AudioEditor.Mcp.IAudioAgentEdits)
            && d.ImplementationType == typeof(ClaudeHomeServer.Services.AudioEditor.Mcp.AudioAgentEdits));
    }
}
