using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services;

// Вычисленные «связи» сессии (parentSessionId, taskDone) убраны из модели Session (Core,
// см. Session.cs) и дописываются здесь — на ГРАНИЦЕ СЕРИАЛИЗАЦИИ, один раз на весь MVC.
//
// Почему конвертер, а не проекция в контроллерах: Session уходит на фронт из ~26 точек
// (6 списочных + одиночные — переименование, архив, персона, участники, настройки,
// window-1m/drop, …). Пер-эндпоинтная проекция держалась дисциплиной вызова, и половина
// точек её не звала: фронт (ChatPanel.onSessionUpdated) подменяет объект чата ответом
// одиночного эндпоинта, и чат без parentSessionId выпадал из дерева после любой правки
// настроек. Конвертер типа снимает вопрос по построению: мимо него Session на wire не
// уходит — ни из Ok(session), ни из списка, ни из анонимного объекта с полем Session.
//
// Механика — «сериализуй Session как раньше и добавь два поля»: сериализуем в JsonObject
// КЛОНОМ тех же опций без этого конвертера (иначе рекурсия), дописываем поля, пишем узел.
// Клон, а не самодельные опции: правила wire (camelCase + enum строками) должны приходить
// из боевых MVC-опций, а не из второй копии, которая с ними разъедется.
//
// Регистрируется ТОЛЬКО в MVC-опциях (SessionJsonOptionsSetup). Стор sessions.json
// сериализуется своими опциями (SessionManager._jsonOpts), поэтому вычисленные поля в
// файл не попадают и чтение работает как прежде.
internal sealed class SessionJsonConverter(IServiceProvider services) : JsonConverter<Session>
{
    // Клон опций кэшируется вместе со СВОИМ источником в одном объекте: конвертер живёт в
    // singleton-опциях и зовётся из разных потоков, а два независимых поля (источник и клон)
    // при гонке могли бы склеиться в несовпадающую пару.
    private sealed record Inner(JsonSerializerOptions Source, JsonSerializerOptions Value);
    private Inner? _inner;

    // ITaskLookup резолвится лениво: опции MVC собираются при первом запросе, но конвертер
    // создаётся раньше — держать здесь готовый ITaskLookup значило бы тянуть TaskManager
    // в момент конфигурации опций. GetService, а не GetRequiredService: отсутствующий
    // lookup — штатный null-случай SessionTaskLinks (связи не резолвятся), а не 500.
    private ITaskLookup? Tasks => services.GetService(typeof(ITaskLookup)) as ITaskLookup;

    public override Session? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<Session>(ref reader, OptionsWithoutSelf(options));

    public override void Write(Utf8JsonWriter writer, Session value, JsonSerializerOptions options)
    {
        var tasks = Tasks;
        var json = (JsonObject)JsonSerializer.SerializeToNode(value, OptionsWithoutSelf(options))!;
        json["parentSessionId"] = SessionTaskLinks.ParentSessionId(value, tasks);
        json["taskDone"] = SessionTaskLinks.IsTaskDone(value, tasks);
        json.WriteTo(writer);
    }

    private JsonSerializerOptions OptionsWithoutSelf(JsonSerializerOptions options)
    {
        if (_inner is { } cached && ReferenceEquals(cached.Source, options)) return cached.Value;

        var clone = new JsonSerializerOptions(options);
        for (var i = clone.Converters.Count - 1; i >= 0; i--)
            if (clone.Converters[i] is SessionJsonConverter) clone.Converters.RemoveAt(i);
        _inner = new Inner(options, clone);
        return clone;
    }
}

// Регистрация конвертера в опциях MVC. IConfigureOptions, а не AddJsonOptions: конвертеру
// нужен IServiceProvider (ленивый ITaskLookup), а лямбда AddJsonOptions доступа к нему не имеет.
internal sealed class SessionJsonOptionsSetup(IServiceProvider services) : IConfigureOptions<JsonOptions>
{
    public void Configure(JsonOptions options) =>
        options.JsonSerializerOptions.Converters.Add(new SessionJsonConverter(services));
}
