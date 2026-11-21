using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Реализация детского гейта по <see cref="IChildModePolicy"/>: снимок читается на каждый вызов
/// (выключение профиля действует со следующего), решение fail-closed.
/// </summary>
// Калькулятор лимитов опционален (GetService в регистрации ядра): журнал присутствия и шов Spend
// живут в Main-составе (Program.cs), а тестовый хост ядра их не поднимает. Отсутствие — не
// «разрешить всё»: для ребёнка отказ текстом Uncheckable (fail-closed), тот же смысл, что у
// «бюджет не проверить» в калькуляторе. В боевом составе он есть всегда — сторож
// ChildPolicyWiringGuardTests проверяет, что гейт из контейнера держит непустой калькулятор.
// Пул подписок и реестр провайдеров (Т5a, И2′) нужны сверке маршрута в LaunchMismatch; их отсутствие — не
// «разрешить»: без них маршрут не доказать, детский запуск отказывает (fail-closed, как у калькулятора).
// Стор персон и реестр проектов (Б2b, И10) нужны живому маршруту урока — модели
// персоны-руководителя проекта-предмета. Реестр проектов держит и сверку проекта урока (Б9a):
// без него урок не в проекте-предмете — отказ запуска. Стор персон и резолвер модели опциональны
// по той же причине, что и остальные: их отсутствие не «разрешает» — живой маршрут не развёрнут,
// и урок отказывается с причиной (переходная ветка по полям предмета снята Б17c).
// Шина событий (Б4b) опциональна, как остальные: без неё отказ остаётся с причиной в журнале,
// не происходит только рассылка кураторам. Единая точка публикации события отказа — LogRefusal;
// подписчик на нём (ChildRouteRefusalNotifier, Main) этого класса не знает, и он не знает его
// (тот же шов, что у стоп-сигнала, KM-B6d).
public sealed class ChildModeGate(IChildModePolicy policy, ChildLimitsCalculator? limits = null,
    ILogger<ChildModeGate>? log = null, ClaudeSubscriptionPool? pool = null,
    LlmProviderRegistry? providers = null, ProjectManager? projects = null,
    PersonaManager? personas = null, ModelAssignmentResolver? assignments = null,
    ITurnEventBus? events = null) : IChildModeGate
{
    private const string DeniedText = "Этот инструмент недоступен в детском режиме";

    public string? Denied(string server, string tool, string? ownerId)
    {
        // Пустой владелец политика читает как «не ребёнок» — гейт отказывает сам (fail-closed)
        if (string.IsNullOrWhiteSpace(ownerId)) return DeniedText;
        if (policy.For(ownerId) is not { } snapshot) return null;
        if (!snapshot.DenyAll && ChildModeAllowList.Allows(server, tool)) return null;
        return DeniedText;
    }

    public bool AllowsServer(string? ownerId, string server)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return false;
        if (policy.For(ownerId) is not { } snapshot) return true;
        return !snapshot.DenyAll && ChildModeAllowList.AllowsServer(server);
    }

    // Чат без владельца (пустой или null ownerId) — не детский (R-KM-B3b): тот же корень, что у
    // LaunchMismatch и LaunchLimitRefusal. IsChild и PolicyFor для пустого владельца отвечают
    // одинаково — «не ребёнок».
    public bool IsChild(string? ownerId) =>
        !string.IsNullOrWhiteSpace(ownerId) && policy.For(ownerId) is not null;

    // И3 (KM-B5d): состояние считает калькулятор (KM-B5c) — здесь только перевод состояния в детский
    // отказ и единственное исключение (чат онбординга правил). Своего состояния у гейта нет: каждый
    // вызов считает заново, без кэша (правка профиля или выключение Spend действуют со следующего).
    public string? LaunchLimitRefusal(string? ownerId, Session session, DateTimeOffset now)
    {
        // Пустой владелец и не-ребёнок — не детский ход, сверки нет (тот же корень, что у LaunchMismatch)
        if (string.IsNullOrWhiteSpace(ownerId)) return null;
        if (policy.For(ownerId) is null) return null;
        // Калькулятора нет → лимитов не проверить → отказа не снять (fail-closed, см. комментарий класса)
        if (limits is null) return ChildLimitTexts.Uncheckable;

        // Пространство чата — ChildSpace, метка закреплённая при создании (Session.cs:541). Пустой ключ
        // — «входить некуда»: это уже правило калькулятора (его out_of_window), отдельной ветки здесь нет.
        // Чат онбординга правил (OnboardingKinds.ChildRules) — единственное исключение И3: отсутствие
        // согласия его не останавливает, но окно, дневной лимит и бюджет действуют и на него — поэтому
        // он читается с rulesExempt (состояние rules_pending для него не вычисляется).
        var rulesChat = session.OnboardingKind == OnboardingKinds.ChildRules;
        ChildStatus status;
        try
        {
            // null — политика выключилась между сверкой И1-бис и расчётом: ребёнком владелец быть
            // перестал, ход идёт обычным путём (тот же исход, что у Evaluate для не-ребёнка).
            if (limits.Evaluate(ownerId, session.ChildSpace ?? "", now, rulesExempt: rulesChat) is not { } s)
                return null;
            status = s;
        }
        catch (Exception e)
        {
            // Любой сбой расчёта (профиль перестал читаться между сверкой И1-бис и расчётом — это
            // ChildPolicyUnavailableException, а также любое иное исключение, включая отмену):
            // отказ хода, fail-closed. Наружу — детский текст, технического имени и сообщения
            // ошибки ребёнок не видит; причина остаётся в серверном журнале: id владельца и тип
            // исключения, без текста ребёнка (хвост R-KM-B5d). Без логгера отказ тот же —
            // просто не записанный (тот же стиль, что у опционального логгера политики).
            log?.LogWarning(e, "Детский режим: расчёт лимитов не прошёл (ребёнок {Owner}, {ErrorType})",
                ownerId, e.GetType().Name);
            return ChildLimitTexts.Uncheckable;
        }

        return status.State is ChildState.Ok or ChildState.Warn ? null : ChildLimitTexts.For(status.State);
    }

    // И10 (дефект a771ca12): вердикт запуска хода ребёнка на КАЖДОМ ходу. Состав — ровно те же
    // две проверки, что делает сборка контекста (TurnLaunchContextFactory.Compose), поэтому второй
    // места правды нет: здесь они спрашиваются ДО прикосновения к процессу, при живом процессе
    // Compose не зовётся вовсе (TurnProcessHost.cs:189) — без этой точки ни И1-бис, ни И3 не
    // работали бы ни на одном ходу живого чата. Порядок тот же, что в Compose: сначала маршрут,
    // потом состояние лимитов. Зовётся после догона: он приводит закреплённые поля к живому
    // маршруту, поэтому сверка бьёт по несводимому рассогласованию, а не по всякой правке персоны.
    public string? TurnRefusalFor(string? ownerId, Session session, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return null;
        if (policy.For(ownerId) is not { } snapshot) return null;

        if (LaunchMismatch(ownerId, session) is { } mismatch)
        {
            // Урок (тот же признак, что у LiveRouteOf/LiveRouteFor: метка пространства и не чат
            // правил): отказ его живого маршрута публикуется здесь (тот же LogRefusal, что у
            // LiveRouteFor) — поэтому и текст с обещанием держится этой публикацией. Дедуп
            // нотификатора по (ребёнок, предмет, причина) делает повтор той же причины молчанием:
            // уведомления она не удваивает. Чат правил этой точки не касается — рассылки не будет
            // никогда, и обещать нечего: текст без обещания (тот же порядок, что у внутренних
            // точек отказа ChildRoute).
            bool lessonChat = session.ChildSpace is not null && session.OnboardingKind != OnboardingKinds.ChildRules;
            if (lessonChat) LogRefusal(ownerId, session, mismatch);
            return lessonChat ? ChildRoute.RefusedTextFor(snapshot) : ChildRoute.RefusedTextNoGuardians;
        }
        return LaunchLimitRefusal(ownerId, session, now);
    }

    // Чат без владельца — не детский: правило держит сам гейт, а не политика — пустому владельцу
    // снимка нет даже если политика отдала бы снимок на любой id (тот же ответ, что у IsChild).
    public ChildPolicySnapshot? PolicyFor(string ownerId) =>
        string.IsNullOrWhiteSpace(ownerId) ? null : policy.For(ownerId);

    public string? LaunchMismatch(string? ownerId, Session session)
    {
        // Детский чат всегда имеет владельца-ребёнка: чат без владельца — не детский, сверять нечего
        if (string.IsNullOrWhiteSpace(ownerId)) return null;
        if (policy.For(ownerId) is not { } snapshot) return null;
        if (snapshot.DenyAll || snapshot.Profile is not { } profile)
            return "профиль детского режима не проходит проверку";
        // Б9a (решение Андрея 19.11, ADR-041 ред. 7): урок — чат проекта-предмета, куратор видит
        // все уроки ребёнка. Чат правил живёт вне проекта как прежде — его касается только
        // последняя строка. Чат «Уроков» сверяется с проектом и руководителем ДО живой маршрута:
        // без них сравнивать поля чата не с чем.
        var rulesChat = session.OnboardingKind == OnboardingKinds.ChildRules;
        if (!rulesChat)
        {
            // Скрытый от куратора урок не стартует: «куратор видит все уроки» — единственный
            // рубеж против PrivateToUserId на уроке (создаётся без него и ребёнок не ставит — Б9b).
            if (session.PrivateToUserId is not null)
                return "скрытый от куратора урок не стартует";
            // Детский чат вне проекта — «старый урок»: только чтение, новых ходов нет (И1-бис
            // «ProjectId == null» уроку снимается именно здесь).
            if (session.ProjectId is null)
                return "старый урок только для чтения: урок — чат проекта-предмета";
            // Проект чата — проект-предмет этого ребёнка (per-owner: чужой проект-предмет с тем
            // же slug предмета — не этот случай) и предмет чата — предмет этого проекта.
            var project = projects?.GetByOwner(ownerId).FirstOrDefault(p => p.Id == session.ProjectId);
            // null-проверка явная: nullability-потока через хелпер ProjectCapabilities компилятор
            // не держит, а ниже project.ChildSubjectId без неё CS8602.
            if (project is null || !ProjectCapabilities.IsChildSubject(project))
                return "урок не в проекте-предмете этого ребёнка";
            if (!string.Equals(project.ChildSubjectId, session.ChildSubject, StringComparison.OrdinalIgnoreCase))
                return "предмет чата не совпадает с предметом проекта";
        }
        // Чат правил сверяется с полями профиля, чат «Уроков» — с ЖИВЫМ маршрутом предмета (моделью
        // его персоны-руководителя, решение 71): проверка на каждом запуске, а не при записи. Поля
        // чата — кэш: их приводит догон (CatchUpChildChatAsync), поэтому строгая сверка по ключу
        // остаётся и после любой двери правки персоны. Персона урока сверяется с route.Persona —
        // при существующем проекте это и есть DefaultPersonaId (руководитель, решение 69).
        var verdict = LiveRouteOf(ownerId, profile, session);
        if (verdict.Route is not { } route) return verdict.Reason;
        var (persona, provider, model, _) = route;
        if (session.PersonaId != persona)
            return "персона чата не совпадает с репетитором";
        if (session.Provider != provider)
            return "поставщик чата не совпадает с поставщиком маршрута";
        if (session.Model != model)
            return "модель чата не совпадает с моделью репетитора";
        // Маршрут (И2′): вердикт ChildRoute по живой паре — тот же, что у LiveRouteFor и у запуска
        // хода. Поля чата уже равны этой паре, поэтому отдельно чат не проверяется: он «закреплён»
        // на этот же маршрут.
        if (!RouteAccepted(route))
            return "маршрут модели не допускается в детском режиме";
        if (rulesChat && session.ProjectId is not null)
            return "чат правил не может принадлежать проекту";
        if (session.Mode != ChildModeLaunch.RequiredMode)
            return "режим чата не совпадает с режимом детского профиля";
        return null;
    }

    // Живой маршрут для догона перезакрепления на старте хода: та же точка, что у сверки
    // (LiveRouteOf и RouteAccepted), второго места правды нет. null — владелец не ребёнок, профиль
    // не читается, чат не «Уроков», предмет закрыт, репетитора нет или ChildRoute маршрут не
    // принимает (причину несёт LogRefusal).
    public ChildRepinTarget? LiveRouteFor(string? ownerId, Session session)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return null;
        if (policy.For(ownerId) is not { DenyAll: false, Profile: { } profile }) return null;
        if (session.ChildSpace is null || session.OnboardingKind == OnboardingKinds.ChildRules) return null;
        var verdict = LiveRouteOf(ownerId, profile, session);
        if (verdict.Route is not { } route)
        {
            LogRefusal(ownerId, session, verdict.Reason);
            return null;
        }
        if (!RouteAccepted(route))
        {
            LogRefusal(ownerId, session, "маршрут модели не допускается в детском режиме");
            return null;
        }
        return new ChildRepinTarget(route.Persona, route.Provider, route.Model);
    }

    // Точка отказа живого маршрута с причиной (решение 71): журнал + единственная публикация
    // события child/route-refusal (Б4b). Текст ребёнку здесь не формируется: его выбирает фабрика
    // контекста по опекунам профиля (TurnLaunchContextFactory → ChildRoute.RefusedTextFor);
    // эта публикация — единственный источник уведомления (подписка ChildRouteRefusalNotifier),
    // обещание «взрослым отправлено уведомление» держится ровно ею. Любой отказ живого маршрута
    // на запуске детского хода проходит через этот метод (до EnsureProcess догон
    // CatchUpChildChatAsync зовёт LiveRouteFor, TurnIntake.cs:464); внутренние точки отказа
    // (ChildRoute.Resolve, ветка env в ClaudeSession, фабрика, шлюз) события не публикуют и
    // говорят текст без обещания (RefusedTextNoGuardians). Причина уходит кураторам
    // через подписчика ChildRouteRefusalNotifier — прямой связи гейта на нотификатор нет (KM-B6d).
    // Fire-and-forget: шина не бросает никогда (контракт PublishAsync), а исход отказа уже решён —
    // рассылка не может его изменить.
    private void LogRefusal(string ownerId, Session session, string? reason)
    {
        log?.LogWarning("Детский режим: живой маршрут чата {Session} ребёнка {Owner} не развёрнут: {Reason}",
            session.Id, ownerId, reason ?? "неизвестно");
        _ = events?.PublishAsync(new ChildRouteRefusalEvent(
            // TurnSeq/AgentDepth = 0 — хода нет: та же конвенция «синтетического хода», что у
            // локального голосового (комментарий у HumanMessageAccepted).
            new TurnContext(session.Id, ownerId, 0, 0, session.ProjectId), session.ChildSubject,
            reason ?? "неизвестно"));
    }

    private readonly record struct RouteFieldsOf(string Persona, string Provider, string Model, bool Acknowledged);

    // Итог развёртки живого маршрута: Route — он же (null — маршрута нет), Reason — причина отказа
    // (её читает сверка запуска; для LiveRouteFor она уходит в журнал).
    private readonly record struct RouteVerdict(RouteFieldsOf? Route, string? Reason);

    // Живой маршрут, с которым обязан совпасть чат: чат правил — поля профиля; чат «Уроков» —
    // модель персоны-руководителя проекта-предмета этого предмета (И10, решение 71).
    // Согласие, которым меряется маршрут (решение 26), — ОДНО на профиль: его поле читаем для
    // обеих форм чата. Согласия предмета (поле снято Б17c) здесь нет
    // никогда: оно давалось на меньший риск и маршрут больше не открывает.
    private RouteVerdict LiveRouteOf(string ownerId, ChildProfile profile, Session session)
    {
        var acknowledged = profile.RouteRiskAcknowledgedAt is not null;
        if (session.ChildSpace is null || session.OnboardingKind == OnboardingKinds.ChildRules)
            return new(new(profile.TutorPersonaId, profile.ProviderKey, profile.Model, acknowledged), null);

        var subject = session.ChildSubject is { } id
            ? profile.Subjects?.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;
        if (subject is not { Enabled: true }) return new(null, "Этот предмет закрыт");

        // Проект-предмет этого предмета (его заводит провижер, Б17a) — единственный источник
        // репетитора и модели урока (Б17c: полей предмета и переходной ветки больше нет).
        // Нет проекта или нет стора персон — отказ с причиной, а не урок по чужим полям.
        var project = projects?.GetByOwner(ownerId).FirstOrDefault(p => ProjectCapabilities.IsChildSubject(p)
            && string.Equals(p.ChildSubjectId, subject.Id, StringComparison.OrdinalIgnoreCase));
        if (project is null || personas is null || assignments is null)
            return new(null, "у предмета нет проекта-предмета");

        if (string.IsNullOrWhiteSpace(project.DefaultPersonaId)) return new(null, "у предмета нет репетитора");
        // Сбой резолва персоны (персона удалена или не принадлежит ребёнку) — отказ запуска с
        // причиной, без необработанного исключения: наружу идёт этот текст, не исключение.
        if (personas.Get(project.DefaultPersonaId!, ownerId) is not { } tutor)
            return new(null, "персона-репетитор предмета не найдена");

        // Штатная развёртка модели персоны для места «чат с персоной» у владельца-ребёнка: явная
        // модель → уровень и матрицы персоны → слоты (ModelAssignmentResolver.PersonaModel, затем
        // Resolve места — он разворачивает маркер tier:*/preset:* в первый шаг, как создание чата).
        var model = providers?.CanonicalizeModel(assignments.Resolve(LocalActionCatalog.ChatPersona,
            assignments.PersonaModel(tutor, ownerId,
                LocalActionCatalog.DefaultTierOf(LocalActionCatalog.ChatPersona)), ownerId));
        if (string.IsNullOrWhiteSpace(model)) return new(null, "у репетитора нет развёрнутой модели");

        // Поставщик — по модели: модель резолвится в провайдера LlmProviders — он и есть поставщик
        // хода. Claude-модель провайдера не имеет: её обслуживает запись пула — аккаунт ПРОФИЛЯ
        // ребёнка (ChildProfile.ProviderKey, Б17c: полей предмета нет). Pick здесь не зовётся
        // никогда (И2′: ребёнок не переезжает между аккаунтами молча).
        var provider = providers?.ResolveByModel(model)?.Key
            ?? (pool?.All.Any(e => string.Equals(e.Key, profile.ProviderKey, StringComparison.Ordinal)) == true
                ? profile.ProviderKey : null);
        if (provider is null) return new(null, "маршрут модели не допускается в детском режиме");

        return new(new(project.DefaultPersonaId!, provider, model!, acknowledged), null);
    }

    // Маршрут (И2′): маркер tier:*/preset:* в модели или пара, которую ChildRoute не принимает.
    private bool RouteAccepted(RouteFieldsOf r) =>
        ChildRoute.Evaluate(r.Provider, r.Model, r.Acknowledged, pool, providers).Accepted;
}
