using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Каскадное удаление установки плагина (ADR-031 §2.3, §4, шаг 1.4): один вызов
// `Uninstall` убирает всё, что установка занимает в хранилище владельца,
// + «внешние» ссылки через шов IPluginInstallCascade:
//   1) пакет      packages/{id}/{version}/   (неизменяемый каталог);
//   2) черновик    _drafts/{slug}/           (build-it-yourself, если создавался);
//   3) запись      installed.json             (с ней уходят выданные права granted и секреты);
//   4) внешние     привязки персон plugin:{id}, размещения панелей в слотах (шов).
//
// Порядок: сначала файлы на диске (пакет, черновик), потом запись, потом шов — чтобы
// при сбое посередине запись ещё была (идемпотентный повтор) и не было «сироты»
// (запись удалена, а пакет/черновик висит). Все удаления каталогов идемпотентны
// (Directory.Exists-страж), повторный Uninstall по той же id — no-op.
public class PluginUninstall
{
    private readonly PluginStore _store;
    private readonly IPluginInstallCascade _cascade;

    public PluginUninstall(PluginStore store, IPluginInstallCascade cascade)
    {
        _store = store;
        _cascade = cascade;
    }

    // Удалить установку плагина <paramref name="id"/> владельца <paramref name="ownerId"/>.
    // Возвращает false, если записи с таким id у владельца не было (ничего не тронуто).
    public bool Uninstall(string ownerId, string id)
    {
        var records = _store.LoadInstalled(ownerId).ToList();
        var record = records.FirstOrDefault(r => r.Id == id);
        if (record is null) return false;

        // 1) Пакет: packages/{id}/{version}/
        var packageDir = _store.PackageDir(ownerId, id, record.Version);
        if (Directory.Exists(packageDir))
            Directory.Delete(packageDir, recursive: true);

        // 2) Черновик: _drafts/{slug}/ (slug — хвост id после '/'; id без '/' — сам slug)
        var draftDir = _store.DraftDir(ownerId, SlugOf(id));
        if (Directory.Exists(draftDir))
            Directory.Delete(draftDir, recursive: true);

        // 3) Запись installed.json — с ней уходят выданные права (granted) и секреты.
        records.Remove(record);
        _store.SaveInstalled(ownerId, records);

        // 4) Внешние ссылки (привязки персон, слоты) — шов, порядок после записи.
        _cascade.OnUninstalled(ownerId, id);

        return true;
    }

    // Slug пакета — хвост глобального id {publisher}/{slug} после последнего '/'.
    // Id без '/' (короткий) — slug целиком.
    private static string SlugOf(string id)
    {
        var i = id.LastIndexOf('/');
        return i < 0 ? id : id[(i + 1)..];
    }
}
