using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Чтение model-fallback.json: стор пишет PascalCase (Save без PropertyNamingPolicy), но файл
// правят и руками в camelCase. Чтение должно быть устойчиво к регистру — иначе camelCase
// молчаливо игнорируется и настройка не применяется (ловушка приёмки 19d8f18e).
public class FallbackSettingsStoreTests
{
    private static IConfiguration BuildConfig(string tempDir) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tempDir, "projects.json"),
        }).Build();

    private static string FilePath(string tempDir) =>
        Path.Combine(tempDir, "model-fallback.json");

    [Fact]
    public void Load_CamelCaseФайл_Применяется()
    {
        // Ручная правка в camelCase (конвенция): global.maxSubstitutions = 3
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(FilePath(tempDir), """{"version":1,"global":{"maxSubstitutions":3}}""");

        var store = new FallbackSettingsStore(BuildConfig(tempDir));

        // До фикса (case-sensitive): camelCase не матчит PascalCase → null → дефолт 4.
        // После фикса: 3, как написано в файле.
        Assert.Equal(3, store.ResolveMaxSubstitutions("any"));
    }

    [Fact]
    public void Load_PascalCaseФайл_Применяется()
    {
        // Формат, которым пишет сам стор (Save без PropertyNamingPolicy) — PascalCase
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(FilePath(tempDir), """{"Version":1,"Global":{"MaxSubstitutions":2}}""");

        var store = new FallbackSettingsStore(BuildConfig(tempDir));

        Assert.Equal(2, store.ResolveMaxSubstitutions("any"));
    }

    [Fact]
    public void Load_OwnerCamelCase_Применяется()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(FilePath(tempDir),
            """{"version":1,"global":{"maxSubstitutions":2},"owners":{"u1":{"maxSubstitutions":5}}}""");

        var store = new FallbackSettingsStore(BuildConfig(tempDir));

        Assert.Equal(5, store.ResolveMaxSubstitutions("u1"));
        Assert.Equal(2, store.ResolveMaxSubstitutions("other")); // global
    }

    [Fact]
    public void Roundtrip_ЗаписьИПеречитывание_ТотЖеФормат()
    {
        // Стор пишет файл сам — перечитывание новым экземпляром должно дать то же значение.
        // Заодно фиксирует эталон: запись и чтение симметричны.
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = new FallbackSettingsStore(BuildConfig(tempDir));
        Assert.Null(store.SetGlobal(3));

        var fileText = File.ReadAllText(FilePath(tempDir));
        // Стор пишет PascalCase (compact, без отступов) — это эталон формата для ручной правки
        Assert.Contains("\"Version\"", fileText);
        Assert.Contains("\"Global\"", fileText);
        Assert.Contains("\"MaxSubstitutions\":3", fileText);

        // Новый экземпляр читает тот же файл
        var reloaded = new FallbackSettingsStore(BuildConfig(tempDir));
        Assert.Equal(3, reloaded.ResolveMaxSubstitutions("any"));
    }
}
