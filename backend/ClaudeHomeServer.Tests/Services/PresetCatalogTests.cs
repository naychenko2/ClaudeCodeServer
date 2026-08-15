using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Docs;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Инварианты каталога пресетов каркаса (знакомство v2, п.2): состав — данные в коде,
// и любой его дефект молча уедет в чужие репозитории при применении. Здесь — сторожа,
// которые ловят такой дефект до релиза.
public class PresetCatalogTests
{
    [Fact]
    public void Ключи_УникальныИПокрываютТриПресета()
    {
        PresetCatalog.All.Should().HaveCount(3);
        PresetCatalog.All.Select(p => p.Key).Should().OnlyHaveUniqueItems();
        // Ключи из согласованной заметки о составе пресетов
        PresetCatalog.All.Select(p => p.Key).Should().BeEquivalentTo(["docs", "dev", "personal"]);
        PresetCatalog.Find("docs").Should().NotBeNull();
        PresetCatalog.Find("dev").Should().NotBeNull();
        PresetCatalog.Find("personal").Should().NotBeNull();
        PresetCatalog.Find("unknown").Should().BeNull();
        PresetCatalog.Find(null!).Should().BeNull();
    }

    [Fact]
    public void Заготовки_РазличаютсяБайтВБайт()
    {
        // Одинаковые файлы в разных проектах получают одинаковый хеш — и детект переноса
        // ProjectKnowledgeSyncService.FindMoveTarget может перевесить документ Dify на
        // пустышку. Все заготовки каталога обязаны быть попарно различны как UTF-8 байты.
        var files = PresetCatalog.All.SelectMany(p => p.Files.Select(f => (p.Key, f.Path, f.Content)));
        var duplicates = files
            .GroupBy(f => Encoding.UTF8.GetBytes(f.Content), ByteArrayComparer.Instance)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(", ", g.Select(f => $"{f.Key}/{f.Path}")))
            .ToList();
        duplicates.Should().BeEmpty("заготовки с одинаковыми байтами ломают детект переноса Dify");
    }

    [Fact]
    public void КаждыйПресет_КладётСтатусMdВФайлыИВОбласть()
    {
        // Без Статус.md в rootFiles области DocsHome не резолвится (DocsIndexService:
        // Home ищется в корпусе, а корневые файлы попадают туда только поимённо)
        foreach (var preset in PresetCatalog.All)
        {
            preset.Files.Should().Contain(f => f.Path == "Статус.md",
                $"{preset.Key}: заготовка Статус.md обязана создаваться");
            preset.DocsScope.RootFiles.Should().Contain("Статус.md",
                $"{preset.Key}: Статус.md обязан быть в файлах корня области");
            preset.DocsScope.Home.Should().Be("Статус.md",
                $"{preset.Key}: «Начало» панели документации — Статус.md");
        }
    }

    [Fact]
    public void Область_ПереживаетНормализациюЗаписиБезПотерь()
    {
        // WriteScopeFile нормализует оси и схему; если нормализация режет состав пресета,
        // в .docs репозитория попадёт не то, что заявлено в каталоге
        foreach (var preset in PresetCatalog.All)
        {
            DocsIndexService.NormalizeFolders(preset.DocsScope.Folders)
                .Should().BeEquivalentTo(preset.DocsScope.Folders, $"{preset.Key}: папки области");
            DocsIndexService.NormalizeRootFiles(preset.DocsScope.RootFiles)
                .Should().BeEquivalentTo(preset.DocsScope.RootFiles, $"{preset.Key}: файлы корня");
            DocsIndexService.NormalizeTypes(preset.DocsScope.Types)
                .Should().BeEquivalentTo(preset.DocsScope.Types, $"{preset.Key}: типы области");
            DocsIndexService.NormalizeHome(preset.DocsScope.Home)
                .Should().Be(preset.DocsScope.Home, $"{preset.Key}: «Начало»");

            DocTypeSchema.Normalize(preset.DocTypes).Should().BeEquivalentTo(preset.DocTypes,
                $"{preset.Key}: схема типов обязана записываться без потерь");
        }
    }

    [Fact]
    public void ТипыОбласти_ИзвестныКаталогуГрупп()
    {
        var known = DocsIndexService.TypeGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var preset in PresetCatalog.All)
            preset.DocsScope.Types.Should().OnlyContain(t => known.Contains(t),
                $"{preset.Key}: неизвестная группе типов не попадёт в область при записи");
    }

    [Fact]
    public void КолонкиДоски_ИменаНепустыеИУникальны()
    {
        foreach (var preset in PresetCatalog.All)
        {
            preset.BoardColumns.Should().NotBeEmpty($"{preset.Key}: пресет задаёт доску");
            preset.BoardColumns.Select(c => c.Name).Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n));
            preset.BoardColumns.Select(c => c.Name).Should().OnlyHaveUniqueItems();
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) =>
            x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            // Достаточная для группировки свёртка: заготовки короткие, коллизии безвредны
            var hash = 17;
            foreach (var b in obj) hash = hash * 31 + b;
            return hash;
        }
    }
}
