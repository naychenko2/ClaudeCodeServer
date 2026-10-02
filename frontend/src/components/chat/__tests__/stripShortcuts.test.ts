import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Окружение node: окно с matchMedia для хуков пустой ленты
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { registerSubsystem, SLOT_COMPOSER_STRIP } = await import('../../../lib/subsystems/registryCore');
const { ChatEmptyState } = await import('../EmptyState');
const { menuShortcuts, plusButtonTitle, useStripShortcuts } = await import('../ComposerStripHost');

// Полоса-вклад с ярлыком «Звук» (ключ — id полосы); доступность — рычагом теста
const lever = { available: true };
registerSubsystem({
  key: 'test-strip', title: 'Тест', core: true,
  slots: {
    [SLOT_COMPOSER_STRIP]: [{
      name: 'sound', order: 25, render: () => null,
      action: {
        title: 'Звук', icon: null,
        isAvailable: () => lever.available,
        shortcuts: () => [
          { key: 'sound', title: 'Звук', hint: 'голос, музыка, обработка', icon: null, onSelect: () => {} },
        ],
      },
    }],
  },
});

const empty = () => renderToStaticMarkup(createElement(ChatEmptyState, { hasProject: true, hasCLAUDEmd: true, onHint: () => {} }));
function titles(projectId: string | null): string[] {
  let out: string[] = [];
  const Probe = () => { out = useStripShortcuts(projectId, 's1').map(s => s.title); return null; };
  renderToStaticMarkup(createElement(Probe));
  return out;
}

describe('ярлыки полос вне меню полосы (ADR-021 п.1)', () => {
  it('пустая лента рисует одну кнопку «Звук», без «Голос» и «Музыка»', () => {
    lever.available = true;
    const html = empty();
    expect(html).toContain('data-empty-shortcuts');
    expect(html).toContain('>Звук<');
    expect(html).not.toContain('>Голос<');
    expect(html).not.toContain('>Музыка<');
  });

  it('недоступная полоса (флаг выключен) ярлыков не отдаёт ни ленте, ни «＋»', () => {
    lever.available = false;
    expect(empty()).not.toContain('data-empty-shortcuts');
    expect(titles('p1')).toEqual([]);
  });

  it('хук отдаёт ярлыки доступной полосы по порядку и в личном чате', () => {
    lever.available = true;
    expect(titles('p1')).toEqual(['Звук']);
    expect(titles(null)).toEqual(['Звук']);
  });
});

describe('меню полос', () => {
  const sound = { key: 'sound', title: 'Звук', icon: null, onSelect: () => {} };
  const video = { key: 'video-clip', title: 'Клип', icon: null, onSelect: () => {} };

  it('ярлык с ключом полосы — действие её пункта, а не второй пункт «Звук»', () => {
    const m = menuShortcuts(['git', 'sound'], [sound, video]);
    expect(m.shortcuts.map(s => s.title)).toEqual(['Клип']);
    expect(m.own('sound')).toBe(sound);
    expect(m.own('git')).toBeUndefined();
  });
});

describe('подсказка кнопки «＋»', () => {
  it('собирается из ярлыков полос, без ярлыков — только файл', () => {
    lever.available = true;
    let shortcuts: { title: string }[] = [];
    const Probe = () => { shortcuts = useStripShortcuts('p1', 's1'); return null; };
    renderToStaticMarkup(createElement(Probe));
    expect(plusButtonTitle(shortcuts)).toBe('Прикрепить файл, звук…');
    expect(plusButtonTitle([{ title: 'Видео' }])).toBe('Прикрепить файл, видео…');
    expect(plusButtonTitle([])).toBe('Прикрепить файл');
  });
});
