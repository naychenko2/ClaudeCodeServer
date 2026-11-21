using System.Net.Http.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Фикстура предмета детских тестов (разрез «Предметы и репетиторы»): чат «Уроков» создаётся с
/// предметом, а предмет — только плитка (Б17c): маршрут и репетитор живут в проекте-предмете.
/// </summary>
public static class ChildTestSubjects
{
    public const string Key = "russian";
    public const string Url = "/api/child/spaces/lessons/chat";

    /// <summary>Момент согласия на риск маршрутов в фикстурах (И2″, решение 26): одно значение на все тесты.</summary>
    public static readonly DateTimeOffset ConsentAt = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Кто дал согласие в фикстурах (админ).</summary>
    public const string ConsentBy = "admin-fixture";

    /// <summary>
    /// Ставит согласие на риск маршрутов в профиль напрямую — как PUT child-profile с acknowledgeRouteRisk=true.
    /// С волны 4 без него маршрут ребёнка не принимается ни в какой форме (ChildRoute.RiskUnacknowledged).
    /// Тесты на отказ без согласия профиль через эту точку НЕ заводят.
    /// </summary>
    public static ChildProfile Consent(ChildProfile profile)
    {
        profile.RouteRiskAcknowledgedAt = ConsentAt;
        profile.RouteRiskAcknowledgedBy = ConsentBy;
        return profile;
    }

    /// <summary>
    /// Добавляет в уже сохранённый профиль ребёнка включённый предмет
    /// и согласие на риск маршрутов (фикстура ребёнка, который может ходить в чат).
    /// </summary>
    public static void Seed(IServiceProvider sp, string childId, string key = Key, bool enabled = true)
    {
        var profile = sp.GetRequiredService<UserStore>().GetById(childId)!.ChildMode!;
        profile.Subjects.Add(Make(profile, key, enabled));
        Consent(profile);
    }

    /// <summary>
    /// Модель репетитора в фикстурах урока (И10): живая модель персоны-руководителя проекта-
    /// предмета. Одно значение на все классы — та же точка, что у назначения места «чат с
    /// персоной» в UI «Назначения».
    /// </summary>
    public const string TutorModel = "sonnet";

    /// <summary>
    /// Конфигурация хоста, на которой урок возможен (тот же состав, что у ChildChatsControllerTests).
    /// Папка песочницы — дом container-ребёнка, внутри него папка предмета
    /// (UserHomeResolver.SubjectHome): без неё провижн проекта-предмета невозможен и
    /// POST …/chat отвечает 409 subject_not_ready (ChildSubjectProjectProvisioner.cs:86).
    /// Рубильник общих проектов открывает проект-предмета его КУРАТОРУ-участнику
    /// (ProjectManager.GetAccessible:86); для самого ребёнка-владельца он не решающий —
    /// замер в копии мутации: без него ChatAttachmentRawControllerTests и ChildRulesControllerTests
    /// остались зелёными (39/39). Он здесь потому, что состав фикстуры один на всех: следующая
    /// фикстура с куратором (её проверка — «урок виден куратору») получает его уже готовым.
    /// prefix — метка папки: каждая фикстура получает свою.
    /// </summary>
    public static void ConfigureSubjectHost(TestWebApplicationFactory host, string prefix)
    {
        host.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        host.ExtraConfig["Sandbox:ProjectsRoot"] =
            Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Живой маршрут репетитора (И10): модель персоны-руководителя — назначением места
    /// «чат с персоной», тем же админским входом, что UI «Назначения» (слотов тиров в тестовом
    /// конфиге нет). Без неё ChildModeGate.LiveRouteOf не разворачивает модель урока, и
    /// POST …/chat отвечает 409 subject_not_ready.
    /// </summary>
    public static void SetTutorModel(IServiceProvider sp, string model = TutorModel) =>
        sp.GetRequiredService<LocalActionOverridesStore>()
            .Set(LocalActionCatalog.ChatPersona, model).Should().BeTrue();

    public static ChildSubject Make(ChildProfile profile, string key = Key, bool enabled = true) => new()
    {
        Id = key, Label = "Русский язык", Glyph = "Аа", Color = "info",
        Enabled = enabled,
    };

    /// <summary>POST создания чата с предметом (по умолчанию — «russian»).</summary>
    public static Task<HttpResponseMessage> CreateChat(HttpClient client, string? subject = Key, string space = "lessons") =>
        client.PostAsJsonAsync($"/api/child/spaces/{space}/chat", new { subject });
}
