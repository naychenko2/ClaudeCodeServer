// Признак «открытый файл не из корня проекта» для AI-палитры. fromRoot считает FileViewer
// (файл найден в дереве чата, branch != null → только чтение) и сам сообщает сюда;
// палитра по нему скрывает действия, которые пишут рядом с файлом в корень проекта.
// path — путь файла, к которому относится признак: чужой (устаревший) признак не действует.

// Слот реактивный (как lib/ai/chatContext): признак приходит из эффекта FileViewer уже после
// загрузки файла, и открытая палитра обязана пересчитать список без переоткрытия.

import { useSyncExternalStore } from 'react';

export interface FileCtx { path: string; fromRoot: boolean }

let _state: FileCtx | undefined;
const _listeners = new Set<() => void>();

export function subscribeFileContext(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

export function setFileContext(path: string, fromRoot: boolean): void {
  if (_state?.path === path && _state.fromRoot === fromRoot) return;
  _state = { path, fromRoot };
  _listeners.forEach(fn => fn());
}

export function clearFileContext(path: string): void {
  if (_state?.path !== path) return;
  _state = undefined;
  _listeners.forEach(fn => fn());
}

export function useFileContext(): FileCtx | undefined {
  return useSyncExternalStore(subscribeFileContext, getFileContext, getFileContext);
}

export function getFileContext(): FileCtx | undefined {
  return _state;
}
