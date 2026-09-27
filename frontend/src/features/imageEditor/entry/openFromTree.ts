// Вход из дерева файлов (записка v3, «Вход из дерева файлов»; решение Андрея 3):
// «Редактировать картинку» и «Нарисовать картинку» открывают последний активный чат
// проекта — даже занятый ходом агента, — а новый создают, только если чатов нет.
// Картинка становится выбранной в этом чате: у файла — его нить (сервер найдёт
// существующую или заведёт новую) и попап «Редактор», у папки — карточка-черновик.

import { api, openChatById, showToast } from 'aihome_shell/kit';
import type { ImageEditorOpenRequest } from '../../../lib/subsystems/registryCore';
import type { Session } from '../../../types';
import { createDraft, workWithFile } from '../thread/actions';
import { ensureThreads, getThreadsState, openEditor } from '../thread/threadStore';

// Последний активный чат: самый свежий по updatedAt среди неархивных
export function lastActiveChat(list: Session[]): Session | null {
  let best: Session | null = null;
  for (const s of list) {
    if (s.isArchived) continue;
    if (!best || Date.parse(s.updatedAt) > Date.parse(best.updatedAt)) best = s;
  }
  return best;
}

async function chatFor(projectId: string): Promise<Session> {
  const list = await api.sessions.list(projectId);
  return lastActiveChat(list) ?? await api.sessions.create(projectId);
}

export async function openFromTree(req: ImageEditorOpenRequest): Promise<void> {
  const { projectId, target } = req;
  try {
    const chat = await chatFor(projectId);
    // Ревизия нужна мутации: без загруженного состояния первая запись словила бы 409
    await ensureThreads(projectId, chat.id);
    const ok = target.kind === 'edit'
      ? await workWithFile(projectId, chat.id, target.path)
      : await createDraft(projectId, chat.id, target.folder);
    if (!ok) return;
    if (!await openChatById(chat.id)) return;
    const focus = getThreadsState(chat.id).focus;
    if (target.kind === 'edit' && focus) openEditor(chat.id, focus);
  } catch (e) {
    showToast('Не удалось открыть картинку в чате', (e as Error).message, 'error');
  }
}
