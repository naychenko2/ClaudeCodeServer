using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Быстрая фраза композера: готовое сообщение и необязательная группа (второй уровень
/// попапа). Group == null — фраза лежит в корне списка; иначе прячется под именем группы.
/// </summary>
[JsonConverter(typeof(QuickPhraseJsonConverter))]
public record QuickPhrase(string Text, string? Group = null);

/// <summary>
/// Читает и старый формат хранения (голая строка — фраза без группы), и новый объект.
/// Конвертер на самом типе, а не в опциях: users.json пишется своими JsonSerializerOptions
/// в UserStore, а бэкапы и API-ответы — чужими, и разъехаться они не должны.
/// </summary>
public class QuickPhraseJsonConverter : JsonConverter<QuickPhrase>
{
    public override QuickPhrase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Наборы, заведённые до появления групп, лежат списком строк
        if (reader.TokenType == JsonTokenType.String)
            return new QuickPhrase(reader.GetString() ?? "", null);

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Быстрая фраза — строка или объект { text, group }");

        string text = "";
        string? group = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "text", StringComparison.OrdinalIgnoreCase))
                text = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
            else if (string.Equals(name, "group", StringComparison.OrdinalIgnoreCase))
                group = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            else
                reader.Skip();
        }
        return new QuickPhrase(text, group);
    }

    public override void Write(Utf8JsonWriter writer, QuickPhrase value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("text", value.Text);
        // Пустую группу не пишем вовсе: в хранилище «фраза без группы» — это отсутствие
        // поля, а не строка "null", и старые записи после первой же перезаписи выглядят так же
        if (!string.IsNullOrEmpty(value.Group)) writer.WriteString("group", value.Group);
        writer.WriteEndObject();
    }
}
