// Вход из «Файлов» в контекст хода (слот context-opener, ADR-023): звуковой файл проекта становится нитью-основным объектом.

import { showToast } from 'aihome_shell/kit';
import { audioApi } from '../api';
import { audioScope } from '../scope';
import { ensureAudioThreads, getThreadsState, mutate } from '../thread/threadStore';
import { audioRefOf } from './work';

export const isAudioFile = (path: string) => /\.(wav|mp3|flac|ogg)$/i.test(path);

// Сервер найдёт нить файла или заведёт новую с якорем в ленте, фокус встанет на неё; ссылка на эту нить и есть
// объект контекста. null — мутация не прошла (тост уже показан)
export async function audioRefOfPath(projectId: string, sessionId: string, path: string) {
  const scope = audioScope(projectId);
  await ensureAudioThreads(scope, sessionId);
  const ok = await mutate(scope, sessionId, rev => audioApi.open(scope, sessionId, { file: path, revision: rev }));
  if (!ok) return null;
  const st = getThreadsState(sessionId);
  // Нить уже есть, а фокус сервер увёл (основной сейчас другой объект): ищем нить по пути файла
  const id = st.focus ?? [...st.threads].reverse().find(t => t.file === path || t.lineage.includes(path))?.id ?? null;
  if (!id) {
    showToast('Не удалось найти звук среди нитей чата', path, 'error');
    return null;
  }
  return audioRefOf(id, null);
}
