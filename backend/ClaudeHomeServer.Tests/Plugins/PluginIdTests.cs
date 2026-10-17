using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// Формат глобального id пакета {publisher}/{slug} (ADR-031 §2.2, M3): валидация в Core,
// единый метод нормализации (PluginId.TryNormalize) — его используют и PluginStore, и
// PluginUninstall. Тесты фиксируют формат, поведение нормализации (URL-форма ~ → /) и
// интеграционный гейт: id с недопустимым символом → установка Fail (400), не 500.
public class PluginIdTests : IDisposable
{
    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginInstaller _installer;

    public PluginIdTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_id_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _installer = new PluginInstaller(_store, new PluginPackageValidator());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    // --- Валидные id ---

    [Theory]
    [InlineData("local-a/pkg")]
    [InlineData("local-abc123/my-plugin")]
    [InlineData("acme/notes")]
    [InlineData("a/b")]
    public void IsValid_КорректныйId_Принят(string id)
    {
        PluginId.IsValid(id).Should().BeTrue(id);
    }

    // --- Невалидные id (M3: 400, не 500) ---

    [Theory]
    [InlineData("../foo")]        // «..» — точки не в [a-z0-9-]
    [InlineData("foo/../bar")]   // «..» как сегмент
    [InlineData("local-a")]      // без '/' (один сегмент)
    [InlineData("local-a/")]     // пустой slug
    [InlineData("/pkg")]         // пустой publisher
    [InlineData("Local-A/pkg")]  // заглавные — не в [a-z0-9-]
    [InlineData("local a/pkg")]   // пробел
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_НекорректныйId_Отклонён(string? id)
    {
        PluginId.IsValid(id).Should().BeFalse(id);
    }

    // --- Нормализация: URL-форма ~ → / ---

    [Fact]
    public void TryNormalize_UrlФорма_ПриводитККанонической()
    {
        PluginId.TryNormalize("local-a~pkg", out var id).Should().BeTrue();
        id.Value.Should().Be("local-a/pkg");
    }

    [Fact]
    public void TryNormalize_КаноническаяФорма_БезИзменений()
    {
        PluginId.TryNormalize("local-a/pkg", out var id).Should().BeTrue();
        id.Value.Should().Be("local-a/pkg");
    }

    [Fact]
    public void TryNormalize_Некорректный_ВозвращаетFalse()
    {
        PluginId.TryNormalize("../foo", out _).Should().BeFalse();
    }

    // --- Мутация (критерий): убираем PluginId.IsValid из PluginInstaller.Install → тест падает.
    //     id «local.a/pkg» (точка в publisher) — не по формату; без проверки установка прошла бы
    //     (валидатор пакета и sha256 не видят формат id) → статус Ok, а не Fail. ---

    [Fact]
    public void Install_id_СНедопустимымСимволом()
    {
        // Черновик с манифестом, у которого id «local.a/pkg» (точка — недопустимый символ).
        var slug = "pkg";
        var root = _store.DraftDir("owner-a", slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "local.a/pkg",   // точка в publisher — не [a-z0-9-]
                name = "Pkg",
                version = "1.0.0",
                contributes = new { skills = new[] { "skills/a" } },
                permissions = new { requested = new[] { "notes:read" } },
            }, new JsonSerializerOptions { WriteIndented = true }));

        var result = _installer.Install("owner-a", "draft:" + slug);

        result.Status.Should().Be(InstallStatus.Fail,
            "id с недопустимым символом → 400 (Fail), не 500 (M3)");
        result.Error.Should().Contain("id");
    }
}
