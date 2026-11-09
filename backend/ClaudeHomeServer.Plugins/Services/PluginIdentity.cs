using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Вывод и проверка <c>id</c>/<c>version</c> пакета плагина (ADR-031 §2.2, QA-фикс L4,
/// QA-фикс N: id выводит ТОЛЬКО сервер). ЕДИНАЯ точка: её используют и валидатор
/// (<c>PluginPackageValidator.ValidateFull</c> — проверка, которую видит человек), и
/// установщик (<c>PluginInstaller.Install</c> — «Принять»). Инвариант: если
/// <c>ValidateFull</c> не нашёл проблем, <c>accept</c> не может упасть по причинам
/// id/version/имён/путей — оба берут id/version из этого места.
///
/// Правило (агент НЕ выбирает ни издателя, ни id):
///   * <c>version</c> обязателен: пуст → <c>missing-version</c>, не-semver → <c>bad-version</c>;
///   * <c>id</c> не задан → выводится сервером: <c>local-{ownerShort}/{slug}</c>
///     (издатель — от владельца, slug — из черновика; агент его не выбирает);
///   * <c>id</c> задан → обязан СОПADAТЬСЯ с выведенным сервером
///     <c>DeriveId(ownerId, slug)</c>, иначе <c>bad-id</c>. Агент выбирает только slug;
///     издатель (local-{ownerShort}) и, следом, id — вывод сервера. Это закрывает
///     подмену: агент не может записать id уже установленного плагина (или чужого
///     издателя) и при «Принять» унаследовать его granted-права без диалога согласия.
/// </summary>
public static class PluginIdentity
{
    /// <summary>
    /// Короткий детерминированный хеш владельца: <c>[a-z0-9-]+</c>, 12 символов.
    /// SHA-256(ownerId) → hex → первые 12 символов. Детерминирован: один владелец → один
    /// издатель; не зависит от порядка/времени. Не-алphanumeric в ownerId (UUID-дефисы)
    /// не влияют: хеш берётся от всей строки. 12 hex = 48 бит: коллизия двух владельцев
    /// ~1:2^48 (8 hex = 32 бита давал 50% при ~65 тыс. владельцев, QA-фикс N).
    /// </summary>
    public static string OwnerShort(string ownerId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ownerId));
        var hex = Convert.ToHexString(hash); // 64 hex-символа
        // Первые 12 hex-символов: [0-9a-f]+ ⊂ [a-z0-9-]+ — всегда валидный издатель.
        return hex[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Вывести id пакета: <c>local-{ownerShort}/{slug}</c>. Агент id не выбирает —
    /// издатель (local-{ownerShort}) определяется владельцем, slug — черновиком.
    /// </summary>
    public static string DeriveId(string ownerId, string slug) =>
        $"local-{OwnerShort(ownerId)}/{slug}";

    /// <summary>
    /// Разрешить и проверить id/version манифеста. Возвращает (выведенный/принятый id, проблемы).
    /// <paramref name="manifest"/> — <c>null</c> (нет ccs-plugin.json и нет нативного) → id выводится,
    /// version → missing-version. <paramref name="ownerId"/>/
    /// <paramref name="slug"/> — для вывода id (единственный агентский вход — slug).
    /// </summary>
    public static (string? Id, List<PluginPackageProblem> Problems) Resolve(
        PluginManifest? manifest, string ownerId, string slug)
    {
        var problems = new List<PluginPackageProblem>();

        // version: обязателен (нет → missing-version; не-semver → bad-version).
        var version = manifest?.Version;
        if (string.IsNullOrWhiteSpace(version))
            problems.Add(new PluginPackageProblem("missing-version",
                "В манифесте нет version (semver MAJOR.MINOR.PATCH) — установка невозможна"));
        else if (!PluginVersion.IsValid(version))
            problems.Add(new PluginPackageProblem("bad-version",
                $"version «{version}» не по формату semver (MAJOR.MINOR.PATCH)"));

        // id: выводит ТОЛЬКО сервер. Не задан → выводим; задан → обязан совпасть с
        // выведенным (иначе bad-id: агент подменил id/издателя). Агент выбирает только slug.
        string? id;
        var rawId = manifest?.Id;
        var derived = DeriveId(ownerId, slug);
        if (string.IsNullOrWhiteSpace(rawId))
        {
            id = derived; // выведен: валиден по построению
        }
        else if (rawId == derived)
        {
            id = rawId; // совпал с выведенным — тот же id, что вывел бы сервер
        }
        else
        {
            id = null;
            problems.Add(new PluginPackageProblem("bad-id",
                $"id «{rawId}» не совпадает с выведенным сервером «{derived}» (издатель и id выводит сервер; агент выбирает только slug)"));
        }

        return (id, problems);
    }
}
