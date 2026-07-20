// Глобальный перехват Ctrl+A / Ctrl+C для «активного документа».
//
// Разметка (декларативно, без подписок из компонентов):
//   data-selection-scope        — контейнер, чьё содержимое выделяет Ctrl+A
//   data-selection-target="css" — селектор внутри scope: выделяется ПОСЛЕДНИЙ видимый
//                                 подходящий потомок (лента чата → последний ответ)
//   data-selection-priority="N" — вес при выборе scope без фокуса/клика (больше = важнее)
//
// Активный scope: содержит фокус → содержит последний клик → самый приоритетный видимый.
// В инпутах, textarea и CodeMirror горячие клавиши не трогаем — там родное поведение.
//
// Ctrl+C без выделения копирует «сырой» текст документа (markdown), если контейнер
// зарегистрирован через registerCopyDoc (FileViewer/NoteView отдают исходник файла).

import { showToast } from './toast';

const copyDocs = new Map<HTMLElement, () => string | null>();

// Регистрирует источник «сырого» текста для Ctrl+C без выделения. Вешать можно на
// стабильного предка scope-контейнера — поиск идёт от scope вверх по дереву.
export function registerCopyDoc(el: HTMLElement, getText: () => string | null): () => void {
  copyDocs.set(el, getText);
  return () => { copyDocs.delete(el); };
}

function isEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) return true;
  if (target.isContentEditable) return true;
  return !!target.closest('.cm-editor, input, textarea, [contenteditable="true"]');
}

function isVisible(el: HTMLElement): boolean {
  return el.isConnected && el.getClientRects().length > 0;
}

function resolveScope(lastPointer: HTMLElement | null): HTMLElement | null {
  const active = document.activeElement instanceof HTMLElement && document.activeElement !== document.body
    ? document.activeElement : null;
  for (const from of [active, lastPointer]) {
    if (!from?.isConnected) continue;
    const scope = from.closest<HTMLElement>('[data-selection-scope]');
    if (scope && isVisible(scope)) return scope;
  }
  // Ни фокус, ни последний клик не внутри scope — самый приоритетный видимый
  // (при равенстве приоритета — последний в DOM-порядке)
  let best: HTMLElement | null = null;
  let bestPriority = -Infinity;
  for (const el of document.querySelectorAll<HTMLElement>('[data-selection-scope]')) {
    if (!isVisible(el)) continue;
    const priority = Number(el.dataset.selectionPriority ?? 0);
    if (priority >= bestPriority) { best = el; bestPriority = priority; }
  }
  return best;
}

function selectionRoot(scope: HTMLElement): HTMLElement {
  const targetSelector = scope.dataset.selectionTarget;
  if (targetSelector) {
    const candidates = scope.querySelectorAll<HTMLElement>(targetSelector);
    for (let i = candidates.length - 1; i >= 0; i--) {
      if (isVisible(candidates[i])) return candidates[i];
    }
  }
  return scope;
}

function findCopyDoc(scope: HTMLElement): (() => string | null) | undefined {
  for (let el: HTMLElement | null = scope; el; el = el.parentElement) {
    const getter = copyDocs.get(el);
    if (getter) return getter;
  }
  return undefined;
}

// Копирование «сырого» markdown (plain text)
export async function copyMarkdown(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

// Копирование отрендеренного документа с форматированием (text/html + text/plain):
// стили в MarkdownViewer инлайновые, так что вставка в Word/почту сохраняет вид.
export async function copyRenderedHtml(el: HTMLElement, plainFallback?: string): Promise<boolean> {
  const plain = plainFallback ?? el.innerText;
  try {
    if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
      await navigator.clipboard.write([new ClipboardItem({
        'text/html': new Blob([el.outerHTML], { type: 'text/html' }),
        'text/plain': new Blob([plain], { type: 'text/plain' }),
      })]);
      return true;
    }
  } catch { /* фолбэк ниже */ }
  return copyMarkdown(plain);
}

// Единожды вешает глобальные обработчики; возвращает cleanup (для HMR/тестов)
export function installSelectionScopes(): () => void {
  let lastPointer: HTMLElement | null = null;

  const onPointerDown = (e: PointerEvent) => {
    if (e.target instanceof HTMLElement) lastPointer = e.target;
  };

  const onKeyDown = (e: KeyboardEvent) => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
    const key = e.key.toLowerCase();
    if (key !== 'a' && key !== 'c' && key !== 'ф' && key !== 'с') return;
    if (isEditable(e.target)) return;

    if (key === 'a' || key === 'ф') {
      // Без документа на странице Ctrl+A не должен выделять весь сайт
      e.preventDefault();
      const scope = resolveScope(lastPointer);
      const selection = window.getSelection();
      if (!scope || !selection) return;
      const range = document.createRange();
      range.selectNodeContents(selectionRoot(scope));
      selection.removeAllRanges();
      selection.addRange(range);
      return;
    }

    // Ctrl+C без выделения — копируем весь документ, если он умеет отдавать исходник
    const selection = window.getSelection();
    if (selection && !selection.isCollapsed) return;
    const scope = resolveScope(lastPointer);
    if (!scope) return;
    const text = findCopyDoc(scope)?.();
    if (!text) return;
    e.preventDefault();
    void copyMarkdown(text).then(ok => {
      if (ok) showToast('Скопировано', 'Документ скопирован как Markdown');
    });
  };

  window.addEventListener('pointerdown', onPointerDown, true);
  window.addEventListener('keydown', onKeyDown, true);
  return () => {
    window.removeEventListener('pointerdown', onPointerDown, true);
    window.removeEventListener('keydown', onKeyDown, true);
  };
}
