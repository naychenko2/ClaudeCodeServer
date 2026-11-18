using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Единственная точка правды «ребёнок ли владелец и что ему можно» (ADR-041, разрез §1).
/// Потребители (KM-B2…KM-B5d) не читают user.ChildMode инлайново: единственный способ
/// узнать ребёнка — этот снимок. Источник истины — стор пользователей, а не claim токена
/// (тот же приём, что у AdminByStore): сервисный токен хода указывает на того же владельца.
/// </summary>
public interface IChildModePolicy
{
    /// <summary>
    /// Снимок политики для владельца.
    /// null — обычный пользователь: профиля нет или он выключен (Enabled=false).
    /// Снимок с DenyAll=true — профиль есть, но не проходит валидацию (пустой
    /// TutorPersonaId/ProviderKey/Model, окно не разбирается как HH:mm) или его чтение
    /// сорвалось: ребёнок остаётся ребёнком, но запрещено ему всё (fail-closed).
    /// Снимок не кэшируется: выключение или правка профиля должны действовать со следующего вызова.
    /// </summary>
    ChildPolicySnapshot? For(string ownerId);
}

/// <summary>
/// Результат одного чтения политики. Profile — профиль из стора; null он только когда
/// само чтение сорвалось (профила наружу не вынести, отказа это не отменяет).
/// DenyAll — ребёнку запрещено всё: потребитель не читает лимиты, он отказывает операцию.
/// </summary>
public sealed record ChildPolicySnapshot(string OwnerId, ChildProfile? Profile, bool DenyAll);
