// Общая модалка подтверждения мержа (волна 3, Ф6): рендер-тесты.
//
// Макет: заголовок «Смержить «{название}» в master?», пояснение
// «Марк получит задачу мержа и сольёт ветку {ветка}.», кнопки «Отмена»/«Мерж».
// Состояния: pending → «Запускаю…» (disabled); error (НЕ-409) → красная ошибка +
// «Повторить»; 360 → шторка снизу (cc-sheet-card).
// Закрытый хвост (FS-XF1): риск tail — блок поимённо + выбор решения, «Мерж»
// неактивна без выбора; без хвоста диалог прежний.
//
// Окружение node (без jsdom): fakeDom. Модалка — портал в document.body, поэтому
// ищем текст/классы в body, не в контейнере.

import { createElement } from 'react';
import { describe, it, expect, afterEach } from 'vitest';
import { FeatureMergeDialog, type FeatureMergeDialogProps } from './FeatureMergeDialog';
import type { FeatureMergeRisk } from '../../types/featureRun';
import { mount, installFakeDom, uninstallFakeDom, textNodes, findNodeByAttr, reactProps } from '../chat/__tests__/fakeDom';

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

// Узел в body по data-testid (модалка — портал в body, как в bodyText выше).
function findTestId(id: string) {
  const body = (globalThis as { document: { body: unknown } }).document.body;
  return findNodeByAttr(body as never, 'data-testid', id);
}

// Текст блока рисков (пустая строка, если блока нет).
function risksText(): string {
  const el = findTestId('feature-merge-risks');
  return el ? textNodes(el).join('') : '';
}

type Props = FeatureMergeDialogProps;
function render(over: Partial<Props> = {}) {
  installFakeDom();
  mount(createElement(FeatureMergeDialog, {
    title: 'Моя фича',
    branch: 'feat/my-feature',
    pending: false,
    error: null,
    tailChoice: null,
    onTailChoice: () => {},
    onCancel: () => {},
    onConfirm: () => {},
    ...over,
  }));
}

// Кнопка диалога по ТОЧНОЙ подписи (обход body — модалка порталом). Точное
// совпадение, не includes: «Мерж» не должен ловить сегмент «Мержить, хвост оставить».
function buttonByText(text: string) {
  const body = (globalThis as { document: { body: unknown } }).document.body as unknown as {
    nodeType: number; nodeName: string; childNodes: unknown[];
  };
  let hit: { nodeType: number; nodeName: string; childNodes: unknown[] } | null = null;
  const walk = (x: { nodeType: number; nodeName?: string; childNodes: unknown[] }) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textNodes(x as never).join('') === text) { hit = x as never; return; }
    for (const c of x.childNodes) walk(c as never);
  };
  walk(body as never);
  return hit as (Parameters<typeof reactProps>[0] & { style?: Record<string, unknown> }) | null;
}

// Кнопка «Мерж» (не «Запускаю…»/«Повторить») или null.
const mergeButton = () => buttonByText('Мерж');

