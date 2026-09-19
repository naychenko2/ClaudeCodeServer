using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Wire-проекция Session для точек отдачи списка чатов (проект/вне-проекта/персона/стена,
// GET projects/{id}/sessions, chats, home summary). Вычисленные «связи» сессии
// (parentSessionId, taskDone) убраны из модели Session (Core, см. Session.cs) и подставляются
// здесь, на стороне сервера, через SessionTaskLinks поверх ITaskLookup.
//
// Механика — «сериализуй Session как раньше и добавь два поля»: сериализуем Session в JsonObject
// (те же camelCase-поля, что уходили на фронт), дописываем parentSessionId/taskDone, отдаём
// JsonObject (ASP.NET пишет JsonNode как есть). Все прочие поля (name, status, taskId, origin,
// isArchived, …) уходят в wire без изменений — фронт их не теряет, расхождение с моделью
// ловилось бы типом, а не ручной копировкой.
internal static class SessionWire
{
    // Те же правила, что ASP.NET MVC отдаёт wire (camelCase-имена + enum строками, camelCase):
    // иначе ClaudeMode/названия полей разъедутся с фронт-типом Session.
    private static readonly JsonSerializerOptions EnumOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static JsonObject ToWire(Session s, ITaskLookup? tasks)
    {
        var json = (JsonObject)JsonSerializer.SerializeToNode(s, EnumOptions)!;
        json["parentSessionId"] = SessionTaskLinks.ParentSessionId(s, tasks);
        json["taskDone"] = SessionTaskLinks.IsTaskDone(s, tasks);
        return json;
    }
}
