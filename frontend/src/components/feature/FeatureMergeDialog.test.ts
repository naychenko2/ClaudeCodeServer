// Общая модалка подтверждения мержа (волна 3, Ф6): рендер-тесты.
//
// Макет: заголовок «Смержить «{название}» в master?», пояснение
// «Марк получит задачу мержа и сольёт ветку {ветка}.», кнопки «Отмена»/«Мерж».
// Состояния: pending → «Запускаю…» (disabled); error (НЕ-409) → красная ошибка +
// «Повторить»; 360 → шторка снизу (cc-sheet-card).
//
// Окружение node (без jsdom): fakeDom. Модалка — портал в document.body, поэтому
// ищем текст/классы в body, не в контейнере.

import { createElement } from 'react';
import { describe, it, expect, afterEach } from 'vitest';
import { FeatureMergeDialog, type FeatureMergeDialogProps } from './FeatureMergeDialog';
import { mount, installFakeDom, uninstallFakeDom, textNodes } from '../chat/__tests__/fakeDom';

// Диалог рендерится порталом в document.body — собираем весь его текст.
function bodyText(): string {
  const body = (globalThis as { document: { body: unknown } }).document.body as { childNodes: unknown[] };
  return textNodes(body as never).join('');
}

// Есть ли в body элемент с class, содержащим `cls` (шторка cc-sheet-card и т. п.).
function bodyHasClass(cls: string): boolean {
  const body = (globalThis as { document: { body: unknown } }).document.body as { childNodes: unknown[] };
  let hit = false;
  const walk = (x: { nodeType: number; getAttribute?: (a: string) => string | null; childNodes: unknown[] }) => {
    if (hit) return;
    if (x.nodeType === 1) {
      const c = x.getAttribute?.('class') ?? '';
      if (c.includes(cls)) { hit = true; return; }
    }
    for (const ch of x.childNodes) walk(ch as never);
  };
  walk(body as never);
  return hit;
}

type Props = FeatureMergeDialogProps;
function render(over: Partial<Props> = {}) {
  installFakeDom();
  mount(createElement(FeatureMergeDialog, {
    title: 'Моя фича',
    branch: 'feat/my-feature',
    pending: false,
    error: null,
    onCancel: () => {},
    onConfirm: () => {},
    ...over,
  }));
}

afterEach(() => { uninstallFakeDom(); });

describe('FeatureMergeDialog', () => {
  it('заголовок «Смержить «{название}» в master?»', () => {
    render();
    expect(bodyText()).toContain('Смержить «Моя фича» в master?');
  });

  it('пояснение: «Марк получит задачу мержа и сольёт ветку {ветка}.»', () => {
    render();
    expect(bodyText()).toContain('Марк получит задачу мержа и сольёт ветку feat/my-feature.');
  });

  it('ветки нет (null) — нейтральная фраза, без «ветку null»', () => {
    render({ branch: null });
    const t = bodyText();
    expect(t).toContain('Марк получит задачу мержа и сольёт ветку фичи.');
    expect(t).not.toContain('null');
  });

  it('кнопки «Отмена»/«Мерж»', () => {
    render();
    const t = bodyText();
    expect(t).toContain('Отмена');
    expect(t).toContain('Мерж');
  });

  it('pending → «Запускаю…» (вместо «Мерж»)', () => {
    render({ pending: true });
    const t = bodyText();
    expect(t).toContain('Запускаю…');
    expect(t).not.toContain('Повторить');
  });

  it('error (НЕ-409) → красная ошибка + «Повторить»', () => {
    render({ error: 'Worktree фичи не найден на диске' });
    const t = bodyText();
    expect(t).toContain('Worktree фичи не найден на диске');
    expect(t).toContain('Повторить');
    // Красная ошибка — не «Мерж» (подсказка подтверждения заменена).
    expect(t).not.toContain('Мерж');
  });

  it('360 → шторка снизу (cc-sheet-card), не центрированная карточка', () => {
    installFakeDom();
    // Мобила: matchMedia → matches: true (установляем после installFakeDom — он сбрасывает window).
    (globalThis as { window: Record<string, unknown> }).window.matchMedia =
      () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    mount(createElement(FeatureMergeDialog, {
      title: 'Моя фича', branch: 'feat/my-feature', pending: false, error: null,
      onCancel: () => {}, onConfirm: () => {},
    }));
    // Шторка (cc-sheet-card) на месте; центрированная карточка (cc-modal-card) — нет.
    expect(bodyHasClass('cc-sheet-card'), '360: нет шторки (cc-sheet-card)').toBe(true);
    expect(bodyHasClass('cc-modal-card'), '360: центрированная карточка вместо шторки').toBe(false);
  });

  it('десктоп (>=768) → центрированная карточка (cc-modal-card), не шторка', () => {
    render(); // matchMedia по умолчанию matches: false → десктоп
    expect(bodyHasClass('cc-modal-card'), 'десктоп: нет карточки (cc-modal-card)').toBe(true);
    expect(bodyHasClass('cc-sheet-card'), 'десктоп: шторка вместо карточки').toBe(false);
  });
});
