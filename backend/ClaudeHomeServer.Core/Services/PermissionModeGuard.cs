using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Гард «координатор не пишет код» в части режима прав: в acceptEdits, bypassPermissions И
// dontAsk (Minor, волна 3 — открытый вопрос предыдущего аудита: имя режима у CLI означает
// ровно «не спрашивать разрешение», тот же класс, что acceptEdits/bypass) CLI разрешение
// не спрашивает, и запись файла через shell (heredoc, tee, sed -i) проходит мимо
// CoordinatorWriteGuard. Такие режимы поднимаем до Auto — остальные оставляем как есть
// (в т.ч. Plan: он спрашивает всегда).
//
// Чистая функция от (ClaudeMode, bool): зовётся в точках смены режима и при приёме хода
// штаба, см. docs/research/session-core-split-2026-09.md §4 (рёбра №9–10). В спине — рядом
// с типом режима, чтобы вертикаль штаба и ядро SessionManager зависели от Core, а не от
// частной подсистемы.
public static class PermissionModeGuard
{
    public static ClaudeMode GuardCompatibleMode(ClaudeMode mode, bool coordinatorNoCode) =>
        coordinatorNoCode && mode is ClaudeMode.AcceptEdits or ClaudeMode.Bypass or ClaudeMode.DontAsk
            ? ClaudeMode.Auto : mode;
}