// disabled-проп кнопки «Мерж» (React props, не DOM-атрибут: disabled React пишет
// свойством узла, в fakeDom его нет; props — точный источник).
function mergeDisabled(): boolean {
  const btn = mergeButton();
  if (!btn) return true; // кнопки нет — «Мерж» невозможен в любом случае
  return Boolean(reactProps(btn)?.disabled);
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

  // ── Риски мержа (Ш8): блок feature-merge-risks над кнопками ──────────────

  it('risks нет (undefined) — блока рисков нет', () => {
    render();
    expect(findTestId('feature-merge-risks')).toBeNull();
    expect(risksText()).toBe('');
  });

  it('risks пустой ([] — чистый мерж) — блока рисков нет', () => {
    render({ risks: [] });
    expect(findTestId('feature-merge-risks')).toBeNull();
    expect(risksText()).toBe('');
  });

  it('три вида — текст ровно «Не заведено шагов: 59 · Заведены, но не приняты: 13 · Трекер расходится: 1»', () => {
    render({ risks: [
      { kind: 'not_created', count: 59 },
      { kind: 'open', count: 13 },
      { kind: 'tracker', count: 1 },
    ] });
    expect(risksText()).toBe('Не заведено шагов: 59 · Заведены, но не приняты: 13 · Трекер расходится: 1');
  });

  it('один вид — печатается только он', () => {
    render({ risks: [{ kind: 'open', count: 13 }] });
    const t = risksText();
    expect(t).toBe('Заведены, но не приняты: 13');
    expect(t).not.toContain('Не заведено');
    expect(t).not.toContain('Трекер');
  });

  it('при рисках кнопка подтверждения по-прежнему «Мерж»', () => {
    render({ risks: [
      { kind: 'not_created', count: 59 },
      { kind: 'open', count: 13 },
      { kind: 'tracker', count: 1 },
    ] });
    const t = bodyText();
    expect(t).toContain('Мерж');
    expect(t).not.toContain('Повторить');
    expect(t).not.toContain('Запускаю');
  });

  // ── Закрытый хвост (FS-XF1, §8.3): блок feature-merge-tail с выбором решения ──
  // Мутации (в отчёте): (1) confirmDisabled без хвоста/без выбора → «без выбора
  // неактивна» или «без хвоста активна» краснеет; (2) tail не фильтруется из
  // текстовой строки → «не входит в текстовую строку» краснеет; (3) onTailChoice
  // не вызывается → «клик варианта» краснеет.

  const tailRisks: FeatureMergeRisk[] = [
    { kind: 'not_created', count: 4 },
    { kind: 'open', count: 2 },
    { kind: 'tail', count: 3 },
    { kind: 'tracker', count: 1 },
  ];

  it('риск tail: блок поимённо (ключи бэка, порядок как есть) и два варианта решения', () => {
    render({ risks: tailRisks, tailKeys: ['Ф1', 'Ф2', 'Д3'] });
    const block = findTestId('feature-merge-tail');
    expect(block, 'блок хвоста').not.toBeNull();
    const t = textNodes(block!).join(' ');
    expect(t).toContain('Закрытые непринятые шаги: 3');
    // Ключи — поимённо, в порядке бэка (Ф1, Ф2, Д3).
    const keys = textNodes(findTestId('feature-merge-tail-keys')!).join(' ');
    expect(keys).toContain('Ф1');
    expect(keys.indexOf('Ф1')).toBeLessThan(keys.indexOf('Ф2'));
    expect(keys.indexOf('Ф2')).toBeLessThan(keys.indexOf('Д3'));
    expect(t).toContain('Снять хвост и мержить');
    expect(t).toContain('Мержить, хвост оставить');
  });

  it('хвост без выбора: «Мерж» неактивна (бэк ответил бы 409 tail_decision_required)', () => {
    render({ risks: [{ kind: 'tail', count: 3 }], tailKeys: ['Ф1', 'Ф2', 'Ф3'] });
    expect(mergeButton(), 'кнопка «Мерж» на месте').not.toBeNull();
    expect(mergeDisabled(), '«Мерж» активна без решения о хвосте').toBe(true);
  });

  it('клик варианта решения вызывает onTailChoice с withdraw и с keep (выбор уходит в тело запроса)', () => {
    const chosen: string[] = [];
    render({
      risks: [{ kind: 'tail', count: 3 }], tailKeys: ['Ф1'],
      onTailChoice: (v) => { chosen.push(v); },
    });
    const w = buttonByText('Снять хвост и мержить');
    const keep = buttonByText('Мержить, хвост оставить');
    expect(w, 'вариант «withdraw»').not.toBeNull();
    expect(keep, 'вариант «keep»').not.toBeNull();
    (reactProps(w)!.onClick as () => void)();
    (reactProps(keep)!.onClick as () => void)();
    expect(chosen, 'оба выбора дошли до вызывающего').toEqual(['withdraw', 'keep']);
  });

  it('выбор сделан (tailChoice keep) — «Мерж» активна', () => {
    render({ risks: [{ kind: 'tail', count: 3 }], tailKeys: ['Ф1'], tailChoice: 'keep' });
    expect(mergeButton(), 'кнопка «Мерж» на месте').not.toBeNull();
    expect(mergeDisabled(), '«Мерж» неактивна при выбранном keep').toBe(false);
  });

  it('tail не входит в текстовую строку рисков; живые и notCreated — как раньше', () => {
    render({ risks: tailRisks, tailKeys: ['Ф1', 'Ф2', 'Д3'] });
    const t = risksText();
    expect(t).toBe('Не заведено шагов: 4 · Заведены, но не приняты: 2 · Трекер расходится: 1');
    expect(t).not.toContain('Закрытые');
  });

  it('только tail — текстового блока рисков нет, есть только блок решения', () => {
    render({ risks: [{ kind: 'tail', count: 3 }], tailKeys: ['Ф1'] });
    expect(findTestId('feature-merge-risks'), 'текстового блока нет').toBeNull();
    expect(findTestId('feature-merge-tail'), 'блок решения на месте').not.toBeNull();
  });

  it('хвоста нет (риски без tail) — блока решения нет, «Мерж» активна', () => {
    render({ risks: [{ kind: 'open', count: 2 }] });
    expect(findTestId('feature-merge-tail'), 'блок хвоста при живом риске').toBeNull();
    expect(mergeDisabled(), '«Мерж» без хвоста').toBe(false);
  });

  it('tailNotice («хвост изменился») — предупреждение в диалоге, не красная ошибка', () => {
    render({
      risks: [{ kind: 'tail', count: 3 }], tailKeys: ['Ф1'],
      tailNotice: 'Хвост изменился, проверь список',
    });
    const n = findTestId('feature-merge-tail-notice');
    expect(n, 'предупреждение о хвосте').not.toBeNull();
    expect(textNodes(n!).join('')).toContain('Хвост изменился, проверь список');
    expect(findTestId('feature-merge-error'), 'красной ошибки нет').toBeNull();
  });

  it('360: варианты решения в столбик, тач-цель ≥ 40 (состав хвоста не меняет)', () => {
    installFakeDom();
    (globalThis as { window: Record<string, unknown> }).window.matchMedia =
      () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    mount(createElement(FeatureMergeDialog, {
      title: 'Моя фича', branch: 'feat/my-feature', pending: false, error: null,
      risks: [{ kind: 'tail', count: 2 }], tailKeys: ['Ф1', 'Ф2'], tailChoice: null,
      onTailChoice: () => {}, onCancel: () => {}, onConfirm: () => {},
    }));
    const w = buttonByText('Снять хвост и мержить');
    expect(w, 'вариант «withdraw» на 360').not.toBeNull();
    // columns=1 → ширина 100 % (столбик); touch → minHeight 40.
    const st = w!.style ?? {};
    expect(String(st.flex), 'на 360 варианты не в столбик').toContain('100.0000%');
    expect(parseFloat(String(st.minHeight)), 'тач-цель < 40 на 360').toBeGreaterThanOrEqual(40);
  });

  it('360 → шторка снизу (cc-sheet-card), не центрированная карточка', () => {
    installFakeDom();
    // Мобила: matchMedia → matches: true (установляем после installFakeDom — он сбрасывает window).
    (globalThis as { window: Record<string, unknown> }).window.matchMedia =
      () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    mount(createElement(FeatureMergeDialog, {
      title: 'Моя фича', branch: 'feat/my-feature', pending: false, error: null,
      tailChoice: null, onTailChoice: () => {},
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
