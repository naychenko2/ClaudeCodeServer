namespace ClaudeHomeServer.Services.Media;

// Шов «усыновление результата прямого local_*» (карточка в ленте как у запуска кнопкой). Агент иногда
// зовёт local-media напрямую, мимо audio_* / image_*: результат тогда лежит файлом в проекте, а ленте
// нечего нарисовать, кроме голого вызова инструмента. Когда задача собрана, local-media отдаёт её файлы
// усыновителям: модуль редактора заводит нить по файлу и кладёт якорь в ленту — та же богатая карточка.
//
// Живёт в Core: издатель (Images) и подписчики (AudioEditor, ImageEditor) — разные вертикали, прямой
// ссылки между ними быть не должно. Сбой усыновителя задачу не роняет — local-media его гасит и пишет в лог.
public interface ILocalMediaAdopter
{
    Task AdoptAsync(LocalMediaAdoption adoption, CancellationToken ct);
}

// Что собрала задача: чат-вызыватель, проект и файлы результата (путь — от корня проекта через «/»).
// Op — имя операции local-media (music_edit, speech, generate_image …), JobId — id задачи local-media
// (по нему усыновитель узнаёт собственный след и не усыновляет задачу дважды)
public sealed record LocalMediaAdoption(
    string OwnerId,
    string ProjectId,
    string SessionId,
    string Op,
    string JobId,
    IReadOnlyList<LocalMediaAdoptedFile> Files);

public sealed record LocalMediaAdoptedFile(string Path, string ContentType);
