// Диалог конфликта сохранения (S2c-F1, п. 27–28) и имя копии.
// Окружение node: SSR-рендер настоящего SaveConflictDialog; Modal подменён тонкой
// обёрткой (портал в SSR невозможен), кнопки и остальное — настоящие.
import { createElement, type ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SaveConflictDialog, type SaveConflict } from '../SaveConflictDialog';
import { conflictCopyName } from '../../lib/conflictCopyName';

const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../ui/useIsMobileModal', () => ({ useIsMobileModal: () => mobileFlag.value }));
vi.mock('../ui/Modal', () => ({
  Modal: ({ title, footer, children }: { title?: ReactNode; footer?: ReactNode; children?: ReactNode }) =>
    createElement('div', null, createElement('h1', null, title), children, footer),
}));

beforeEach(() => { mobileFlag.value = false; });

const conflict: SaveConflict = {
  code: 'file_changed', sha256: 'abc',
  author: { userId: 'u1', displayName: 'Андрей' },
};
const DIFF = '@@ -1,1 +1,1 @@\n-старая строка\n+новая строка\n';

const render = (over: Partial<Parameters<typeof SaveConflictDialog>[0]> = {}) =>
  renderToStaticMarkup(createElement(SaveConflictDialog, {
    conflict, fileName: 'menu.md', copyName: 'menu (копия, Мария).md', diff: DIFF,
    onSaveCopy: () => {}, onReplace: () => {}, onTakeTheirs: () => {}, onClose: () => {},
    ...over,
  }));

const COPY = 'Сохранить мою копией';
const REPLACE = 'Заменить своей';
const TAKE = 'Взять чужую версию';

describe('conflictCopyName', () => {
  it('обычный файл: суффикс перед расширением', () => {
    expect(conflictCopyName('a.md', 'Мария')).toBe('a (копия, Мария).md');
  });
  it('без расширения: суффикс в конец', () => {
    expect(conflictCopyName('a', 'Мария')).toBe('a (копия, Мария)');
  });
  it('скрытый файл без расширения: .env не делится', () => {
    expect(conflictCopyName('.env', 'Мария')).toBe('.env (копия, Мария)');
  });
  it('путь сохраняется, расширение — после последней точки', () => {
    expect(conflictCopyName('src/menu/a.tar.gz', 'Мария')).toBe('src/menu/a.tar (копия, Мария).gz');
  });
});

describe('SaveConflictDialog', () => {
  it('360: кнопки идут копия → заменить → взять чужое', () => {
    mobileFlag.value = true;
    const html = render();
    const [i, j, k] = [html.indexOf(COPY), html.indexOf(REPLACE), html.indexOf(TAKE)];
    expect(i).toBeGreaterThan(-1);
    expect(i).toBeLessThan(j);
    expect(j).toBeLessThan(k);
    expect(html).toContain('flex-direction:column');
  });

  it('360: дифф свёрнут за «Показать разницу»', () => {
    mobileFlag.value = true;
    const html = render();
    expect(html).toContain('Показать разницу');
    expect(html).not.toContain('conflict-diff');
    expect(html).not.toContain('новая строка');
  });

  it('десктоп: primary «копия» справа, дифф показан', () => {
    const html = render();
    expect(html.indexOf(TAKE)).toBeLessThan(html.indexOf(REPLACE));
    expect(html.indexOf(REPLACE)).toBeLessThan(html.indexOf(COPY));
    expect(html).toContain('новая строка');
    expect(html).not.toContain('Показать разницу');
  });

  it('главное действие — primary: «копия» на accent-фоне, остальные нет', () => {
    const btn = (html: string, label: string) => html.match(new RegExp(`<button[^>]*>(?:(?!</button>).)*${label}`))?.[0] ?? '';
    for (const mobile of [false, true]) {
      mobileFlag.value = mobile;
      const html = render();
      expect(btn(html, COPY)).toContain('background:var(--c-accent)');
      expect(btn(html, REPLACE)).not.toContain('background:var(--c-accent)');
      expect(btn(html, TAKE)).not.toContain('background:var(--c-accent)');
    }
  });

  it('source с чатом — подпись в шапке; приватный — своя; null/нет — подписи нет', () => {
    expect(render({ conflict: { ...conflict, source: { chatTitle: 'Меню на зиму' } } })).toContain('чат „Меню на зиму“');
    expect(render({ conflict: { ...conflict, source: { private: true } } })).toContain('приватный чат');
    expect(render({ conflict: { ...conflict, source: null } })).not.toMatch(/чат/);
    expect(render()).not.toMatch(/чат/);
  });

  it('тач-цели: все три кнопки не ниже 40 px', () => {
    const html = render();
    const heights = [...html.matchAll(/min-height:(\d+)px/g)].map(m => Number(m[1]));
    expect(heights.length).toBeGreaterThanOrEqual(3);
    expect(Math.min(...heights)).toBeGreaterThanOrEqual(40);
  });

  it('имя копии показано под кнопками', () => {
    expect(render()).toContain('menu (копия, Мария).md');
  });

  // Доработка по ревью d90f7171: копия — за своей дверью ('file.create'). Вызывающий без
  // этого права не передаёт onSaveCopy — и копии нет ни в ряду, ни в подписи (0 узлов).
  it('без onSaveCopy: кнопки копии и подписи имени нет; «заменить» и «взять чужое» — на месте', () => {
    for (const mobile of [false, true]) {
      mobileFlag.value = mobile;
      const html = render({ onSaveCopy: undefined });
      expect(html, 'кнопки копии нет').not.toContain(COPY);
      expect(html, 'подписи имени копии нет').not.toContain('menu (копия, Мария).md');
      expect(html, '«заменить» остался').toContain(REPLACE);
      expect(html, '«взять чужое» остался').toContain(TAKE);
    }
  });

  it('Office/draw.io: без диффа — две строки версий', () => {
    const html = render({ noDiff: true, diff: undefined, mineAt: '14:02' });
    expect(html).not.toContain('conflict-diff');
    expect(html).toContain('ваша версия · 14:02');
    expect(html).toContain('в проекте · Андрей');
  });

  // DEF-65cf: текст повторного конфликта собирается по code, как заголовок:
  // для заметки было жёсткое «Файл…».
  it('повторный конфликт: Notice по code — «Файл…» / «Заметку…»', () => {
    const noteConflict: SaveConflict = { ...conflict, code: 'note_changed' };
    for (const noDiff of [false, true]) {
      const file = render({ again: true, noDiff });
      expect(file, 'файл: «Файл…»').toContain('Файл изменили ещё раз');
      const note = render({ again: true, noDiff, conflict: noteConflict });
      expect(note, 'заметка: «Заметку…»').toContain('Заметку изменили ещё раз');
      expect(note, 'заметка: без «Файл…»').not.toContain('Файл');
    }
    // с диффом — та же формулировка про свежую версию, по code
    expect(render({ again: true })).toContain('Файл изменили ещё раз — показываем свежую версию.');
    expect(render({ again: true, conflict: noteConflict })).toContain('Заметку изменили ещё раз — показываем свежую версию.');
  });

  it('загрузка диффа — скелетон, кнопки активны', () => {
    const html = render({ diffLoading: true });
    expect(html).toContain('aria-busy="true"');
    expect(html).not.toContain('disabled');
  });
});
