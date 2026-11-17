using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Реализация детского гейта по <see cref="IChildModePolicy"/>: снимок читается на каждый вызов
/// (выключение профиля действует со следующего), решение fail-closed.
/// </summary>
public sealed class ChildModeGate(IChildModePolicy policy) : IChildModeGate
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
