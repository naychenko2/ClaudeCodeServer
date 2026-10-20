using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Вывод и проверка <c>id</c>/<c>version</c> пакета плагина (ADR-031 §2.2, QA-фикс L4).
/// ЕДИНАЯ точка: её используют и валидатор (<c>PluginPackageValidator.ValidateFull</c> —
/// проверка, которую видит человек), и установщик (<c>PluginInstaller.Install</c> — «Принять»).
/// Инвариант: если <c>ValidateFull</c> не нашёл проблем, <c>accept</c> не может упасть по
/// причинам id/version/имён/путей — оба берут id/version из этого места.
///
/// Правило (агент НЕ выбирает издателя):
///   * <c>version</c> обязателен: пуст → <c>missing-version</c>, не-semver → <c>bad-version</c>;
///   * <c>id</c> не задан → выводится сервером: <c>local-{ownerShort}/{slug}</c>
///     (издатель — от владельца, slug — из черновика; агент его не выбирает);
///   * <c>id</c> задан → обязан быть валидным по <see cref="PluginId"/> (формат
///     <c>{publisher}/{slug}</c>); чужой издатель не запрещён дополнительно — формат
///     <c>[a-z0-9-]+/[a-z0-9-]+</c> и так не пускает «..»/юникод/длину.
/// </summary>
public static class PluginIdentity
{
    /// <summary>
    /// Короткий детерминированный хеш владельца: <c>[a-z0-9-]+</c>, 8 символов.
    /// SHA-256(ownerId) → hex → первые 8 символов. Детерминирован: один владелец → один
    /// издатель; не зависит от порядка/времени. Не-алphanumeric в ownerId (UUID-дефисы)
    /// не влияют: хеш берётся от всей строки.
    /// </summary>
    public static string OwnerShort(string ownerId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ownerId));
        var hex = Convert.ToHexString(hash); // 64 hex-символа
        // Первые 8 hex-символов: [0-9a-f]+ ⊂ [a-z0-9-]+ — всегда валидный издатель.
        return hex[..8].ToLowerInvariant();
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
    /// <paramref name="slug"/> — для вывода id, когда он не задан.
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

        // id: не задан → выводим (local-{ownerShort}/{slug}); задан → валиден по PluginId.
        string? id;
        var rawId = manifest?.Id;
        if (string.IsNullOrWhiteSpace(rawId))
        {
            id = DeriveId(ownerId, slug); // выведен: валиден по построению
        }
        else if (PluginId.IsValid(rawId))
        {
            id = rawId; // задан и валиден
        }
        else
        {
            id = null;
            problems.Add(new PluginPackageProblem("bad-id",
                $"id «{rawId}» не по формату {{publisher}}/{{slug}} ([a-z0-9-]+)"));
        }

        return (id, problems);
    }
}
