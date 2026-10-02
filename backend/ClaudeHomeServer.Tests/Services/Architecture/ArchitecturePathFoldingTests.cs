using ClaudeHomeServer.Services.Architecture;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// Единое правило свёртки путей в контейнер/компонент (ArchitecturePathFolding).
public class ArchitecturePathFoldingTests
{
    private static readonly ArchitecturePathFolding Folding = new(new[]
    {
        "backend/ClaudeHomeServer",
        "backend/ClaudeHomeServer.Core",
        "frontend",
        "frontend/modules/notes",
    });

    [Theory]
    [InlineData("backend/ClaudeHomeServer/Services/Auth/JwtService.cs", "backend/ClaudeHomeServer", "Services/Auth")]
    [InlineData("backend/ClaudeHomeServer.Core/Services/SafePath.cs", "backend/ClaudeHomeServer.Core", "Services")]
    [InlineData("backend/ClaudeHomeServer/Program.cs", "backend/ClaudeHomeServer", ArchitecturePathFolding.RootName)]
    // Глубже ComponentDepth не режем: всё под Services/CodeGraph — один компонент.
    [InlineData("backend/ClaudeHomeServer/Services/Mcp/Http/Deep/X.cs", "backend/ClaudeHomeServer", "Services/Mcp")]
    // src прозрачен.
    [InlineData("frontend/src/components/ui/Button.tsx", "frontend", "components/ui")]
    [InlineData("frontend/src/App.tsx", "frontend", ArchitecturePathFolding.RootName)]
    // Вложенный проект побеждает внешний.
    [InlineData("frontend/modules/notes/src/Note.tsx", "frontend/modules/notes", ArchitecturePathFolding.RootName)]
    // Нет проекта-маркера — каталог верхнего уровня (общая свёртка для любых проектов).
    [InlineData("scripts/tools/gen.ts", "scripts", "tools")]
    [InlineData("setup.py", ArchitecturePathFolding.RootName, ArchitecturePathFolding.RootName)]
    public void Fold_КладётФайлВКонтейнерИКомпонент(string file, string container, string component)
    {
        Folding.Fold(file).Should().Be((container, component));
    }

    [Fact]
    public void Fold_ПутьСОбратнымиСлэшамиИТочкой_НормализуетсяОдинаково()
    {
        Folding.Fold(@".\backend\ClaudeHomeServer\Services\Auth\JwtService.cs")
            .Should().Be(Folding.Fold("backend/ClaudeHomeServer/Services/Auth/JwtService.cs"));
    }

    [Fact]
    public void Fold_ПрефиксБезГраницыКаталога_НеСчитаетсяПроектом()
    {
        // «backend/ClaudeHomeServer» не должен захватывать «backend/ClaudeHomeServerX».
        Folding.ContainerOf("backend/ClaudeHomeServerX/A.cs").Should().Be("backend");
    }

    [Theory]
    [InlineData("backend/ClaudeHomeServer/bin/Debug/X.cs", true)]
    [InlineData("frontend/node_modules/react/index.ts", true)]
    [InlineData(".claude/worktrees/x/A.cs", true)]
    [InlineData("backend/ClaudeHomeServer/data/claude-profiles/p/package.json", true)]
    [InlineData("backend/ClaudeHomeServer/Services/A.cs", false)]
    public void IsIgnored_ОтсекаетСлужебныеКаталоги(string path, bool ignored)
    {
        ArchitecturePathFolding.IsIgnored(path).Should().Be(ignored);
    }

    [Theory]
    [InlineData("backend/ClaudeHomeServer.Tests/A.cs", true)]
    [InlineData("frontend/e2e/login.spec.ts", true)]
    [InlineData("src/__tests__/a.ts", true)]
    [InlineData("backend/ClaudeHomeServer/Services/TestRunner.cs", false)]
    public void IsTestPath_УзнаётТесты(string path, bool isTest)
    {
        ArchitecturePathFolding.IsTestPath(path).Should().Be(isTest);
    }
}
