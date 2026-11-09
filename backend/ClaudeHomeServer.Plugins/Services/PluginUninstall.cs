using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Plugins;

// Каскадное удаление установки плагина (ADR-031 §2.3, §4, шаг 1.4): один вызов
// `Uninstall` убирает всё, что установка занимает в хранилище владельца,
// + «внешние» ссылки через шов IPluginInstallCascade:
//   1) пакет      packages/{id}/{version}/   (неизменяемый каталог);
//   2) черновик    _drafts/{slug}/           (build-it-yourself, если создавался);
//   3) внешние     привязки персон plugin:{id}, размещения панелей в слотах (шов);
//   4) запись      installed.json             (с ней уходят выданные права granted);
//   5) секреты     plugin-secrets.json        (M7: значения, не в installed.json).
//
// Порядок (M3/minor): нормализация id (PluginId) → файлы на диске (пакет, черновик)
// → шов (внешние ссылки) → запись installed.json → секреты. Запись — «коммит»: её
// удаляем ПОСЛЕ шова, чтобы при исключении в каскаде запись ещё была (идемпотентный
// повтор) и не было «сироты» (запись удалена, а пакет/черновик/внешние ссылки висят).
// Все удаления каталогов идемпотентны (Directory.Exists-страж), повторный Uninstall
// по той же id — no-op.
public class PluginUninstall
{
    private readonly PluginStore _store;
    private readonly PluginSecretsStore _secrets;
    private readonly IPluginInstallCascade _cascade;
    // Прокси net:{host} (rework 12, minor 8): при удачном каскаде чистит его окно
    // ограничения частоты, иначе словарь singleton'а рос бы на каждый удалённый плагин.
    // Опциональная: без прокси (например, в старых тестах) очистка просто не происходит.
    private readonly PluginNetProxy? _netProxy;
    // Итерация 2: рассыльщик для единого plugins_changed после каскада. DI авто-инъектит
    // (Main); в тестах/выключенной подсистеме — null.
    private readonly ISessionBroadcaster? _broadcaster;

    public PluginUninstall(PluginStore store, PluginSecretsStore secrets, IPluginInstallCascade cascade,
        PluginNetProxy? netProxy = null, ISessionBroadcaster? broadcaster = null)
    {
        _store = store;
        _secrets = secrets;
        _cascade = cascade;
        _netProxy = netProxy;
        _broadcaster = broadcaster;
    }

    // Удалить установку плагина <paramref name="id"/> владельца <paramref name="ownerId"/>.
    // id — URL-форма (publisher~slug) или каноническая (publisher/slug): нормализуем
    // единым методом PluginId (M3, как в PluginStore). Возвращает false, если записи с
    // таким id у владельца не было (ничего не тронуто) или id не по формату.
    public bool Uninstall(string ownerId, string id)
    {
        // M3: нормализация id (единый метод PluginId): ~ → /, проверка формата.
        if (!PluginId.TryNormalize(id, out var normId)) return false;
        id = normId.Value;

        var records = _store.LoadInstalled(ownerId).ToList();
        var record = records.FirstOrDefault(r => r.Id == id);
        if (record is null) return false;

        // 1) Пакет: packages/{id}/{version}/
        var packageDir = _store.PackageDir(ownerId, id, record.Version);
        if (Directory.Exists(packageDir))
            Directory.Delete(packageDir, recursive: true);

        // 2) Черновик: _drafts/{slug}/ — slug общий для всех publisher'ов, поэтому
        //    удаляем ТОЛЬКО вместе с проверкой publisher (minor): сверяем ВЫВЕДЕННЫЙ id
        //    черновика (DeriveId(ownerId, slug), QA-фикс O минор 3: не сырой manifest.Id —
        //    для манифестов без id он null и черновик не удалялся) с uninstall-имым id;
        //    не совпало (чужой publisher) — черновик не трогаем.
        var draftDir = _store.DraftDir(ownerId, SlugOf(id));
        if (Directory.Exists(draftDir) && PluginIdentity.DeriveId(ownerId, SlugOf(id)) == id)
            Directory.Delete(draftDir, recursive: true);

        // 3) Внешние ссылки (привязки персон, слоты) — шов. До записи: если шов бросит,
        //    запись ещё на месте (идемпотентный повтор), «сироты» нет.
        _cascade.OnUninstalled(ownerId, id);

        // 4) Запись installed.json — «коммит»: с ней уходят выданные права (granted).
        //    Итерация 2: fireEvent: false — каскад схлопывает в одно событие (шаг 8).
        records.Remove(record);
        _store.SaveInstalled(ownerId, records, fireEvent: false);

        // 5) Секреты (M7): значения — из per-owner архива plugin-secrets.json.
        //    Итерация 2: fireEvent: false — см. шаг 4.
        _secrets.RemovePlugin(ownerId, id, fireEvent: false);

        // 6) Окно ограничения частоты (rework 12, minor 8): после удачного каскада —
        //     словарь singleton'а прокси не растёт бесконечно (ключ на каждый плагин).
        _netProxy?.RemoveRateWindow(ownerId, id);

        // 7) Кэш манифестов (rework 12, minor 4): плагин удалён — освободить его записи (по (owner, id)).
        PluginManifestCache.Invalidate(ownerId, id);

        // 8) Итерация 2: единое plugins_changed после всего каскада (вместо двух:
        //    от SaveInstalled и от RemovePlugin). Клиент делает один GET, не два.
        if (_broadcaster is { } b)
            _ = b.ToOwner(ownerId, new PluginsChangedMessage());

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
