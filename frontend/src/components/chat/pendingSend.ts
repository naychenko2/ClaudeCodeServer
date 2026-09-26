// Отложенное сообщение ChatPanel (уходит само после присоединения к чату). Строка — прежние
// вызовы: только текст. Объект — чат картинки (ADR-018 §6): свои вложения и пометка снимка
// холста, отправка через SendImageChatMessage

import type { ImageSnapshotMark } from '../../types';

export interface PendingChatSend {
  text: string;
  attachedPaths: string[];
  imageSnapshot?: ImageSnapshotMark | null;
}

export type PendingSendOpts = { imageChat: { snapshot: ImageSnapshotMark | null } } | undefined;

// Аргументы send(text, paths, mode, opts) для отложенного сообщения
export function pendingSendArgs(msg: string | PendingChatSend, mode: string, imageChat: boolean): [string, string[], string, PendingSendOpts] {
  if (typeof msg === 'string') return [msg, [], mode, undefined];
  return [msg.text, msg.attachedPaths, mode, imageChat ? { imageChat: { snapshot: msg.imageSnapshot ?? null } } : undefined];
}
