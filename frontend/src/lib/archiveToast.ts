// Тост «Чат убран в архив» с кнопкой «Отменить» — общая точка для обоих мест,
// где активный чат уезжает в архив: ChatsPage (глобальный список) и
// WorkspacePage (проект). Блок был скопирован дословно в оба файла, а тексты
// уведомления и подпись действия обязаны совпадать: человек видит одно и то же
// сообщение, из какого бы списка ни архивировал.
//
// Сеть возврата тоже здесь: «Отменить» — это тот же PUT /chats/{id}/archived
// с archived=false. Обновление своего состояния остаётся за вызывающим — он
// получает свежую сессию колбэком onRestored.

import type { Session } from '../types';
import { archiveApi } from '../api/chats';
import { showToast } from './toast';

export function showArchivedToast(chat: Session, onRestored: (fresh: Session) => void) {
  showToast(
    'Чат убран в архив',
    `«${chat.name ?? 'Без названия'}» больше не в общем списке.`,
    'info',
    {
      label: 'Отменить',
      onClick: async () => {
        try {
          onRestored(await archiveApi.setArchived(chat.id, false));
        } catch (e) {
          // 409 «в чате идёт ход» и прочие отказы бэкенд называет точнее нашей
          // обёртки — отдаём текст как есть
          showToast('Архив', e instanceof Error ? e.message : 'Не удалось вернуть чат из архива');
        }
      },
    },
  );
}
