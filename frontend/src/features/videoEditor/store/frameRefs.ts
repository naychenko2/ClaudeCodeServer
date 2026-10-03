// Кадры сцены как референсы контекста хода (ADR-023, КТ-5): роли `frame-a` и `frame-b` основного объекта
// «video-scene». Бэкенд при запуске по ревизии берёт кадры только из них, поэтому всё, что выбирает кадр
// (меню чипа, «Нарисовать в „Картинках“»), пишет референс, а не настройки сцены.

import { attachRef, detachRef, getChatContextState, type ChatContextRef } from 'aihome_shell/kit';
import type { FrameRef } from '../api';

export type FrameSlot = 'A' | 'B';

export const FRAME_ROLE: Readonly<Record<FrameSlot, string>> = { A: 'frame-a', B: 'frame-b' };
const IMAGE_KIND = 'image';
const FILE_KIND = 'project-file';
const FRAME_KINDS: readonly string[] = [IMAGE_KIND, FILE_KIND];

export interface FrameInput { kind: string; ref: Record<string, unknown> }

// Кадр слота: самый поздно добавленный из подходящих по виду (как выбирает сервер)
export function frameRefOf(refs: readonly ChatContextRef[], slot: FrameSlot): ChatContextRef | null {
  const role = FRAME_ROLE[slot];
  return refs
    .filter(r => r.role === role && FRAME_KINDS.includes(r.kind))
    .sort((a, b) => b.addedAt.localeCompare(a.addedAt))[0] ?? null;
}

// Кадр сцены → референс контекста. Файл рабочей папки личного чата референсом не бывает: проекта нет
export function frameInputOf(f: FrameRef, personal: boolean): FrameInput | null {
  if (f.kind === 'image') return { kind: IMAGE_KIND, ref: { threadId: f.threadId, versionId: f.versionId } };
  return personal ? null : { kind: FILE_KIND, ref: { path: f.path } };
}

// Референс контекста → кадр сцены (для «Править в „Картинках“»); неполная ссылка кадром не станет
export function frameOfRef(r: ChatContextRef): FrameRef | null {
  if (r.kind === IMAGE_KIND && typeof r.ref.threadId === 'string' && typeof r.ref.versionId === 'string') {
    return { kind: 'image', threadId: r.ref.threadId, versionId: r.ref.versionId };
  }
  return r.kind === FILE_KIND && typeof r.ref.path === 'string' ? { kind: 'file', path: r.ref.path } : null;
}

const sameRef = (a: Record<string, unknown>, b: Record<string, unknown>) => JSON.stringify(a) === JSON.stringify(b);

// Поставить кадр слота (input = null — убрать). Прежние кадры слота снимаются после успешной постановки:
// отказ не оставляет слот пустым
export async function setFrameRef(sessionId: string, slot: FrameSlot, input: FrameInput | null): Promise<boolean> {
  const role = FRAME_ROLE[slot];
  const olds = getChatContextState(sessionId).refs.filter(r => r.role === role && FRAME_KINDS.includes(r.kind));
  const keep = input ? olds.find(r => r.kind === input.kind && sameRef(r.ref, input.ref)) : undefined;
  if (input && !keep && await attachRef(sessionId, { kind: input.kind, ref: input.ref, role }) !== 'ok') return false;
  for (const old of olds) {
    if (old !== keep && await detachRef(sessionId, old.id) !== 'ok') return false;
  }
  return true;
}
