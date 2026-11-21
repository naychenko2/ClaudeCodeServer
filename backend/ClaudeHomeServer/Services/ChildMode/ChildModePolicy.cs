using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Политика по стору пользователей (образец: AdminByStoreHandler — проверка по стору, а не по
/// claim): читает User.ChildMode по ownerId. Своего кэша нет — UserStore уже держит людей
/// в памяти, а снимок обязан отражать последнюю правку профиля, включая его выключение.
/// Исключение при чтении профиля наружу не выходит: сорванное чтение — DenyAll (fail-closed).
/// </summary>
public sealed class ChildModePolicy(IUserStore users, ILogger<ChildModePolicy>? log = null) : IChildModePolicy
{
    public ChildPolicySnapshot? For(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return null;
        try
        {
            // Пользователя нет или профиля нет — обычный пользователь.
            if (users.GetById(ownerId) is not { ChildMode: { } profile }) return null;
            // Профиль есть, но выключен — тоже обычный пользователь: выключение и есть Enabled=false.
            if (!profile.Enabled) return null;
            // Профиль есть — ребёнок; нечитаемый профиль не отменяет детства, но запрещает всё.
            return new ChildPolicySnapshot(ownerId, profile, DenyAll: !IsValid(profile));
        }
        catch (Exception e)
        {
            // Сорванное чтение профиля — не причина бросать в вызывающего: отказываем всё.
            log?.LogWarning(e, "Детский режим: не удалось прочитать профиль владельца {Owner}", ownerId);
            return new ChildPolicySnapshot(ownerId, null, DenyAll: true);
        }
    }

    // Валидация профиля (разрез §1): обязательные поля детского хода заполнены, каждое окно
    // каждого пространства разбирается как HH:mm. Пустые окна — не провал: «окон нет» —
    // законное значение SpaceLimits, а отказывать вне окна — дело потребителя.
    private static bool IsValid(ChildProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.TutorPersonaId) &&
        !string.IsNullOrWhiteSpace(profile.ProviderKey) &&
        !string.IsNullOrWhiteSpace(profile.Model) &&
        profile.Spaces.Values.All(space => space.Windows.All(WindowParses)) &&
        SubjectsValid(profile.Subjects);

    // И1-бис по предмету (разрез Т1c): id всех предметов — slug и уникален без учёта регистра;
    // репетитор и модель предмета живут в проекте-предмете (Б17c), а не в записи профиля —
    // ни пустой ключ персоны, ни пустой маршрут записи предмета профиль не портят.
    private static bool SubjectsValid(List<ChildSubject>? subjects)
    {
        if (subjects is null) return true;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in subjects)
        {
            if (subject is null || !SubjectId.IsMatch(subject.Id ?? "") || !ids.Add(subject.Id!)) return false;
        }
        return true;
    }

    // [GeneratedRegex] не нужен: проверка редкая; ^…$ без RegexOptions.Multiline, но $ пропускает
    // хвостовой \n — поэтому \z.
    private static readonly System.Text.RegularExpressions.Regex SubjectId = new(@"^[a-z0-9-]{1,32}\z");

    // Тот же разбор границы окна, что у DailyTimeWindow.TryParseBounds: «25:00» и мусор — не окно.
    private static bool WindowParses(TimeWindow? w) =>
        w is null || (TimeOnly.TryParseExact(w.From, "HH:mm", out _) && TimeOnly.TryParseExact(w.To, "HH:mm", out _));
}
