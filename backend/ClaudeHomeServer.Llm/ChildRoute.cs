using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Отказ запуска хода ребёнка (И2′): маршрут профиля/предмета не принят <see cref="ChildRoute"/>
/// (нет записи пула с API-ключом, запись несёт токен подписки, провайдер выключен, нет согласия
/// админа и т. п.). Откатываться на родную подписку нельзя — только отказ.
/// Текст показывается человеку (TurnFailureText.ForException).
/// </summary>
public sealed class ChildRouteRefusedException(string message) : InvalidOperationException(message);

public enum ChildRouteKind
{
    /// <summary>Запись ClaudeSubscriptionPool с API-ключом (модель Anthropic).</summary>
    Pool,
    /// <summary>Запись LlmProviders (облако или локальная модель).</summary>
    Provider,
}

public enum ChildRouteRefusal
{
    /// <summary>Ключ не резолвится в допустимую запись: нет записи, подписка, выключенный провайдер.</summary>
    NoRoute,
    /// <summary>Модель — маркер (tier:*, preset:*, default, claude) или пусто.</summary>
    ModelNotConcrete,
    /// <summary>Облачная модель без цены: бюджет И3 молча перестал бы ограничивать.</summary>
    ModelUnpriced,
    /// <summary>Не-пул без согласия админа на риск маршрута.</summary>
    RiskUnacknowledged,
    /// <summary>Модель принадлежит другому провайдеру (или у пул-ключа модель стороннего).</summary>
    ModelNotOfProvider,
}

/// <summary>Результат классификации маршрута: принятый (Kind + запись) либо отказ.</summary>
public sealed record ChildRouteDecision(
    ChildRouteKind? Kind, ClaudeSubscriptionConfig? Pool, LlmProviderConfig? Provider, ChildRouteRefusal? Refusal)
{
    public bool Accepted => Refusal is null;
    public static ChildRouteDecision Refuse(ChildRouteRefusal refusal) => new(null, null, null, refusal);
}

/// <summary>
/// Маршрут хода ребёнка (И2′, ADR-041 ред. 2, разрез §3): единственная точка правды «куда может
/// идти ход ребёнка». Её зовут запуск хода (Resolve), валидатор профиля (400), ручка списка
/// маршрутов и сверка И1-бис — правил отбора в двух местах не заводить.
/// Допустимо ровно два вида: запись пула с ApiKey без OAuthToken и Anthropic-моделью, либо запись
/// LlmProviders (Enabled, модель принадлежит провайдеру, у облачной цена &gt; 0, согласие админа).
/// Всё прочее — отказ, а не откат на подписку.
/// </summary>
public static class ChildRoute
{
    public const string RefusedText =
        "Ход не выполнен: для детского режима не настроен допустимый маршрут модели. Обратитесь к администратору.";

    /// <summary>
    /// Конкретная модель: не пусто и не маркер. «tier:*»/«preset:*» ModelAssignmentResolver разворачивает
    /// даже в явной модели (первый шаг пресета ушёл бы на подписку), «default»/«claude» — легаси-значения.
    /// </summary>
    public static bool IsConcreteModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.Trim();
        return !m.Equals(LlmProviderRegistry.DefaultClaudeModel, StringComparison.OrdinalIgnoreCase)
            && !m.Equals(LocalActionOverridesStore.ClaudeRoute, StringComparison.OrdinalIgnoreCase)
            && !m.StartsWith(LocalActionOverridesStore.TierPrefix, StringComparison.OrdinalIgnoreCase)
            && !m.StartsWith(LocalActionOverridesStore.PresetPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Облачная модель с ценой: известна провайдеру и хоть одна сторона &gt; 0 (как ComputeCost).</summary>
    public static bool IsPriced(LlmProviderConfig provider, string? model) =>
        provider.FindModel(model) is { } m && (m.PriceInMissPer1M > 0 || m.PriceOutPer1M > 0);

    /// <summary>
    /// Чистая классификация пары «поставщик + модель». acknowledged — согласие админа на не-Anthropic
    /// маршрут (на валидации — из тела/живого профиля, на запуске — RouteAcknowledgedAt предмета).
    /// providers == null — отказ (fail-closed): без реестра «модель Anthropic» не доказать.
    /// </summary>
    public static ChildRouteDecision Evaluate(string? providerKey, string? model, bool acknowledged,
        ClaudeSubscriptionPool? pool, LlmProviderRegistry? providers)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return ChildRouteDecision.Refuse(ChildRouteRefusal.NoRoute);
        if (!IsConcreteModel(model)) return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelNotConcrete);

