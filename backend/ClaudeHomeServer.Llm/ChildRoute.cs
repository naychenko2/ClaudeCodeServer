using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Отказ запуска хода ребёнка (И2″): маршрут профиля/предмета не принят <see cref="ChildRoute"/>
/// (нет записи ни в пул, ни в провайдер — в т. ч. первичный аккаунт хоста без записи, план аккаунта
/// не тянет модель, модель не принадлежит поставщику, нет согласия профиля на риск маршрута
/// и т. п.). Откатываться на родную подписку нельзя — только отказ.
/// Текст показывается человеку (TurnFailureText.ForException).
/// </summary>
/// <summary>
/// Отказ запуска хода ребёнка. Message — текст, который доезжает до человека (через
/// TurnFailureText.ForException как есть): внутреннего имени причины в нём нет никогда.
/// Reason — сама причина (человеческий текст сверки гейта): она для журнала и разбора,
/// в ленту не выходит. Пустой — когда точки отказа её не называют (отказ вердиктом ChildRoute).
/// </summary>
public sealed class ChildRouteRefusedException(string message) : InvalidOperationException(message)
{
    public ChildRouteRefusedException(string message, string? reason) : this(message) => Reason = reason;

    public string? Reason { get; }
}

public enum ChildRouteKind
{
    /// <summary>Запись ClaudeSubscriptionPool: токен setup-token, API-ключ или оба (И2″, решение 25).</summary>
    Pool,
    /// <summary>Запись LlmProviders (облако или локальная модель).</summary>
    Provider,
}

public enum ChildRouteRefusal
{
    /// <summary>Ключ не резолвится в допустимую запись: нет записи (в т. ч. PrimaryKey без записи
    /// пула — учётка хостового ~/.claude), выключенный провайдер, нет реестра провайдеров.</summary>
    NoRoute,
    /// <summary>Модель — маркер (tier:*, preset:*, default, claude) или пусто.</summary>
    ModelNotConcrete,
    /// <summary>Облачная модель без цены: бюджет И3 молча перестал бы ограничивать.</summary>
    ModelUnpriced,
    /// <summary>План аккаунта пула не тянет модель (Opus или окно [1m] на плане без Opus/1M):
    /// ход на ней не тратит бюджет, а падает «There's an issue with the selected model».</summary>
    ModelNotOnPlan,
    /// <summary>Нет согласия профиля на риск маршрута (И2″, решение 26): без него не годится
    /// НИ один маршрут — ни запись пула, ни провайдер. Согласия предметов его не заменяют.</summary>
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
/// Маршрут хода ребёнка (И2″, ADR-041 ред. 4 решения 25–26): единственная точка правды «куда может
/// идти ход ребёнка». Её зовут запуск хода (Resolve), валидатор профиля (400), ручка списка
/// маршрутов и сверка И1-бис — правил отбора в двух местах не заводить.
/// Детских ключей нет: ребёнку доступен любой маршрут реестра взрослых — допустимы ровно два вида:
/// любая включённая запись пула (setup-token, API-ключ или оба; модель Anthropic и по плану
/// аккаунта) либо любая включённая запись LlmProviders (Enabled, модель принадлежит провайдеру,
/// у облачной цена &gt; 0). И то и другое — только с согласием профиля на риск маршрута.
/// Всё прочее — отказ, а не откат на подписку.
/// </summary>
public static class ChildRoute
{
    // Текст с обещанием уведомления (решение 71): внутреннего имени причины ребёнок не видит —
    // она остаётся в серверном журнале и идёт кураторам. Тон — тот же, что у текстов состояния И3
    // (ChildLimitTexts): спокойный конец, а не упрёк. Обещание правда только там, где отказ
    // опубликован событием child/route-refusal (единственная публикация — ChildModeGate.LogRefusal;
    // текст по опекунам выбирает фабрика контекста через RefusedTextFor). Наружу этот текст
    // выходит только через RefusedTextFor ниже.
    public const string RefusedText = "Занятие пока недоступно, взрослым отправлено уведомление.";

