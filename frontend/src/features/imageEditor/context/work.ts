// Наполнение контекста действиями человека (ADR-023, 2к-2): «Работать с этой» ставит картинку основным
// объектом и показывает панель «Контекст»; клик по карточке только переключает уже открытую панель.
// Запись идёт в стор контекста чата (PUT primary), а не в фокус нитей: сервер зеркалит её сам.

import { followSelection, revealContextPanel, setPrimary } from 'aihome_shell/kit';
import { IMAGES_PANEL } from '../characters/panel';
import { imageDraftKey } from '../thread/threadStore';
import { IMAGE_KIND } from './state';

// Ссылка основного объекта: версия нити, а у нити без версий — сама нить
export const imageRefOf = (threadId: string, versionId: string | null) =>
  ({ kind: IMAGE_KIND, ref: versionId ? { threadId, versionId } : { threadId } });

// «Работать с этой»: просьба открыть — панель показывается и закрытая. На телефоне шторка панели закрыла бы
// поле ввода, а чипы действий появляются над ним сами (макет, сценарий 9): там reveal = false, панель — по чипу строки
export async function workWithInContext(sessionId: string, threadId: string, versionId: string | null, reveal = true): Promise<boolean> {
  const target = imageDraftKey(threadId);
  if (await setPrimary(sessionId, imageRefOf(threadId, versionId)) === 'failed') return false;
  if (reveal) revealContextPanel(sessionId, { target });
  return true;
}

// Клик человека по карточке: картинка становится основной, открытая панель переключается на «Контекст»,
// закрытая не открывается. working — карточка уже основной объект: запись не нужна
export async function pickInContext(sessionId: string, threadId: string, versionId: string | null, working: boolean): Promise<void> {
  if (!working && await setPrimary(sessionId, imageRefOf(threadId, versionId)) === 'failed') return;
  followSelection(IMAGES_PANEL, sessionId, imageDraftKey(threadId));
}
