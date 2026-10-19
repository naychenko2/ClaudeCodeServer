using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Глобальный id пакета плагина <c>{publisher}/{slug}</c> (ADR-031 §2.2). <see cref="PluginId.Pattern"/>:
/// обе части — <c>[a-z0-9-]+</c>, ровно один разделитель. Publisher — <c>local-{ownerShort}</c>
/// (MVP) или явный publisher каталога.
///
/// Метод <see cref="TryNormalize"/> — ЕДИНАЯ точка нормализации (M3): принимает URL-форму
/// <c>publisher~slug</c> (один сегмент маршрута, M1) или каноническую <c>publisher/slug</c>,
/// приводит <c>~</c> → <c>/</c> и проверяет формат. Используют и <c>PluginStore</c> (путь
/// <c>packages/{id}/{version}</c>), и <c>PluginUninstall</c> (удаление по id): один и тот же
/// метод, чтобы два места не разъехались.
/// </summary>
public sealed record PluginId
{
    /// <summary>Формат id: <c>{publisher}/{slug}</c>, обе части <c>[a-z0-9-]+</c>.</summary>
    public const string Pattern = "^[a-z0-9-]+/[a-z0-9-]+$";

    /// <summary>
    /// Грамматика slug <c>[a-z0-9-]+</c> (часть id после '/'). Единый источник правды
    /// (minor 14, D2): <see cref="IsValidSlug"/> используют и черновики
    /// (<c>PluginDraftService.IsValidSlug</c>), и проверка параметра метки источника
    /// (<c>PluginInstaller.IsLabelParamValid</c>) — два места не разъезжаются.
    /// </summary>
    public const string SlugPattern = "^[a-z0-9-]+$";

    /// <summary>
    /// Потолок длины slug: 64. Выровнен с клиентской проверкой <c>isDraftSlug</c>
    /// (<c>frontend/src/features/plugins/feed/records.tsx</c>), которая держит тот же 64.
    /// Единый источник правды: <see cref="IsValidSlug"/> применяет его и черновики, и
    /// параметр метки источника. Slug как часть id установленного пакета валидируется
    /// через <see cref="Pattern"/> (без потолка) — потолок здесь не ломает существующие
    /// пакеты: повторная установка сверяет id через <see cref="IsValid"/>, а не <see cref="IsValidSlug"/>.
    /// </summary>
    public const int MaxSlugLength = 64;

    private static readonly Regex Regex = new(Pattern, RegexOptions.Compiled);
    private static readonly Regex SlugRegex = new(SlugPattern, RegexOptions.Compiled);

    /// <summary>Каноническая форма id: <c>publisher/slug</c>.</summary>
    public string Value { get; }

    public PluginId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Нормализовать и проверить: <paramref name="raw"/> — URL-форма <c>publisher~slug</c>
    /// или каноническая <c>publisher/slug</c>. <c>true</c> — формат корректен, <paramref name="id"/>
    /// — каноническая форма. <c>false</c> — пусто или не по формату (M3: 400, не 500).
    /// </summary>
    public static bool TryNormalize(string? raw, out PluginId id)
    {
        id = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var normalized = raw.Replace('~', '/');
        if (!Regex.IsMatch(normalized)) return false;
        id = new PluginId(normalized);
        return true;
    }

    /// <summary>Проверка формата (M3): <c>true</c> — id валиден.</summary>
    public static bool IsValid(string? raw) => TryNormalize(raw, out _);

    /// <summary>
    /// Проверка slug (грамматика <see cref="SlugPattern"/>, потолок <see cref="MaxSlugLength"/>):
    /// <c>true</c> — непустой, по формату и не длиннее <see cref="MaxSlugLength"/>.
    /// </summary>
    public static bool IsValidSlug(string? slug) =>
        slug is { Length: > 0 and <= MaxSlugLength } && SlugRegex.IsMatch(slug);
}
