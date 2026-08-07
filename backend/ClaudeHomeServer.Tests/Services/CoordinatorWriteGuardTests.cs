using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Гард «координатор не пишет код сам» (Э7-фикс, находка Веры Major №1): раньше Write/Edit
// блокировались, а координатор создавал файлы через Bash (cat > file << EOF) в обход.
// Важен и позитив (ловим подтверждённый вживую обход), и негатив (сборка/тесты не гасятся).
public class CoordinatorWriteGuardTests
{
    [Theory]
    [InlineData("Bash")]
    [InlineData("bash")]
    [InlineData("PowerShell")]
    [InlineData("powershell")]
    public void IsShellTool_ИзвестныеИмена_True(string toolName)
    {
        CoordinatorWriteGuard.IsShellTool(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("Grep")]
    [InlineData("Task")]
    public void IsShellTool_ДругиеИнструменты_False(string toolName)
    {
        CoordinatorWriteGuard.IsShellTool(toolName).Should().BeFalse();
    }

    [Theory]
    [InlineData("cat > \"backend/Counter.cs\" << 'EOF'\nnamespace Demo;\nEOF\n")]  // ровно находка Веры
    [InlineData("cat > file.txt << EOF\nконтент\nEOF")]
    [InlineData("echo 'using System;' > Program.cs")]
    [InlineData("printf 'text' >> notes.md")]
    [InlineData("echo done | tee output.log")]
    [InlineData("sed -i 's/old/new/' Program.cs")]
    [InlineData("Set-Content -Path a.txt -Value hi")]
    [InlineData("Add-Content -Path a.txt -Value hi")]
    [InlineData("Get-Content x | Out-File y")]
    [InlineData("New-Item -ItemType File -Path a.txt")]
    [InlineData("Copy-Item a.txt b.txt")]
    // Волна 3: способы обхода, найденные адверсарным аудитом Глеба, но не покрытые регэкспом
    [InlineData("sed 's/old/new/' f > f2 && mv f2 f")]
    [InlineData("awk '{print $1}' input.txt > output.txt")]
    [InlineData("mv src.cs dst.cs")]
    [InlineData("cp src.cs dst.cs")]
    [InlineData("'using System;' > Program.cs")]
    public void LooksLikeFileWrite_ИзвестныеСпособыЗаписи_True(string command)
    {
        CoordinatorWriteGuard.LooksLikeFileWrite(command).Should().BeTrue();
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet test --filter FullyQualifiedName~SessionManagerTests")]
    [InlineData("git status")]
    [InlineData("git diff")]
    [InlineData("ls -la")]
    [InlineData("npm run build")]
    [InlineData("dotnet test > build.log 2>&1")]           // редирект лога сборки — легитимно
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeFileWrite_ПроверочныеКоманды_False(string? command)
    {
        CoordinatorWriteGuard.LooksLikeFileWrite(command).Should().BeFalse();
    }
}
