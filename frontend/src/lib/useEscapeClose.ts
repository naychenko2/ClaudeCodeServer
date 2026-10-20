import { useEffect, useRef } from 'react';

// Esc закрывает «просмотр в центре» (файл / страница фичи) — ОДНО правило для обоих:
// не перехватываем, если печатают в поле/редакторе (INPUT/TEXTAREA/contentEditable) или
// уже открыт диалог/меню ([role=dialog]/[role=menu]) — там Escape нужнее им.
//
// Слушатель вешаем один раз (deps []), но зовём ВСЕГДА свежий onClose: через ref,
// который обновляется на каждый рендер. При размонтировании снимаем (return).
// Общий хук FileViewer и FeaturePage: правка правила доезжает и до файла, и до страницы.
export function useEscapeClose(onClose: () => void) {
  const closeRef = useRef(onClose);
  closeRef.current = onClose;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      if (document.querySelector('[role="dialog"], [role="menu"]')) return;
      void closeRef.current();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
}
