// Показ панелей картинок из старых мест (полоса «Картинки», действия нитей). Одна точка: при флаге
// composer-context-row панели «Картинки» нет — «настройки» это панель «Контекст», «персонажи» — отдельная
// панель «Персонажи»; без флага — прежняя панель «Картинки» нужной вкладкой. Сторож features/** считает
// вызовы revealWorkspacePanel с ключами генерации по файлам, поэтому такой вызов в вертикали один.

import { FLAGS, getFlag, revealContextPanel, revealWorkspacePanel } from 'aihome_shell/kit';
import { IMAGES_PANEL } from '../characters/panel';

export function revealImagesPanel(sessionId: string | null | undefined, tab: 'settings' | 'characters', target?: string): boolean {
  if (getFlag(FLAGS.composerContextRow)) {
    if (tab === 'characters') { revealWorkspacePanel('characters'); return true; }
    if (!sessionId) return false;
    return revealContextPanel(sessionId, target ? { target } : {});
  }
  return revealWorkspacePanel(IMAGES_PANEL, tab, sessionId ? { sessionId, ...(target ? { target } : {}) } : {});
}
