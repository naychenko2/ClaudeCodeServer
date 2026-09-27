// Ключ панели «Персонажи» в рабочей области и её показ извне (из полосы «Картинки»)

import { REVEAL_PANEL_EVENT } from 'aihome_shell/kit';

export const CHARACTERS_PANEL = 'characters';

export function revealWorkspacePanel(key: string) {
  window.dispatchEvent(new CustomEvent(REVEAL_PANEL_EVENT, { detail: { key } }));
}
