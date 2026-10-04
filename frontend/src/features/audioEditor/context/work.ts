// Наполнение контекста действиями человека (ADR-023, 2з-3): «Работать с этой» ставит звук основным объектом
// и показывает панель «Контекст»; клик по карточке только переключает уже открытую панель. Запись идёт в стор
// контекста чата (PUT primary), а не в фокус нитей: сервер зеркалит её сам.

import { followSelection, revealContextPanel, setPrimary } from 'aihome_shell/kit';
import { soundDraftKey } from '../thread/threadStore';
import { AUDIO_KIND } from './state';

// Ссылка основного объекта: версия нити, а у нити без версий — сама нить
export const audioRefOf = (threadId: string, versionId: string | null) =>
  ({ kind: AUDIO_KIND, ref: versionId ? { threadId, versionId } : { threadId } });

// «Работать с этой»: просьба открыть — панель показывается и закрытая. На телефоне шторка закрыла бы поле
// ввода, а чипы действий появляются над ним сами: там reveal = false
export async function workWithInContext(sessionId: string, threadId: string, versionId: string | null, reveal = true): Promise<boolean> {
  const target = soundDraftKey(threadId);
  if (await setPrimary(sessionId, audioRefOf(threadId, versionId)) === 'failed') return false;
  if (reveal) revealContextPanel(sessionId, { target });
  return true;
}

// Клик человека по карточке: звук становится основным, открытая панель переключается на «Контекст», закрытая
// не открывается. working — карточка уже основной объект: запись не нужна
export async function pickInContext(sessionId: string, threadId: string, versionId: string | null, working: boolean): Promise<void> {
  if (!working && await setPrimary(sessionId, audioRefOf(threadId, versionId)) === 'failed') return;
  followSelection('chatContext', sessionId, soundDraftKey(threadId));
}
