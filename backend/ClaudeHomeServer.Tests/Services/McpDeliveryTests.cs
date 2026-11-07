using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
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
/// Матрица ниже — поведение широкой рамки (личный проект, чат вне проекта): с приходом
/// рамки (S2a-7) она осталась дословно той же, ей передаётся TurnFrameKind.Wide.
/// Узкая рамка (общий проект) — отдельная матрица ниже.
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

        McpDelivery.ShouldDeliver(record, projectServersOn, isProjectChat, personaGranted,
                readOnly, TurnFrameKind.Wide)
            .Should().Be(expected);
    }

    // Grant проекта снимает пустым списком «никто не включён» — как и отсутствие списка
    [Fact]
    public void ShouldDeliver_ПустойСписокПроекта_КакОтсутствие()
    {
        var record = new McpServerRecord { Key = "srv", Enabled = true };
        // пустой McpServersOn = «в проекте не включён никто» → проектный grant не срабатывает
        McpDelivery.ShouldDeliver(record, [], isProjectChat: true, personaGranted: false,
                readOnly: false, TurnFrameKind.Wide)
            .Should().BeFalse();
        // но выдача персоны спасает
        McpDelivery.ShouldDeliver(record, [], isProjectChat: true, personaGranted: true,
                readOnly: false, TurnFrameKind.Wide)
            .Should().BeTrue();
    }

    // --- Узкая рамка (S2a-7, решение 29.10): любой чат общего проекта, любой актор ---
    //
    // Личные оси («включён в проекте», «выдан персоне») — свойства личного пространства
    // арендатора, от общего проекта оно закрыто всем, включая самого арендатора. Поэтому в
    // Narrow они не смотрятся вовсе: единственный путь записи реестра в ход — явная пометка
    // SharedWithMembers. RO-гейт пометка не снимает: она про участников, а не про профиль.
    // Параметры: enabled, shared, allowReadOnly, inProjectOn, personaGranted, readOnly, expected
    [Theory]
    // Не помеченный — не едет ни при каких выдачах (это и есть мутационный ряд: без
    // гейта рамки первая строка стала бы true)
    [InlineData(true, false, false, true,  true,  false, false)] // grant проекта, без пометки → мимо
    [InlineData(true, false, false, false, true,  false, false)] // grant персоны, без пометки → мимо
    [InlineData(true, false, false, false, false, false, false)] // без пометки и без выдач → мимо
    // Помеченный — едет сам, без каких либо выдач: пометка и есть разрешение
    [InlineData(true, true,  false, false, false, false, true)]
    // Пометка не расширяет рубильник: выключенная запись не едет и помеченная
    [InlineData(false, true, false, true,  true,  false, false)]
    // RO-гейт поверх пометки не поглощается
    [InlineData(true, true,  false, false, false, true,  false)] // RO без явного разрешения → режет
    [InlineData(true, true,  true,  false, false, true,  true)]  // RO с явным разрешением → едет
    public void ShouldDeliver_NarrowТолькоПомеченные(bool enabled, bool shared, bool allowReadOnly,
        bool inProjectOn, bool personaGranted, bool readOnly, bool expected)
    {
        var record = new McpServerRecord
        {
            Key = "srv",
            Enabled = enabled,
            SharedWithMembers = shared,
            AllowReadOnlyPersonas = allowReadOnly,
        };
        // проектный grant общий проект тоже мог бы иметь — он не решает ничего (строки
        // с inProjectOn/personaGranted=true стоят ровно для этого)
        IReadOnlyCollection<string>? projectServersOn = inProjectOn ? ["srv"] : null;

        McpDelivery.ShouldDeliver(record, projectServersOn, isProjectChat: true, personaGranted,
                readOnly, TurnFrameKind.Narrow)
            .Should().Be(expected);
    }

    // Wide-поведение не изменилось ни для одной оси (тот же состав, что матрица выше,
    // но в узкой рамке эти же комбинации дали бы другой ответ) — контраст держится явным тестом:
    // выдать сервер в личный проект или персоне по-прежнему достаточно без пометки
    [Fact]
    public void ShouldDeliver_WideПометкаНеНужна()
    {
        var record = new McpServerRecord { Key = "srv", Enabled = true }; // SharedWithMembers=false
        McpDelivery.ShouldDeliver(record, ["srv"], isProjectChat: true, personaGranted: false,
                readOnly: false, TurnFrameKind.Wide)
            .Should().BeTrue("в личном проекте пометка не требуется: прежнее поведение сохранено");
        McpDelivery.ShouldDeliver(record, null, isProjectChat: false, personaGranted: false,
                readOnly: false, TurnFrameKind.Wide)
            .Should().BeFalse("вне проектов — прежняя ось AllowOutsideProjects, не пометка");
    }

    // --- IsBuiltinDelivered: продуктовая встроенная интеграция (Higgsfield) ---
    //
    // Каскад проекта/персоны снят: решение зависит ТОЛЬКО от рубильника записи и
    // RO-гейта. Ни McpServersOn, ни personaGranted в формуле не участвуют — иначе
    // владелец, вошедший в Higgsfield, терял бы сервер на каждом чате, не включённом
    // вручную в проекте/персоне.
    [Theory]
    [InlineData(false, false, false, false)]  // выключен — не едет
    [InlineData(true,  true,  false, false)]  // RO без явного разрешения — не едет
    [InlineData(true,  false, false, true)]   // обычная персона — едет
    [InlineData(true,  true,  true,  true)]   // RO с явным разрешением — едет
    public void IsBuiltinDelivered_Матрица(bool enabled, bool readOnly, bool allowReadOnly, bool expected)
    {
        var record = new McpServerRecord
        {
            Key = "higgsfield",
            Enabled = enabled,
            AllowReadOnlyPersonas = allowReadOnly,
        };

        McpDelivery.IsBuiltinDelivered(record, readOnly).Should().Be(expected);
    }

    // Допущение для встроенной записи: ключ и транспорт не смотрят на ход и проект,
    // поэтому RO-гейт — единственный фильтр поверх рубильника. AllowOutsideProjects
    // НЕ участвует (у записи он может быть выставлен реестром, но для встроенной
    // интеграции он не имеет смысла).
    [Fact]
    public void IsBuiltinDelivered_AllowOutsideProjectsИгнорируется()
    {
        var record = new McpServerRecord
        {
            Key = "higgsfield",
            Enabled = true,
            AllowOutsideProjects = false, // явно выключен
        };

        McpDelivery.IsBuiltinDelivered(record, readOnly: false).Should().BeTrue(
            "IsBuiltinDelivered — продуктовая интеграция: AllowOutsideProjects из каскада реестра не применяется");
    }
}
