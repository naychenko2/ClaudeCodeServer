namespace ClaudeHomeServer.Services.Spend;

// Гейт доступа к аналитике расхода. Правила (спека AC-4/AC-5): не-админ видит ТОЛЬКО свои
// данные — scope=all и разрез «user» ему были недоступны; с общими проектами (S2a-6b) «свои
// данные» означают шире: свои записи как арендатора (свои проекты, включая чужие ходы в них)
// и как актора (свои ходы в чужом общем проекте). Поэтому:
//  • не-админ всегда ограничен Me = текущий пользователь — чужое на уровне запроса
//    недостижимо по-прежнему;
//  • разрез «user» не-админу открыт: в его срезе он даёт владельцу разбивку по участникам
//    его общих проектов, а участнику — только его самого; посторонний не видит ничего;
//  • фильтр по другому пользователю остаётся админскому: он резолвит ключ оси «user»
//    (актор, а без актора — владелец), а не арендатора.
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
        string? provider, string? model, string? source)
    {
        var all = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
        if (!isAdmin)
        {
            if (all)
                return Resolution.Denied("Срез по всем пользователям доступен только администратору");
            if (user is not null && user != currentUserId)
                return Resolution.Denied("Фильтр по другому пользователю доступен только администратору");
        }

        // mine: свои записи (арендатор или актор) — принудительно; admin в scope=all сужается
        // фильтром user по ключу оси «user»
        var filter = new SpendFilter(
            Owner: null, Project: project, Chat: chat, Task: task, Persona: persona,
            Provider: provider, Model: model, Source: source,
            Me: all ? null : currentUserId,
            User: all ? user : null);
        return new Resolution(filter, all, null);
    }
}
