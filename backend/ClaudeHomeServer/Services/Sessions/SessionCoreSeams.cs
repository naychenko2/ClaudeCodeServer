namespace ClaudeHomeServer.Services.Sessions;

// Швы ядра сессий (S3-0 §3, docs/research/session-manager-split-2026-11.md): именованные
// разрывы циклов между ролями, вынесенными из SessionManager. Пять допустимых аргументов
// Lazy<T> держит SessionCoreShapeGuardTests (правило 3); здесь — объявления швов.
// Реализация связывается в AddSessionCore как Lazy<T> от синглтона; до шага, где вынесен
// настоящий владелец, интерфейс реализует SessionManager (переходный режим).

// «Начать ход»: роль очереди при постановке сообщения может форсировать разбор очереди
// (переход 0→1 при свободном чате) — это рекурсия «очередь → приём хода», сейчас она идёт
// вызовом метода внутри SessionManager. Полностью шов достраивается в S3-4/S3-7
// (SendMessageAsync, SendOrEnqueueAsync, ContinueWorkLoopAsync, добивание сабагента и т. д.);
// здесь — только тот метод, чей перенос в S3-2 требует разрыва.
// public ТОЛЬКО потому, что Lazy<ITurnStarter> — параметр публичного конструктора PendingQueue
// (CS0051: менее доступный тип в параметре доступного конструктора); самой роли-очереди хватает
// одного метода, публичной поверхности у шва нет.
public interface ITurnStarter
{
    Task DrainNextPendingAsync(string sessionId);
}
