import { useEffect, useState, useSyncExternalStore } from 'react';

// «Вернуть» после снятия выбора: человек снял картинку или звук (✕ на чипе) — над
// полосой на 4 с встаёт плашка с кнопкой «Вернуть». Снятие агентом или автоматикой
// плашку не показывает (и гасит прежнюю — возвращать уже нечего).
// Ядро — контроллер без React: его таймер проверяется в node без DOM.

export const RELEASE_UNDO_MS = 4000;

export interface ReleaseOffer<T> {
  // Что вернуть: прежний фокус, режим, отметки — решает раздел
  snapshot: T;
  text: string;
}

export interface ReleaseUndoController<T> {
  current(): ReleaseOffer<T> | null;
  subscribe(fn: () => void): () => void;
  // Снят выбор; byHuman — снял человек
  release(offer: ReleaseOffer<T>, byHuman: boolean): void;
  // «Вернуть»: отдаёт снимок и гасит плашку; без плашки — null
  undo(): T | null;
  dismiss(): void;
  dispose(): void;
}

export function createReleaseUndo<T>(ms = RELEASE_UNDO_MS): ReleaseUndoController<T> {
  let offer: ReleaseOffer<T> | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  const subs = new Set<() => void>();
  const set = (next: ReleaseOffer<T> | null) => {
    if (timer) { clearTimeout(timer); timer = null; }
    offer = next;
    if (next) timer = setTimeout(() => set(null), ms);
    subs.forEach(fn => fn());
  };
  return {
    current: () => offer,
    subscribe(fn) { subs.add(fn); return () => { subs.delete(fn); }; },
    release(next, byHuman) {
      if (byHuman) set(next);
      else if (offer) set(null);
    },
    undo() {
      const o = offer;
      if (o) set(null);
      return o ? o.snapshot : null;
    },
    dismiss() { if (offer) set(null); },
    dispose() { if (timer) clearTimeout(timer); timer = null; subs.clear(); },
  };
}

// Хук над контроллером: один на полосу; при размонтировании таймер гасится
export function useReleaseUndo<T>(ms = RELEASE_UNDO_MS) {
  const [ctl] = useState(() => createReleaseUndo<T>(ms));
  useEffect(() => () => ctl.dispose(), [ctl]);
  const offer = useSyncExternalStore(ctl.subscribe, ctl.current, ctl.current);
  return { offer, release: ctl.release, undo: ctl.undo, dismiss: ctl.dismiss };
}
