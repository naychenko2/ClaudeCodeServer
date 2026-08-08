using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// OR-матрица правила доставки сервера личного реестра в allow-модели (флаг mcp-allowlist).
/// Чистая функция <see cref="McpDelivery.ShouldDeliver"/> — без SessionManager и DI, чтобы
/// гонять все значимые комбинации «проектный/внепроектный чат × grant проекта × grant
/// персоны × профиль только-чтение». Правило — docs/research/mcp-allowlist-plan.md,
/// блок-схема «Правило доставки».
/// </summary>
public class McpDeliveryTests
{
    // Параметры: enabled, allowOutside, allowReadOnly, isProjectChat, inProjectOn,
    // personaGranted, readOnly, expected
    [Theory]
    // Ось 0 — выключенная запись не едет ни при каких выдачах
    [InlineData(false, false, false, true,  true,  false, false, false)]
    // Проектный чат, обычная персона
    [InlineData(true,  false, false, true,  false, false, false, false)] // нет выдач → мимо
    [InlineData(true,  false, false, true,  true,  false, false, true)]  // grant проекта → едет
    [InlineData(true,  false, false, true,  false, true,  false, true)]  // grant персоны → едет
    [InlineData(true,  false, false, true,  true,  true,  false, true)]  // оба → едет
    // Внепроектный чат, обычная персона
    [InlineData(true,  false, false, false, false, false, false, false)] // allowOutside=false, без персоны → мимо
    [InlineData(true,  true,  false, false, false, false, false, true)]  // allowOutside=true → едет
    [InlineData(true,  false, false, false, false, true,  false, true)]  // grant персоны → едет
    // Профиль «Только чтение» — AND-гейт поверх allow-list не поглощается выдачей
    [InlineData(true,  false, false, true,  true,  false, true,  false)] // grant проекта, allowReadOnly=false → режет
    [InlineData(true,  false, true,  true,  true,  false, true,  true)]  // grant проекта, allowReadOnly=true → едет
    [InlineData(true,  false, false, false, false, true,  true,  false)] // grant персоны, allowReadOnly=false → режет
    [InlineData(true,  false, true,  false, false, true,  true,  true)]  // grant персоны, allowReadOnly=true → едет
    // RO без выдачи — гейт не расширяет права (false по allow, до гейта не доходит)
    [InlineData(true,  false, false, true,  false, false, true,  false)]
    public void ShouldDeliver_OrМатрица(bool enabled, bool allowOutside, bool allowReadOnly,
        bool isProjectChat, bool inProjectOn, bool personaGranted, bool readOnly, bool expected)
    {
        var record = new McpServerRecord
        {
            Key = "srv",
            Enabled = enabled,
            AllowOutsideProjects = allowOutside,
            AllowReadOnlyPersonas = allowReadOnly,
        };
        IReadOnlyCollection<string>? projectServersOn =
            isProjectChat && inProjectOn ? ["srv"] : null;

        McpDelivery.ShouldDeliver(record, projectServersOn, isProjectChat, personaGranted, readOnly)
            .Should().Be(expected);
    }

    // Grant проекта снимает пустым списком «никто не включён» — как и отсутствие списка
    [Fact]
    public void ShouldDeliver_ПустойСписокПроекта_КакОтсутствие()
    {
        var record = new McpServerRecord { Key = "srv", Enabled = true };
        // пустой McpServersOn = «в проекте не включён никто» → проектный grant не срабатывает
        McpDelivery.ShouldDeliver(record, [], isProjectChat: true, personaGranted: false, readOnly: false)
            .Should().BeFalse();
        // но выдача персоны спасает
        McpDelivery.ShouldDeliver(record, [], isProjectChat: true, personaGranted: true, readOnly: false)
            .Should().BeTrue();
    }
}
