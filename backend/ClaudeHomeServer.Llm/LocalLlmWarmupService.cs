namespace ClaudeHomeServer.Services.Llm;

// Фоновый прогрев активной локальной модели при старте сервера: лучше загрузить
// веса в память сразу, чем платить холодным стартом первого хода (30+ секунд
// на qwen-стиле моделях). Раньше прогрев жил в Program.cs строкой
// `_ = Task.Run(() => app.Services.GetRequiredService<ILocalLlmClient>().WarmUpAsync())`,
// и у него было две проблемы:
//   1) Срабатывал на каждом старте безусловно, даже когда ни одно фоновое место
//      не маршрутизировано на локаль — на инстансе без локальной модели прогрев
//      был пустой работой (Enabled проверка внутри клиента, но всё равно лишний ход).
//   2) Грел `Model` (читается как `Ollama:Model`), а текстовые вызовы идут на
//      `TextModel` (`Ollama:TextModel`, при отсутствии откат на Model). На стенде
//      оба значения совпадают — разъедутся, и в память уйдёт не та модель, на
//      которую потом пойдут ходы.
//
// Условие запуска: локаль включена И хотя бы одно место каталога реально
// маршрутизировано на локаль. Это не задевает конфигурацию «один чат-голос» —
// голосовой ход обходит CheapTextRunner (свой путь), но и для него прогрев
// TextModel не лишний: горячая память экономит время первого реплики разговора.
//
// Греем `TextModel` — на нём работают текстовые места (NotesTags/ChatTitle/...),
// и он же значение по умолчанию для всего остального. Реализации `ILocalLlmClient`
// принимают параметр `model` и греют именно его, отдельного цикла нет.
public sealed class LocalLlmWarmupService(
    ILocalLlmClient client,
    LocalActionRouter router,
    ILogger<LocalLlmWarmupService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldRun())
        {
            log.LogDebug("Прогрев локальной модели пропущен: ни одно фоновое место не маршрутизировано на локаль");
            return Task.CompletedTask;
        }

        // В копии (--inspect) пропускаем: см. LlmSubsystem.Register — прогрев там же
        // не делается, чтобы не поднимать веса на инспекционном копировании.
        // Условие «локаль включена И есть маршрут» уже отсекает пустые случаи.
        return Task.Run(async () =>
        {
            try
            {
                // Греем `TextModel` — на нём идут текстовые места (NotesTags/ChatTitle/...),
                // голосовой ход тоже использует его как основную модель. Раньше
                // прогрев грел `Model` и при разъезде значений в память уходила
                // не та модель.
                await client.WarmUpAsync(client.TextModel, cancellationToken);
            }
            catch (Exception ex)
            {
                // best-effort: ошибка прогрева не должна мешать старту сервера
                log.LogDebug(ex, "Прогрев локальной модели не удался (не критично)");
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool ShouldRun()
    {
        if (!client.Enabled) return false;
        foreach (var action in LocalActionCatalog.All)
        {
            if (router.UsesLocal(action.Key)) return true;
        }
        return false;
    }
}