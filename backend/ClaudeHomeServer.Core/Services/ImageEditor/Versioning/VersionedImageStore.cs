using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.ImageEditor.Versioning;

// Версионное сохранение результата правки картинки (ADR-017, раздел 5): оригинал никогда
// не перезаписывается. Запись идёт строго через FileMode.CreateNew — гонка за имя решается
// переходом на следующий свободный номер, а не молчаливой перезаписью (FileMode.Create
// затёр бы уже сохранённую версию соседнего сеанса правки).
public interface IVersionedImageStore
{
    // relativePath — путь ОРИГИНАЛА (или уже версионированного файла) относительно корня
    // проекта, например "images/hero.png". bytes сохраняются под следующим свободным
    // номером версии рядом с ним; расширение берётся по фактическому формату bytes,
    // а не по имени relativePath. Возвращает путь сохранённого файла относительно корня.
    string SaveNextVersion(string projectRoot, string relativePath, byte[] bytes);
}

public sealed class VersionedImageStore : IVersionedImageStore
{
    private static readonly Regex VersionSuffix = new(@"^(.+)\.v\d+$", RegexOptions.Compiled);

    public string SaveNextVersion(string projectRoot, string relativePath, byte[] bytes)
    {
        var normalized = relativePath.Replace('\\', '/');
        var ext = ImageFormatSniffer.DetectExtension(bytes) ?? Path.GetExtension(normalized);
        var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "";
        var stem = StripVersionSuffix(Path.GetFileNameWithoutExtension(normalized));

        for (var n = 2; ; n++)
        {
            var fileName = $"{stem}.v{n}{ext}";
            var candidateRel = string.IsNullOrEmpty(dir) ? fileName : $"{dir}/{fileName}";
            var absolute = SafePath.Join(projectRoot, candidateRel);

            try
            {
                using var fs = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write);
                fs.Write(bytes, 0, bytes.Length);
                return candidateRel;
            }
            catch (IOException) when (File.Exists(absolute))
            {
                // имя версии занято соседним сохранением — пробуем следующий номер
            }
        }
    }

    private static string StripVersionSuffix(string stem)
    {
        var m = VersionSuffix.Match(stem);
        return m.Success ? m.Groups[1].Value : stem;
    }
}
