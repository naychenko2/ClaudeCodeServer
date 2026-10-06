// Показ панели «Контекст» из мест звука (действия нитей, запуск из контекста). Одна точка: «настройки» звука —
// это панель «Контекст»; панели «Звук» больше нет, а «Голоса» открываются отдельно своим ключом.

import { revealContextPanel } from 'aihome_shell/kit';

export function revealSoundPanel(sessionId: string | null | undefined, target?: string): boolean {
  if (!sessionId) return false;
  return revealContextPanel(sessionId, target ? { target } : {});
}
