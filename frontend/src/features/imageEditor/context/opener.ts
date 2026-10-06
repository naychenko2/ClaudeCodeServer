// Вход из «Файлов» в контекст хода (слот context-opener, ADR-023, 2к-2).

import { showToast } from 'aihome_shell/kit';
import { ensureThreads, getThreadsState, mutate } from '../thread/threadStore';
import { threadsApi } from '../thread/threadsApi';
import { imageRefOf } from './work';

// Вход из «Файлов» (слот context-opener): сервер найдёт нить файла или заведёт новую с якорем в ленте,
// фокус встанет на неё; ссылка на эту нить и есть объект контекста. null — мутация не прошла (тост уже показан)
export async function imageRefOfPath(projectId: string, sessionId: string, path: string) {
  await ensureThreads(projectId, sessionId);
  const ok = await mutate(projectId, sessionId, rev => threadsApi.create(projectId, sessionId, { file: path, revision: rev }));
  if (!ok) return null;
  const st = getThreadsState(sessionId);
  // Нить уже есть, а фокус сервер увёл (основной сейчас другой объект): ищем нить по пути файла
  const id = st.focus ?? [...st.threads].reverse().find(t => t.file === path || t.lineage.includes(path))?.id ?? null;
  if (!id) {
    showToast('Не удалось найти картинку среди нитей чата', path, 'error');
    return null;
  }
  return imageRefOf(id, null);
}
