// Общая механика кнопок «в контекст чата» (эскиз B2 §3): одна и та же кнопка стоит
// в шапке файла, шапке ридера, шапке задачи и пунктом контекстного меню строки —
// и все они обязаны одинаково гейтиться, одинаково подписываться и одинаково
// переключаться. Логика собрана здесь, чтобы четыре места не разъехались.
//
// Кнопка живёт, только когда включён флаг chat-context И открыт чат: материал
// кладут В ЧАТ, и без чата действие бессмысленно.
import { useCallback } from 'react';
import type { SessionContextType } from '../../types';
import { showToast } from '../../lib/toast';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import {
  addToChatContext, inChatContext, removeFromChatContext,
  useActiveChatForContext, useChatContext,
} from '../../lib/chatContext';

export interface ContextButtonState {
  // false — кнопки/пункта нет вовсе (флаг выключен, чата нет, чат не в проекте)
  available: boolean;
  // Материал уже приложен к чату: подпись и иконка переключаются на «Убрать»
  inContext: boolean;
  title: string;
  toggle: () => void;
}

// name — человекочитаемое имя материала: уходит в тост подтверждения
export function useContextButton(type: SessionContextType, id: string | null, name?: string): ContextButtonState {
  const on = useFeature(FLAGS.chatContext);
  const chat = useActiveChatForContext();
  const list = useChatContext(chat?.sessionId ?? null);
  const inCtx = !!id && inChatContext(list, type, id);

  const toggle = useCallback(() => {
    if (!chat || !id) return;
    const label = name || id;
    if (inCtx) {
      void removeFromChatContext(chat.projectId, chat.sessionId, type, id)
        .then(() => showToast('Контекст чата', `Убрано: ${label}`))
        .catch(() => showToast('Контекст чата', 'Не удалось убрать материал', 'info'));
      return;
    }
    // Первый материал в этом чате — поясняем, что произошло: полоса появляется
    // внезапно, и без подписи её принимают за вложение
    const first = !list || list.length === 0;
    void addToChatContext(chat.projectId, chat.sessionId, { type, id, title: name ?? null })
      .then(added => {
        if (!added) return;
        showToast('В контекст чата', first
          ? `«${label}» останется под рукой у чата, пока вы его не уберёте`
          : `Добавлено: ${label}`);
      })
      .catch(() => showToast('Контекст чата', 'Не удалось добавить материал', 'info'));
  }, [chat, id, inCtx, list, name, type]);

  return {
    available: on && !!chat && !!id,
    inContext: inCtx,
    title: inCtx ? 'Убрать из контекста' : 'В контекст чата',
    toggle,
  };
}
