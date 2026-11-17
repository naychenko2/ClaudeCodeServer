using System.Net.Http.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Фикстура предмета детских тестов (разрез «Предметы и репетиторы»): чат «Уроков» создаётся с
/// предметом, а предмет берёт поля из профиля ребёнка — те же, что чат нёс до предметов.
/// </summary>
public static class ChildTestSubjects
{
    public const string Key = "russian";
    public const string Url = "/api/child/spaces/lessons/chat";

    /// <summary>Добавляет в уже сохранённый профиль ребёнка включённый предмет с полями профиля.</summary>
    public static void Seed(IServiceProvider sp, string childId, string key = Key, bool enabled = true)
    {
        var profile = sp.GetRequiredService<UserStore>().GetById(childId)!.ChildMode!;
        profile.Subjects.Add(Make(profile, key, enabled));
    }

    public static ChildSubject Make(ChildProfile profile, string key = Key, bool enabled = true) => new()
    {
        Id = key, Label = "Русский язык", Glyph = "Аа", Color = "info",
        TutorPersonaId = profile.TutorPersonaId, ProviderKey = profile.ProviderKey, Model = profile.Model,
        Enabled = enabled,
    };

    /// <summary>POST создания чата с предметом (по умолчанию — «russian»).</summary>
    public static Task<HttpResponseMessage> CreateChat(HttpClient client, string? subject = Key, string space = "lessons") =>
        client.PostAsJsonAsync($"/api/child/spaces/{space}/chat", new { subject });
}
