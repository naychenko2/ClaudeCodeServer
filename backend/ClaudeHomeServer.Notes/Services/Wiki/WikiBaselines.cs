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
    private const string MarkerDir = "keeper/baselines-backfilled";

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

    /// <summary>Разовое донесение основ для страниц, записанных до появления основ: запись манифеста,
    /// чей хэш совпадает с байтами файла, получает основу (файл не дрейфует — это именно её версия).
    /// Дрейфующие файлы не трогаются: их версия манифеста в файле уже не лежит. Делается один раз
    /// на раздел (маркер вне каталога основ — чистка по возрасту его не снесёт), под замком раздела.
    /// Возвращает число сохранённых основ; сбой ввода-вывода маркер не ставит — повтор при следующем входе.</summary>
    internal static int BackfillOnce(string root, string section)
    {
        try
        {
            var marker = SafePath.Join(root, $"{MarkerDir}/{WikiApplyService.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(section))}");
            if (File.Exists(marker))
                return 0;
            var mres = WikiManifest.TryRead(root, section);
            if (mres.State == WikiManifestState.Invalid)
                return 0;
            var saved = 0;
            foreach (var (page, hash) in mres.Manifest)
            {
                if (File.Exists(SafePath.Join(root, $"{Dir}/{hash}")))
                    continue;
                var file = SafePath.JoinBelowRoot(root, "pages/" + page);
                if (!File.Exists(file))
                    continue;
                var bytes = File.ReadAllBytes(file);
                if (WikiApplyService.Sha256OfBytes(bytes) != hash)
                    continue;
                Save(root, bytes);
                saved++;
            }
            WikiApplyService.WriteBytesAtomic(root, $"{MarkerDir}/{WikiApplyService.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(section))}", []);
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return 0;
        }
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
