using System.Text;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Обыск «устройства» на серверные секреты (сторож G3, C#-порт идеи
/// tools/local-projects-spike/check-device.mjs): точные значения секретов ищутся во всех
/// файлах каталога устройства и в дополнительных текстах (env/argv процессов, принятые
/// агентом кадры). В находках — только имя секрета и место, значение не печатается.
/// </summary>
internal static class DeviceSecretScanner
{
    public sealed record Hit(string Where, string Secret)
    {
        public override string ToString() => $"{Secret} в {Where}";
    }

    public sealed record Report(int ScannedFiles, long ScannedBytes, IReadOnlyList<Hit> Hits);

    public static Report Scan(
        IReadOnlyDictionary<string, string> secrets,
        string deviceDir,
        IEnumerable<(string Where, string Text)>? extra = null)
    {
        var hits = new List<Hit>();
        var files = 0;
        long bytes = 0;

        foreach (var file in Directory.EnumerateFiles(deviceDir, "*", SearchOption.AllDirectories))
        {
            // latin1: побайтовое чтение, секрет найдётся в любом месте, включая бинарные файлы
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
            files++;
            bytes += text.Length;
            Match(Path.GetRelativePath(deviceDir, file), text);
        }
        foreach (var (where, text) in extra ?? [])
            Match(where, text);

        return new Report(files, bytes, hits);

        void Match(string where, string text)
        {
            foreach (var (name, value) in secrets)
                if (!string.IsNullOrEmpty(value) && text.Contains(value, StringComparison.Ordinal))
                    hits.Add(new Hit(where, name));
        }
    }
}
