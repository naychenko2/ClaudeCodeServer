// Признак «открытый файл не из корня проекта» для AI-палитры. fromRoot считает FileViewer
// (файл найден в дереве чата, branch != null → только чтение) и сам сообщает сюда;
// палитра по нему скрывает действия, которые пишут рядом с файлом в корень проекта.
// path — путь файла, к которому относится признак: чужой (устаревший) признак не действует.

export interface FileCtx { path: string; fromRoot: boolean }

let _state: FileCtx | undefined;

export function setFileContext(path: string, fromRoot: boolean): void {
  _state = { path, fromRoot };
}

export function clearFileContext(path: string): void {
  if (_state?.path === path) _state = undefined;
}

export function getFileContext(): FileCtx | undefined {
  return _state;
}
