using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Files;

/// <summary>
/// Ответ «содержимое файла» панели файлов: текст, картинка base64, документ для
/// клиентского рендера или только метаданные бинарника. Одна форма на сервер и агент
/// устройства (ADR-016, задача 4.2) — поля, которых у вида нет, в JSON не пишутся, как у
/// прежних анонимных объектов контроллера. Контракт в спине: его отдаёт шов
/// <c>IProjectFiles</c>; собирает его <c>FileContentReader</c> вертикали Files.
/// </summary>
public sealed record FileContentView(
    string? Content,
    bool IsBinary,
    bool IsImage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsDocument = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsVideo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsAudio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MimeType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? FileSize = null,
    // W4-LINK-B (шаг B2a): дерево, из которого прочитан файл. Заполняется только на пути
    // поиска по деревьям проекта (?sessionId=): branch — ветка найденного дерева (null для
    // main), isAnchor — прочитано ли из заявленного дерева чата. Чип «из дерева <ветка>»
    // строят именно из этих полей; вне поиска — null/false, чипа нет.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Branch = null,
    bool IsAnchor = false);
