using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.TestRuns;

// Подсказка «сборка — инструментом build, а не Bash» (сервер dev). Причина та же, что у
// TestRunsHintContributor: CLI прячет MCP-инструменты за ToolSearch, и без подсказки модель по
// привычке собирает Bash'ем — без прогресса в чате, без очереди сборок и без «Стоп».
//
// Секция едет СИСТЕМНЫМ блоком: текст постоянный и зависит только от свойства сессии (сервер dev
// доехал до хода), поэтому prefix cache она не рвёт.
public sealed class BuildHintContributor : IPromptSectionContributor
{
    public const string SectionKey = "mcp-dev";

    public const string Text =
        "`dotnet build` и `npm run build` (и другие скрипты сборки из package.json) запускай инструментом "
        + "mcp__dev__build (если его схемы ещё нет — сначала ToolSearch с запросом «select:mcp__dev__build»), "
        + "а не Bash: он показывает прогресс сборки в чате, встаёт в общую очередь сборок и останавливается по "
        + "«Стоп». Вид — аргументом kind (dotnet по умолчанию, npm); всегда указывай target (проект или решение "
        + "dotnet, каталог с package.json для npm), имя скрипта npm — аргументом script. Сборка дольше 9 минут "
        + "обрывается — повтори вызов, следующая сборка будет инкрементальной. Дев-стенд поднимай инструментом "
        + "mcp__dev__start_stand (service — id сервиса из панели «Сервисы» или путь к .csproj), а не "
        + "`dotnet run &`/`npm run dev &` в Bash: фоновый процесс Bash умрёт вместе с CLI, а стенд инструмента "
        + "живёт после хода и виден в панели «Сервисы»; гасится mcp__dev__stop_stand. e2e против стенда — "
        + "его URL передай в env.PLAYWRIGHT_BASE_URL у run_tests.";

    public string Key => SectionKey;
    public string Title => "Как собирать проект";
    // Место в системном блоке задаёт ClaudeSession по ключу (сразу за подсказкой тестов);
    // Order — только порядок обхода шины, свободный шаг за тестами (650)
    public int Order => 660;
    public string Group => "mcp";

    public bool IsEnabled(PromptSessionContext sessionContext) => sessionContext.HasDevMcp;

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText) =>
        Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, Text, Title)]));
}
