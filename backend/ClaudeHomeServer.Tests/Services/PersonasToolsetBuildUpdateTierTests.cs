using System.Text.Json.Nodes;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Проброс тир-ячеек (ADR-007 §2) из аргументов <c>personas_update</c> в
/// <see cref="ClaudeHomeServer.Services.Mcp.Http.PersonasToolset"/>.
/// Семантика бэкенда: null — не менять, "" — сбросить к наследованию от специальности,
/// иначе — id модели или preset:{id}. Проглатывание "" в null делает сброс недоступным,
/// поэтому важен не только happy-path, но и пустая строка.
/// </summary>
public class PersonasToolsetBuildUpdateTierTests
{
    private static JsonObject Args(params (string Key, string Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in pairs) obj[k] = v;
        return obj;
    }

    [Fact]
    public void ВсеТриЯчейкиПрокидываютсяВUpdatePersonaRequest()
    {
        // Любой несущий аргумент помимо тир-ячеек нужен, чтобы BuildUpdateRequest не
        // вернул null (короткая ветка «только чтение текущей персоны» в обработчике
        // personas_update). Здесь это memoryEnabled — он ничего не правит и не мешает.
        var args = Args(
            ("id", "p1"),
            ("memoryEnabled", "true"),
            ("tierStrong", "claude-opus-4-8"),
            ("tierMedium", "preset:balanced"),
            ("tierWeak", ""));

        var req = ClaudeHomeServer.Services.Mcp.Http.PersonasToolset
            .BuildUpdateRequest(args, sessionProjectId: null);

        req.Should().NotBeNull();
        req!.TierStrong.Should().Be("claude-opus-4-8");
        req.TierMedium.Should().Be("preset:balanced");
        // Пустая строка обязана дойти как пустая строка — сброс к наследованию.
        req.TierWeak.Should().Be("");
    }

    [Fact]
    public void ОтсутствиеТирЯчеекДаётNull()
    {
        // Несущий аргумент помимо тир-ячеек — memoryEnabled. Без него метод вернул бы null
        // (короткая ветка «только чтение»).
        var args = Args(("id", "p1"), ("memoryEnabled", "true"));

        var req = ClaudeHomeServer.Services.Mcp.Http.PersonasToolset
            .BuildUpdateRequest(args, sessionProjectId: null);

        req.Should().NotBeNull();
        req!.TierStrong.Should().BeNull();
        req.TierMedium.Should().BeNull();
        req.TierWeak.Should().BeNull();
    }

    [Fact]
    public void ТолькоОднаТирЯчейкаПрокидываетсяБезКасанияОстальных()
    {
        // Частичная правка — одна ячейка задана, остальные null.
        var args = Args(("id", "p1"), ("memoryEnabled", "true"), ("tierMedium", "preset:economy"));

        var req = ClaudeHomeServer.Services.Mcp.Http.PersonasToolset
            .BuildUpdateRequest(args, sessionProjectId: null);

        req.Should().NotBeNull();
        req!.TierStrong.Should().BeNull();
        req.TierMedium.Should().Be("preset:economy");
        req.TierWeak.Should().BeNull();
    }

    [Fact]
    public void ПустаяСтрокаВТирЯчейкеНеПревращаетсяВNull()
    {
        // Граничный кейс, который OptionalArg проглотил бы как null —
        // именно поэтому в BuildUpdateRequest тиры идут через ContainsKey + StringArg.
        var args = Args(("id", "p1"), ("memoryEnabled", "true"), ("tierStrong", ""));

        var req = ClaudeHomeServer.Services.Mcp.Http.PersonasToolset
            .BuildUpdateRequest(args, sessionProjectId: null);

        req.Should().NotBeNull();
        req!.TierStrong.Should().Be("", "пустая строка обязана дойти до контроллера "
            + "как сброс к наследованию, а не как «не менять»");
        req.TierMedium.Should().BeNull();
        req.TierWeak.Should().BeNull();
    }
}
