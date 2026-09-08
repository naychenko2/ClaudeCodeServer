namespace ClaudeHomeServer.Services.Spend;

// Гейт доступа к аналитике расхода. Правила (спека AC-4/AC-5): не-админ видит ТОЛЬКО свои
// данные — scope=all, фильтр по чужому пользователю и разрез «user» ему недоступны;
// admin в scope=all видит всех (цифры и названия, содержимое недостижимо by design).
// Вынесен из контроллера статикой ради юнит-тестов без ASP.NET-конвейера.
public static class SpendAccess
{
    public sealed record Resolution(SpendFilter Filter, bool AllUsers, string? Error)
    {
        public static Resolution Denied(string error) => new(new SpendFilter(), false, error);
    }

    public static Resolution Resolve(bool isAdmin, string currentUserId, string? scope,
        string? user, string? project, string? chat, string? task, string? persona,
        string? provider, string? model, string? source, string? groupBy = null)
    {
        var all = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
        if (!isAdmin)
        {
            if (all)
                return Resolution.Denied("Срез по всем пользователям доступен только администратору");
            if (user is not null && user != currentUserId)
                return Resolution.Denied("Фильтр по другому пользователю доступен только администратору");
            if (groupBy == "user")
                return Resolution.Denied("Разрез «пользователь» доступен только администратору");
        }

        // mine: владелец принудительный; admin в scope=all может сузиться фильтром user
        var owner = all ? user : currentUserId;
        return new Resolution(new SpendFilter(owner, project, chat, task, persona,
            provider, model, source), all, null);
    }
}