    // Текст без обещания (доработка ревью Б4b, п.3; второй круг — решение Софьи): рассылку
    // рождает только событие отказа (подписка ChildRouteRefusalNotifier на нём); точки, которые
    // события не публикуют, не вправе обещать уведомление даже при опекунах — его не будет
    // никогда. Текст ведёт ребёнка к взрослому сам: настройки детского режима правит опекун
    // или админ, не ребёнок. Им говорят внутренние точки отказа: SubjectFor/Resolve/ResolveLesson,
    // ветка env ребёнка в ClaudeSession, фабрика адаптеров и шлюз (UpstreamSelector).
    public const string RefusedTextNoGuardians =
        "Занятие пока недоступно. Попроси взрослого открыть настройки детского режима.";

    /// <summary>
    /// Текст отказа для ребёнка по снимку его политики: обещание про уведомление — только при
    /// опекунах в профиле. Решение — наличие опекунов, а не факт рассылки: обещание держится
    /// публикацией события гейтом (ChildModeGate.LogRefusal, fire-and-forget) и не зависит от
    /// того, успел ли сработать подписчик шины. Снимка нет (владелец не ребёнок) или профиль
    /// не читается — обещать некому: текст без уведомления.
    /// </summary>
    public static string RefusedTextFor(ChildPolicySnapshot? snapshot) =>
        snapshot?.Profile?.GuardianUserIds.Count > 0 ? RefusedText : RefusedTextNoGuardians;

