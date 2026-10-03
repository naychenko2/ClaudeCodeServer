// Вход из «Файлов» в контекст хода (слот context-opener, ADR-023): звуковой файл проекта становится нитью-основным объектом.

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
  const focus = ok ? getThreadsState(sessionId).focus : null;
  return focus ? audioRefOf(focus, null) : null;
}
