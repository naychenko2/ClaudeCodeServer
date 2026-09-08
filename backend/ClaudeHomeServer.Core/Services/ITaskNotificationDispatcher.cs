using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Узкий шов NotificationService для выноса Tasks (Этап 5, волна 1).
//
// Заводим ОТДЕЛЬНЫЙ шов, а не расширяем существующий IKnowledgeNotificationDispatcher —
// контракты разные по существу:
//   • Tasks работает с готовым NotificationMessage (тип из Core/Protocol), Knowledge
//     шлёт плоские параметры title/body/url/tag/source. NotificationMessage несёт
//     денормализованные атрибуты персоны/проекта (PersonaName, ProjectName, …), которые
//     Tasks получает уже готовыми от отправителя, а Knowledge не использует.
//   • Tasks-уведомления всегда с web push (напоминания и события исполнителя — важные
//     события, фронт ждёт их офлайн). Knowledge шлёт тихие алерты (sendPush:false) —
//     дедуп по дате через LastNotifiedAtAsync, чтобы не сыпать каждый час.
//   • Tasks не нуждается в LastNotifiedAtAsync (нет дедупа), Knowledge не нуждается
//     в SendExecutionEventAsync (нет sessionId и нет push).
//
// Склейка двух разных контрактов в один «грабмешок» обманула бы и читателя шва,
// и будущего автора ревью (Tasks пишет `sendPush:true`, Knowledge — `false`; один
// шов с одним флагом на оба не сходится без condition).
//
// Покрывает три места в вертикали Tasks (через DailyBriefingService, чья
// регистрация принадлежит TasksSubsystem):
//   • TaskSchedulerService.SendNotificationMessageAsync — напоминание о сроке;
//   • TaskManager.SendExecutionEventAsync — спавн следующего экземпляра регулярной задачи;
//   • DailyBriefingService.SendNotificationMessageAsync — тост «доброе утро».
public interface ITaskNotificationDispatcher
{
    // Отправить уже сформированное NotificationMessage. sendPush — отправить также web push
    // (напоминания и бриф — да, по умолчанию false для совместимости с on-demand путями).
    Task SendNotificationMessageAsync(string userId, NotificationMessage msg, bool sendPush = false);

    // Событие исполнителя (TaskExecutionService спавнит, инициирует, завершает ход): тост +
    // web push по умолчанию. Плоские параметры повторяют сигнатуру NotificationService.
    Task SendExecutionEventAsync(string userId, string sessionId,
        string title, string body, string? projectId, string? taskId,
        string type, string tag, string url);
}