    // Метка детского отказа маршрута в ленте (дефект 5f7854e7): по ней фронт (childFeedFilter.ts)
    // узнаёт отказ и показывает ребёнку текст точки отказа как есть — без метки фронт подменяет
    // любой error общей плашкой, и ребёнок не видит честной причины. Тот же приём, что
    // PersonaZone.ChangeCompanionAction у отказа сферы. Ставится ядром приёма (TurnIntake.RefuseTurnAsync)
    // на обеих детских точках отказа; отказ маршрута «сменить собеседника» не несёт никогда.
    public const string RefusalAction = "child-route-refusal";

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
    /// Чистая классификация пары «поставщик + модель». acknowledged — ОДНО согласие профиля ребёнка
    /// на риск маршрута (И2″, решение 26): приходит ПАРАМЕТРОМ от вызывающего, здесь поле профиля
    /// не читается (его читает вызывающий — Б2b). Без него не годен ни один маршрут, включая
    /// запись пула: режима «безопасного маршрута без согласия» больше нет — детских ключей нет,
    /// отличать не от чего. Старые согласия предмета (поле снято Б17c) маршрут
    /// не открывают: они давались на меньший риск. Порядок проверки — сначала «что это за маршрут»
    /// (админу нужен конкретик: нет записи / чужая модель / нет цены), согласие — последнее.
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
            // Любая включённая запись пула — маршрут ребёнка (решение 25): токен setup-token,
            // API-ключ или оба. Ограничения по токену сняты: риск закрывает согласие профиля ниже,
            // а не форма аутентификации.
            if (providers is null) return ChildRouteDecision.Refuse(ChildRouteRefusal.NoRoute);
            // Ключ пула годится только Anthropic-модели: чужая модель с ним — чужой эндпоинт.
            if (providers.ResolveByModel(model) is not null)
                return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelNotOfProvider);
            // Способность ПЛАНА аккаунта (Opus/1M): на неспособном плане ход не тратит бюджет,
            // а падает «There's an issue with the selected model» — это не маршрут ребёнка.
            if (!pool!.SupportsModel(entry.Key, model))
                return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelNotOnPlan);
            return Accept(ChildRouteKind.Pool, entry, null, acknowledged);
        }

        var provider = providers?.GetByKey(providerKey);
        if (provider is null || !provider.Enabled) return ChildRouteDecision.Refuse(ChildRouteRefusal.NoRoute);
        if (!string.Equals(providers!.ResolveByModel(model)?.Key, provider.Key, StringComparison.Ordinal))
            return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelNotOfProvider);
        if (!provider.IsLocal && !IsPriced(provider, model))
            return ChildRouteDecision.Refuse(ChildRouteRefusal.ModelUnpriced);
        return Accept(ChildRouteKind.Provider, null, provider, acknowledged);
    }

    // Единственная точка, где согласие переводится в вердикт: форма маршрута проверена —
    // без согласия профиля она не принимается ни в какой форме (И2″).
    private static ChildRouteDecision Accept(ChildRouteKind kind, ClaudeSubscriptionConfig? pool,
        LlmProviderConfig? provider, bool acknowledged) =>
        acknowledged
            ? new ChildRouteDecision(kind, pool, provider, null)
            : ChildRouteDecision.Refuse(ChildRouteRefusal.RiskUnacknowledged);

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
        // Текст без обещания: этот отказ события не публикует — уведомления не будет никогда.
        return subject is { Enabled: true } ? subject : throw new ChildRouteRefusedException(RefusedTextNoGuardians);
    }

    /// <summary>
    /// Запуск хода чата правил: маршрут — поля профиля, тем же резолвером. Согласие — ПАРАМЕТРОМ
    /// (profileRiskAcknowledged, решение 26): поле живого профиля читает вызывающий (Б2b), здесь
    /// поля профиля не читаются никогда. Отказ — <see cref="ChildRouteRefusedException"/> с текстом
    /// без обещания: эта точка события отказа не публикует (единственная публикация —
    /// ChildModeGate.LogRefusal), поэтому «уведомление отправлено» здесь было бы неправдой.
    /// </summary>
    public static ChildRouteDecision Resolve(ChildPolicySnapshot snapshot,
        bool profileRiskAcknowledged, ClaudeSubscriptionPool? pool, LlmProviderRegistry? providers)
    {
        if (snapshot.DenyAll || snapshot.Profile is not { } profile)
            throw new ChildRouteRefusedException(RefusedTextNoGuardians);
        var decision = Evaluate(profile.ProviderKey, profile.Model, profileRiskAcknowledged, pool, providers);
        return decision.Accepted ? decision : throw new ChildRouteRefusedException(RefusedTextNoGuardians);
    }

    /// <summary>
    /// Запуск хода чата «Уроков»: маршрут — поставщик и модель САМОГО ЧАТА (их закрепил запуск по
    /// персоне-руководителю проекта-предмета и аккаунту профиля, Б17c: полей предмета больше нет).
    /// Согласие и отказ — как у <see cref="Resolve"/>.
    /// </summary>
    public static ChildRouteDecision ResolveLesson(ChildPolicySnapshot snapshot, string? providerKey, string? model,
        bool profileRiskAcknowledged, ClaudeSubscriptionPool? pool, LlmProviderRegistry? providers)
    {
        if (snapshot.DenyAll || snapshot.Profile is null)
            throw new ChildRouteRefusedException(RefusedTextNoGuardians);
        var decision = Evaluate(providerKey ?? "", model ?? "", profileRiskAcknowledged, pool, providers);
        return decision.Accepted ? decision : throw new ChildRouteRefusedException(RefusedTextNoGuardians);
    }


    /// <summary>
    /// Цепочка фолбэка ребёнка (И2″, решение 27): из развёрнутой цепочки места (хвост слотов
    /// владельца-ребёнка, ModelAssignmentResolver.ResolveChain) выпадают облачные шаги без цены —
    /// на них бюджет И3 молча перестал бы ограничивать. Шаги подписки (модели, которым не
    /// принадлежит ни один провайдер) и шаги локальных провайдеров остаются: облако они не тратят.
    /// Чистая функция: первый шаг (закрепление чата) она не трогает — его проверяет Evaluate.
    /// Звать будет запуск хода (Б1b); предпросмотр цепочки для адмена (child-chain) не реализован,
    /// отложен до появления предпросмотра в окне настроек ребёнка.
    /// </summary>
    public static IReadOnlyList<string> ChildChain(IReadOnlyList<string> chain, LlmProviderRegistry providers) =>
        chain.Where(m => IsChainStepAllowed(m, providers)).ToList();

    // Шаг цепочки годится, если он не «облако без цены»: подписка (модель не принадлежит ни одному
    // провайдеру) и локальный провайдер не тратят облако. Модель вне каталога провайдера (по
    // ModelPrefixes она резолвится в него, записи в каталоге нет) — без цены, выпадает: что за неё
    // заплатит ход — неизвестно, а молча неоплаченный шаг в цепочке — ровно тот дефект,
    // которого фильтр и ждёт.
    private static bool IsChainStepAllowed(string? model, LlmProviderRegistry providers)
    {
        var provider = providers.ResolveByModel(model);
        return provider is null || provider.IsLocal || IsPriced(provider, model);
    }
}
