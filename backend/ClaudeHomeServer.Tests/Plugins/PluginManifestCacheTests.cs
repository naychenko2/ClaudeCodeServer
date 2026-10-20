using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Кэш манифестов пакета (rework 12, minor 4): N+1 чтений ccs-plugin.json на каждый
// GET /api/plugins убран. Манифест неизменяем по (owner, id, version, sha), поэтому запись
// кэша не «протухает» по содержимому; GetOrLoad не перечитывает диск, Invalidate освобождает
// записи пакета при установке/обновлении/удалении.
//
// QA-фикс K (minor 4): ownerId в ключе + пустой sha не кэшируется — два владельца с одним
// id+version (и пустым sha) не получают чужой манифест.
public class PluginManifestCacheTests
{
    // Кэш статический и общий между тестами процесса — каждый тест использует УНИКАЛЬНЫЙ
    // (owner, id, version, sha), чтобы не попадать в записи соседних тестов.

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

        var m1 = PluginManifestCache.GetOrLoad("owner-cache", "t-cache/pkg", "1.0.0", "sha-cache", loader);
        var m2 = PluginManifestCache.GetOrLoad("owner-cache", "t-cache/pkg", "1.0.0", "sha-cache", loader);

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

        PluginManifestCache.GetOrLoad("owner-inv", "t-inv/pkg", "1.0.0", "sha-inv", loader);
        PluginManifestCache.Invalidate("owner-inv", "t-inv/pkg");
        PluginManifestCache.GetOrLoad("owner-inv", "t-inv/pkg", "1.0.0", "sha-inv", loader);

        loads.Should().Be(2, "после Invalidate запись перечитана (кэш освобождён)");
    }

    // Другой id не инвалидируется: Invalidate по (owner, id) не трогает записи другого id.
    [Fact]
    public void Invalidate_ДругойId_НеТронут()
    {
        int loadsA = 0, loadsB = 0;
        var loaderA = () => { loadsA++; return new PluginManifest { Id = "t-a/pkg", Version = "1.0.0" }; };
        var loaderB = () => { loadsB++; return new PluginManifest { Id = "t-b/pkg", Version = "1.0.0" }; };

        PluginManifestCache.GetOrLoad("owner-x", "t-a/pkg", "1.0.0", "shaA", loaderA);
        PluginManifestCache.GetOrLoad("owner-x", "t-b/pkg", "1.0.0", "shaB", loaderB);
        PluginManifestCache.Invalidate("owner-x", "t-a/pkg"); // только A

        PluginManifestCache.GetOrLoad("owner-x", "t-a/pkg", "1.0.0", "shaA", loaderA);
        PluginManifestCache.GetOrLoad("owner-x", "t-b/pkg", "1.0.0", "shaB", loaderB);

        loadsA.Should().Be(2, "A перечитан после Invalidate");
        loadsB.Should().Be(1, "B не тронут Invalidate по A");
    }

    // QA-фикс K (minor 4): пустой sha (PluginInstall.Sha256 по умолчанию "") не кэшируется —
    // loader вызывается каждый раз. Мутация: кэшировать и при пустом sha (убрать guard) →
    // loader вызван 1 раз (второй запрос из кэша), r2 = stale → тест красный.
    [Fact]
    public void GetOrLoad_ПустойSha_НеКэшируется()
    {
        var calls = 0;
        var loader = () =>
        {
            calls++;
            return new PluginManifest { Id = "t-noc/pkg", Version = "1.0.0", Name = "call" + calls };
        };

        var r1 = PluginManifestCache.GetOrLoad("owner-noc", "t-noc/pkg", "1.0.0", "", loader);
        var r2 = PluginManifestCache.GetOrLoad("owner-noc", "t-noc/pkg", "1.0.0", "", loader);

        calls.Should().Be(2, "пустой sha не кэшируется: loader вызван дважды");
        r1!.Name.Should().Be("call1");
        r2.Name.Should().Be("call2", "второй вызов — свежий результат, не stale-кэш");
    }

    // QA-фикс K (minor 4): ownerId в ключе — два владельца с одним id+version+sha (одинаковое
    // содержимое) не делят запись кэша. Мутация: убрать ownerId из ключа → B получает запись A
    // из кэша (loadsB = 0) → тест красный.
    [Fact]
    public void GetOrLoad_ДваВладельца_ОдинаковыйКлюч_НеПересекаются()
    {
        int loadsA = 0, loadsB = 0;
        var loaderA = () => { loadsA++; return new PluginManifest { Id = "t-2o/pkg", Version = "1.0.0", Name = "A" }; };
        var loaderB = () => { loadsB++; return new PluginManifest { Id = "t-2o/pkg", Version = "1.0.0", Name = "B" }; };

        PluginManifestCache.GetOrLoad("owner-a", "t-2o/pkg", "1.0.0", "sha-same", loaderA);
        PluginManifestCache.GetOrLoad("owner-b", "t-2o/pkg", "1.0.0", "sha-same", loaderB);

        loadsA.Should().Be(1, "A загрузил свой манифест");
        loadsB.Should().Be(1, "B загрузил СВОЙ манифест (не взял запись A из кэша: ownerId в ключе)");
    }
}
