// Показ панелей звука из старых мест (полоса «Звук», действия нитей, запуск из контекста). Одна точка: при
// флаге composer-context-row панели «Звук» нет — «настройки» это панель «Контекст», «голоса» — отдельная
// панель «Голоса»; без флага — прежняя панель «Звук» нужной вкладкой. Сторож features/** считает вызовы
// revealWorkspacePanel с ключами генерации по файлам, поэтому такой вызов в вертикали один.

import { FLAGS, getFlag, revealContextPanel, revealWorkspacePanel } from 'aihome_shell/kit';
import { SOUND_PANEL, VOICES_KEY } from '../thread/panelKey';

export function revealSoundPanel(sessionId: string | null | undefined, tab: 'settings' | 'voices', target?: string): boolean {
  if (getFlag(FLAGS.composerContextRow)) {
    if (tab === 'voices') { revealWorkspacePanel(VOICES_KEY); return true; }
    if (!sessionId) return false;
    return revealContextPanel(sessionId, target ? { target } : {});
  }
  // Прежний вид вызова: чат в детали — только вместе с элементом (takeVersion), остальные ходили без него
  return revealWorkspacePanel(SOUND_PANEL, tab, sessionId && target ? { sessionId, target } : {});
}
