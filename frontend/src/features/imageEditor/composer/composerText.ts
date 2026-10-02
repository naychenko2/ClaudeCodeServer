// Текст поля ввода в режиме «Картинка» по чатам: низ панели «Картинки» по нему решает, что
// делает кнопка запуска в «Создать» — отправить набранное или «↻ Ещё N» на пустом поле.
import { useSyncExternalStore } from 'react';

const _texts = new Map<string, string>();
const _subs = new Set<() => void>();
let _version = 0;

export function setImageComposerText(sessionId: string, text: string) {
  if ((_texts.get(sessionId) ?? '') === text) return;
  _texts.set(sessionId, text);
  _version++;
  _subs.forEach(fn => fn());
}

export const getImageComposerText = (sessionId: string | null) => (sessionId && _texts.get(sessionId)) || '';

export function useImageComposerText(sessionId: string | null): string {
  useSyncExternalStore(fn => { _subs.add(fn); return () => { _subs.delete(fn); }; }, () => _version, () => _version);
  return getImageComposerText(sessionId);
}
