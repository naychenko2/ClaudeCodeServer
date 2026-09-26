namespace ClaudeHomeServer.Services.ImageEditor;

// Шов между контроллером Main и исполнителем задач редактора (ImageEditJobService в
// вертикали Images, ADR-017, раздел 7). Контроллер получает его nullable: подсистема
// выключена или исполнитель ещё не подключён — ручки отвечают 503, а не 500.
//
// Изоляция владельцев — на стороне реализации: КАЖДЫЙ метод сверяет ownerId и projectId
// задачи и котировки. Чужая задача неотличима от несуществующей (null / JobNotFound).
public interface IImageEditJobs
{
    Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
        string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct);

    // Запуск ровно по котировке: другой пары «поставщик + модель» здесь не бывает
    Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
        string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct);

    ImageEditJobDto? Get(string ownerId, string projectId, string jobId);

    // null — задачи нет или она чужая
    Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct);

    // Байты готового варианта из рабочей папки сеанса; null — нет такого или чужой
    EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant);
}

// Шов записи результата в проект новым файлом (FileMode.CreateNew, hero.v2.png).
// Реализация — поверх версионного сохранения; до её подключения ручка save отвечает 503.
public interface IImageEditSaver
{
    ImageEditCallResult<ImageEditSaveResultDto> Save(
        string projectRoot, ImageEditSaveRequest request, EditedImage image);

    // «Сохранить как…» (ADR-018 §5): ровно то имя, что выбрал человек, расширение — по формату
    // байтов. Только FileMode.CreateNew: занятое имя — NameTaken, перезаписи нет никогда
    ImageEditCallResult<ImageEditSaveResultDto> SaveAs(
        string projectRoot, string? folder, string? fileName, EditedImage image);

    // Проверка имени на лету: ничего не пишет. extension — «.png», «.jpg», «.webp», «.gif»
    ImageEditCallResult<SaveCheckResponse> Check(
        string projectRoot, string? folder, string? fileName, string extension);
}
