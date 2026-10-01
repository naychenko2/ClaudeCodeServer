using ClaudeHomeServer.Services.AudioEditor;
using FluentAssertions;

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
}
