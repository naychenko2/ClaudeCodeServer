using ClaudeHomeServer.Models;

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
public sealed class ChildModeGate(IChildModePolicy policy, ChildLimitsCalculator? limits = null) : IChildModeGate
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

    public bool IsChild(string? ownerId) =>
        string.IsNullOrWhiteSpace(ownerId) || policy.For(ownerId) is not null;

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
        catch (Exception)
        {
            // Любой сбой расчёта (профиль перестал читаться между сверкой И1-бис и расчётом — это
            // ChildPolicyUnavailableException, а также любое иное исключение): отказ хода, fail-closed.
            // Наружу — детский текст, технического имени и сообщения ошибки ребёнок не видит.
            return ChildLimitTexts.Uncheckable;
        }

        return status.State is ChildState.Ok or ChildState.Warn ? null : ChildLimitTexts.For(status.State);
    }

    public ChildPolicySnapshot? PolicyFor(string ownerId) => policy.For(ownerId);

    public string? LaunchMismatch(string? ownerId, Session session)
    {
        // Детский чат всегда имеет владельца-ребёнка: чат без владельца — не детский, сверять нечего
        if (string.IsNullOrWhiteSpace(ownerId)) return null;
        if (policy.For(ownerId) is not { } snapshot) return null;
        if (snapshot.DenyAll || snapshot.Profile is not { } profile)
            return "профиль детского режима не проходит проверку";
        if (session.PersonaId != profile.TutorPersonaId)
            return "персона чата не совпадает с закреплённым тьютором";
        if (session.Provider != profile.ProviderKey)
            return "поставщик чата не совпадает с поставщиком профиля";
        if (session.Model != profile.Model)
            return "модель чата не совпадает с моделью профиля";
        if (session.ProjectId is not null)
            return "детский чат не может принадлежать проекту";
        if (session.Mode != ChildModeLaunch.RequiredMode)
            return "режим чата не совпадает с режимом детского профиля";
        return null;
    }
}
