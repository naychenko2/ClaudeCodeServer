using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.TestRuns;

// Подсказка «тесты — инструментом run_tests, а не Bash». CLI прячет MCP-инструменты за
// ToolSearch, и без подсказки модель по привычке зовёт `dotnet test` Bash'ем: тогда нет ни
// прогресса в чате, ни очереди сборок, ни остановки по «Стоп».
//
// Секция едет СИСТЕМНЫМ блоком (InTurnTail = false): текст постоянный и зависит только от
// свойства сессии (сервер tests доехал до хода), поэтому системный блок стабилен от хода к
// ходу и prefix cache не рвёт — промах один раз, на первом ходе после выкатки. Хвостом хода
// она платилась бы каждый ход. От наличия *.sln в дереве не зависим: файлы меняются, а
// текст секции меняться не должен.
public sealed class TestRunsHintContributor : IPromptSectionContributor
{
    public const string SectionKey = "mcp-tests";

    public const string Text =
        "`dotnet test`, `vitest` и `playwright test` запускай инструментом mcp__tests__run_tests (если его "
        + "схемы ещё нет — сначала ToolSearch с запросом «select:mcp__tests__run_tests»), а не Bash: он "
        + "показывает прогресс прогона в чате, встаёт в общую очередь сборок и останавливается по «Стоп». "
        + "Вид — аргументом kind (dotnet по умолчанию, vitest, playwright); всегда указывай target (проект или "
        + "решение dotnet, каталог фронта для vitest/playwright). dotnet сам собирает проект, считает тесты и "
        + "возвращает сводку с упавшими; no_build=true — если дерево уже собрано. Playwright стенд не "
        + "поднимает. Прогон дольше 9 минут обрывается — дроби его аргументами filter/files.";

    public string Key => SectionKey;
    public string Title => "Как запускать тесты";
    // Место в системном блоке задаёт ClaudeSession по ключу (рядом с прочими подсказками MCP);
    // Order — только порядок обхода шины, свободный шаг между code-graph (600) и редактором (700)
    public int Order => 650;
    public string Group => "mcp";

    public bool IsEnabled(PromptSessionContext sessionContext) => sessionContext.HasTestsMcp;

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText) =>
        Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, Text, Title)]));
}
