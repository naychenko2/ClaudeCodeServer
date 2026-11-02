namespace ClaudeHomeServer.Services.Notes.Wiki;

// Основы страниц для «Принять как мою правку» (ADR-038, §6; шаг B5). Манифест хранит только хэш
// последней применённой версии, а дифф «манифест → диск» требует её ТЕКСТ. Поэтому каждая запись
// Хранителя (применение пакета, WritePage, relink, «Принять как есть») кладёт байты записанной
// версии в keeper/baselines/{sha256}: имя файла — хэш, значит чтение по записи манифеста точное,
// а одинаковые версии не дублируются. Для версий, записанных до появления основ, запасной путь —
// прообраз следующего пакета (он хранит ровно эту версию); нет ни того ни другого — основы нет, и
// «как мою правку» отказывает честно (остаётся «Принять как есть»).
//
// Срок хранения — как у прообразов (PurgeOldPreimages): основа нужна, пока возможна правка мимо
// веб-морды поверх этой версии; повторная запись той же версии продлевает срок.
internal static class WikiBaselines
{
    private const string Dir = "keeper/baselines";

    /// <summary>Кладёт байты версии страницы; возвращает их sha256. Повторная запись той же
    /// версии только обновляет время файла. Сбой записи основы не должен ронять запись страницы
    /// (основа — вспомогательная): исключение глотается, основы просто не будет.</summary>
    internal static string Save(string root, byte[] bytes)
    {
        var hash = WikiApplyService.Sha256OfBytes(bytes);
        try
        {
            var full = SafePath.Join(root, $"{Dir}/{hash}");
            if (File.Exists(full))
                File.SetLastWriteTimeUtc(full, DateTime.UtcNow);
            else
                WikiApplyService.WriteBytesAtomic(root, $"{Dir}/{hash}", bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Основа — удобство «как мою правку», а не часть транзакции записи.
        }
        return hash;
    }

    /// <summary>Байты версии страницы с этим хэшем: из основ, иначе из прообраза любого пакета,
    /// где страница лежала именно в этой версии. null — версии нигде нет.</summary>
    internal static byte[]? TryLoad(string root, string pageKey, string hash)
    {
        try
        {
            var full = SafePath.Join(root, $"{Dir}/{hash}");
            if (File.Exists(full) && File.ReadAllBytes(full) is { } own && WikiApplyService.Sha256OfBytes(own) == hash)
                return own;

            var preimages = SafePath.Join(root, "keeper/preimages");
            if (!Directory.Exists(preimages))
                return null;
            foreach (var pkgDir in Directory.EnumerateDirectories(preimages))
            {
                var candidate = Path.Combine(pkgDir, pageKey.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate) && File.ReadAllBytes(candidate) is { Length: > 0 } bytes
                    && WikiApplyService.Sha256OfBytes(bytes) == hash)
                    return bytes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Нечитаемое — как отсутствующее: вызывающий скажет «основы нет».
        }
        return null;
    }

    /// <summary>Чистка основ старше срока хранения (вызывает PurgeOldPreimages); возвращает число
    /// удалённых файлов. Сбой на одном файле остальные не останавливает.</summary>
    internal static int Purge(string root, DateTimeOffset cutoff)
    {
        var dir = SafePath.Join(root, Dir);
        if (!Directory.Exists(dir))
            return 0;
        var deleted = 0;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (new DateTimeOffset(File.GetLastWriteTimeUtc(f)) >= cutoff)
                    continue;
                File.Delete(f);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return deleted;
    }
}
