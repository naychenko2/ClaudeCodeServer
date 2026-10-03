// Вход из «Файлов» в контекст хода (слот context-opener, ADR-023, 2к-2).

import { ensureThreads, getThreadsState, mutate } from '../thread/threadStore';
import { threadsApi } from '../thread/threadsApi';
import { imageRefOf } from './work';

// Вход из «Файлов» (слот context-opener): сервер найдёт нить файла или заведёт новую с якорем в ленте,
// фокус встанет на неё; ссылка на эту нить и есть объект контекста. null — мутация не прошла (тост уже показан)
export async function imageRefOfPath(projectId: string, sessionId: string, path: string) {
  await ensureThreads(projectId, sessionId);
  const ok = await mutate(projectId, sessionId, rev => threadsApi.create(projectId, sessionId, { file: path, revision: rev }));
  const focus = ok ? getThreadsState(sessionId).focus : null;
  return focus ? imageRefOf(focus, null) : null;
}