        var entry = pool?.All.FirstOrDefault(s => s.Key == providerKey);
        if (entry is not null)
        {
            // Запись с OAuthToken — подписка, даже если ApiKey тоже задан: токен мог бы уехать в ход.
            if (string.IsNullOrWhiteSpace(entry.ApiKey) || !string.IsNullOrWhiteSpace(entry.OAuthToken)
                || providers is null)
                return ChildRouteDecision.Refuse(ChildRouteRefusal.NoRoute);
            // Ключ пула годится только Anthropic-модели: чужая модель с ним — чужой эндпоинт.
            if (providers.ResolveByModel(model) is not null)
                return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelNotOfProvider);
            return new(ChildRouteKind.Pool, entry, null, null);
        }

        var provider = providers?.GetByKey(providerKey);
        if (provider is null || !provider.Enabled) return ChildRouteDecision.Refuse(ChildRouteRefusal.NoRoute);
        if (!string.Equals(providers!.ResolveByModel(model)?.Key, provider.Key, StringComparison.Ordinal))
            return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelNotOfProvider);
        if (!provider.IsLocal && !IsPriced(provider, model))
            return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelUnpriced);
        if (!acknowledged) return ChildRouteDecision.Refuse(ChildRouteRefusal.RiskUnacknowledged);
        return new(ChildRouteKind.Provider, null, provider, null);
    }

    /// <summary>
    /// Предмет чата «Уроков» для запуска; null — чат правил (маршрут по полям профиля).
    /// Предмета нет или он выключен — отказ запуска (тот же исход, что «Этот предмет закрыт» в И1-бис).
    /// </summary>
    public static ChildSubject? SubjectFor(ChildPolicySnapshot snapshot, Session session)
    {
        var lessonsChat = session.ChildSpace is not null && session.OnboardingKind != OnboardingKinds.ChildRules;
        if (!lessonsChat) return null;
        var subject = session.ChildSubject is { } id
            ? snapshot.Profile?.Subjects?.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;
        return subject is { Enabled: true } ? subject : throw new ChildRouteRefusedException(RefusedText);
    }

    /// <summary>
    /// Явная модель хода ребёнка (И2′): модель предмета (чат «Уроков») или профиля (чат правил) —
    /// первый шаг цепочки, назначение места ChatPersona её не переопределяет. Без исключений:
    /// нет профиля/предмета — null (отказ даст Resolve при запуске).
    /// </summary>
    public static string? ExplicitModelFor(ChildPolicySnapshot snapshot, Session session)
    {
        if (snapshot.Profile is not { } profile) return null;
        static string? NonBlank(string? m) => string.IsNullOrWhiteSpace(m) ? null : m;
        var lessonsChat = session.ChildSpace is not null && session.OnboardingKind != OnboardingKinds.ChildRules;
        if (!lessonsChat) return NonBlank(profile.Model);
        var subject = session.ChildSubject is { } id
            ? profile.Subjects?.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;
        return NonBlank(subject?.Model);
    }

    /// <summary>
    /// Запуск хода: маршрут предмета (subject) либо — для чата правил (subject == null) — полей профиля,
    /// тем же резолвером. Отказ — <see cref="ChildRouteRefusedException"/>.
    /// </summary>
    public static ChildRouteDecision Resolve(ChildPolicySnapshot snapshot, ChildSubject? subject,
        ClaudeSubscriptionPool? pool, LlmProviderRegistry? providers)
    {
        if (snapshot.DenyAll || snapshot.Profile is not { } profile)
            throw new ChildRouteRefusedException(RefusedText);
        var decision = subject is null
            ? Evaluate(profile.ProviderKey, profile.Model, acknowledged: false, pool, providers)
            : Evaluate(subject.ProviderKey, subject.Model, subject.RouteAcknowledgedAt is not null, pool, providers);
        return decision.Accepted ? decision : throw new ChildRouteRefusedException(RefusedText);
    }
}
