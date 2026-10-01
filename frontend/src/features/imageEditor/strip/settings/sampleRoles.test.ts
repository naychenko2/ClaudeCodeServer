import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Роль образца в панели «Картинки» выбирают строкой прямо под чипами, без модального окна
// (записка v4, «Что меняется»). Окружение node: localStorage на Map, окно с matchMedia
const m = new Map<string, string>();
vi.stubGlobal('localStorage', {
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
} as Storage);
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
// Modal рисуется порталом, которого у renderToStaticMarkup нет: подменяем узлом с заголовком
vi.mock('aihome_shell/kit', async orig => ({
  ...await orig<Record<string, unknown>>(),
  Modal: ({ title, children }: { title: string; children: unknown }) => createElement('div', { 'data-modal': title }, children as never),
}));

const { setSamples } = await import('../../thread/threadStore');
const { SampleChips } = await import('./CharacterSection');

setSamples('p1', [{ id: 's1', source: 'project', name: 'palette.jpg', role: 'style', path: 'ref/palette.jpg', url: '/f/palette.jpg' }]);

describe('роль образца в панели', () => {
  it('клик по чипу открывает выбор роли строкой под чипами: три роли, текущая отмечена', () => {
    const html = renderToStaticMarkup(createElement(SampleChips, { projectId: 'p1', max: 4, inlineRoles: true, initialRoleFor: 's1' }));
    const group = html.slice(html.indexOf('data-sample-roles="palette.jpg"'));
    expect(group).toContain('role="group"');
    for (const r of ['Персонаж — сохранить лицо', 'Стиль', 'Предмет']) expect(group).toContain(r);
    expect(html).not.toContain('Как модели использовать «palette.jpg»</');
  });

  it('пока чип не нажат, строки выбора нет', () => {
    const html = renderToStaticMarkup(createElement(SampleChips, { projectId: 'p1', max: 4, inlineRoles: true }));
    expect(html).toContain('Стиль · palette.jpg');
    expect(html).not.toContain('data-sample-roles');
  });

  it('без inlineRoles (карточка над полосой без флага) выбор роли открывает Modal, строки под чипами нет', () => {
    const html = renderToStaticMarkup(createElement(SampleChips, { projectId: 'p1', max: 4, initialRoleFor: 's1' }));
    const modal = html.slice(html.indexOf('data-modal="Как модели использовать «palette.jpg»"'));
    expect(html).toContain('data-modal="Как модели использовать «palette.jpg»"');
    for (const r of ['Персонаж — сохранить лицо', 'Стиль', 'Предмет']) expect(modal).toContain(r);
    expect(html).not.toContain('data-sample-roles');
  });
});
