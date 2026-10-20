using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Кэш манифестов пакета (rework 12, minor 4): N+1 чтений ccs-plugin.json на каждый
// GET /api/plugins убран. Манифест неизменяем по (id, version, sha), поэтому запись кэша
// не «протухает» по содержимому; GetOrLoad не перечитывает диск, Invalidate освобождает
// записи пакета при установке/обновлении/удалении.
public class PluginManifestCacheTests
{
    // Кэш статический и общий между тестами процесса — каждый тест использует УНИКАЛЬНЫЙ
    // (id, version, sha), чтобы не попадать в записи соседних тестов.

    // Мутация: GetOrLoad всегда вызывает loader (убрать TryGetValue) → loads = 2, тест падает.
    [Fact]
    public void GetOrLoad_Кэширует_ИНеПеречитывает()
    {
        int loads = 0;
        var loader = () =>
        {
            loads++;
            return new PluginManifest { Id = "t-cache/pkg", Name = "pkg", Version = "1.0.0" };
        };

        var m1 = PluginManifestCache.GetOrLoad("t-cache/pkg", "1.0.0", "sha-cache", loader);
        var m2 = PluginManifestCache.GetOrLoad("t-cache/pkg", "1.0.0", "sha-cache", loader);

        m1.Should().NotBeNull();
        m1.Should().BeSameAs(m2, "повторный запрос — из кэша, та же запись");
        loads.Should().Be(1, "loader вызван один раз (второй запрос из кэша)");
    }

    // Мутация: Invalidate не удаляет записи → loads = 1 (не 2), тест падает.
    [Fact]
    public void Invalidate_ОсвобождаетЗаписиПакета()
    {
        int loads = 0;
        var loader = () =>
        {
            loads++;
            return new PluginManifest { Id = "t-inv/pkg", Name = "pkg", Version = "1.0.0" };
        };

        PluginManifestCache.GetOrLoad("t-inv/pkg", "1.0.0", "sha-inv", loader);
        PluginManifestCache.Invalidate("t-inv/pkg");
        PluginManifestCache.GetOrLoad("t-inv/pkg", "1.0.0", "sha-inv", loader);

        loads.Should().Be(2, "после Invalidate запись перечитана (кэш освобождён)");
    }

    // Другой id не инвалидируется: Invalidate по одному id не трогает записи другого.
    [Fact]
    public void Invalidate_ДругойId_НеТронут()
    {
        int loadsA = 0, loadsB = 0;
        var loaderA = () => { loadsA++; return new PluginManifest { Id = "t-a/pkg", Version = "1.0.0" }; };
        var loaderB = () => { loadsB++; return new PluginManifest { Id = "t-b/pkg", Version = "1.0.0" }; };

        PluginManifestCache.GetOrLoad("t-a/pkg", "1.0.0", "shaA", loaderA);
        PluginManifestCache.GetOrLoad("t-b/pkg", "1.0.0", "shaB", loaderB);
        PluginManifestCache.Invalidate("t-a/pkg"); // только A

        PluginManifestCache.GetOrLoad("t-a/pkg", "1.0.0", "shaA", loaderA);
        PluginManifestCache.GetOrLoad("t-b/pkg", "1.0.0", "shaB", loaderB);

        loadsA.Should().Be(2, "A перечитан после Invalidate");
        loadsB.Should().Be(1, "B не тронут Invalidate по A");
    }
}
