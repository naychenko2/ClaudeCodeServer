using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Шов к стору проектов «Личное» (ADR-040): отдельный data/personal-projects.json, одна запись
// на владельца. «Личного» нет в ProjectManager — ни один список и ни один GetById его не
// отдаёт; сюда ходят только осознанные потребители (медиа-области, LoRA-финишер, бэкап,
// каскад удаления пользователя). На вызовы GetByIdIncludingPersonal/GetAllPersonal стоит
// сторож (Guards.Tests, PersonalProjectAccessGuardTests).
public interface IPersonalProjects
{
    /// <summary>«Личное» владельца либо null. Только чтение: не создаёт запись и папку.</summary>
    Project? GetPersonalOf(string ownerId);

    /// <summary>
    /// «Личное» владельца: идемпотентно создаёт запись и папку, при пропаже папки — пересоздаёт.
    /// Бросает InvalidOperationException, если у владельца нельзя вычислить папку.
    /// </summary>
    Task<Project> EnsurePersonalAsync(string ownerId, CancellationToken ct = default);

    /// <summary>Запись «Личного» по id либо null; в ProjectManager.GetById её нет намеренно.</summary>
    Project? GetByIdIncludingPersonal(string id);

    /// <summary>Все записи «Личного» — для инфраструктурных потребителей (бэкап, LoRA-финишер).</summary>
    IReadOnlyCollection<Project> GetAllPersonal();

    /// <summary>Удаляет запись владельца (папка на диске остаётся). false — записи не было.</summary>
    bool DeleteOf(string ownerId);
}
