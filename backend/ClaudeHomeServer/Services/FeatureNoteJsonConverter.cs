using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Чат автора голоса фичи (FeatureNote.BySessionId) живёт в tasks.json, но наружу не отдаётся:
// инвариант модели (TaskItem.cs: «BySessionId — чат автора… наружу не отдаётся») и разрез
// «Сводка состояния фичи» п.1/п.6 (в brief.note его нет). Поле из модели не удалено: это единственный
// след автора голоса, из него в будущем заполняется byPersonaId (аддитивная правка модели, не проектора).
//
// Фильтр — не пер-эндпоинтный: TaskItem с голосом уходит наружу из нескольких точек со своими
// опциями — списки и карточка REST (MVC), ответы PUT/POST (TasksController.FeatureJsonOptions),
// task_changed (SignalR), ответы MCP (TasksToolset). Пер-эндпоинтные копии разъехались бы —
// ровно за это у Session была поставлен конвертер типа (см. SessionJsonConverter). Конвертер
// зарегистрирован во всех четырёх выходных опциях: FeatureNote мимо него на wire не уходит.
// Стор пишется своими опциями TaskManager без конвертера — поле остаётся в файле (круговой
// тест TaskStoreWriteTests зелёный).
//
// Механика — «сериализуй запись как раньше и убери чат автора»: сериализуем в JsonObject
// КЛОНОМ тех же опций без этого конвертера (иначе рекурсия), удаляем ключ по имени из политики
// имён вызывающих опций, пишем узел. Клон, а не самодельные опции: правила wire должны
// приходить из боевых опций точки отдачи, а не из второй копии, которая с ними разъедется.
internal sealed class FeatureNoteJsonConverter : JsonConverter<FeatureNote>
{
    // Клон опций кэшируется вместе с СВОИМ источником в одном объекте: конвертер живёт в
    // singleton-опциях и зовётся из разных потоков (тот же узор, что у SessionJsonConverter).
    private sealed record Inner(JsonSerializerOptions Source, JsonSerializerOptions Value);
    private Inner? _inner;

    public override FeatureNote? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<FeatureNote>(ref reader, OptionsWithoutSelf(options));

    public override void Write(Utf8JsonWriter writer, FeatureNote value, JsonSerializerOptions options)
    {
        var inner = OptionsWithoutSelf(options);
        var json = (JsonObject)JsonSerializer.SerializeToNode(value, inner)!;
        json.Remove(WireName(inner));
        json.WriteTo(writer);
    }

    // Имя ключа — из политики имён тех же опций, которыми сериализовали запись: все выходные
    // опции camelCase, но второй копии правила wire здесь нет и при другой политике.
    private static string WireName(JsonSerializerOptions options) =>
        options.PropertyNamingPolicy is { } policy
            ? policy.ConvertName(nameof(FeatureNote.BySessionId))
            : nameof(FeatureNote.BySessionId);

    private JsonSerializerOptions OptionsWithoutSelf(JsonSerializerOptions options)
    {
        if (_inner is { } cached && ReferenceEquals(cached.Source, options)) return cached.Value;

        var clone = new JsonSerializerOptions(options);
        for (var i = clone.Converters.Count - 1; i >= 0; i--)
            if (clone.Converters[i] is FeatureNoteJsonConverter) clone.Converters.RemoveAt(i);
        _inner = new Inner(options, clone);
        return clone;
    }
}
