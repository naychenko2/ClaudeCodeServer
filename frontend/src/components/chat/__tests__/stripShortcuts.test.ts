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
const { plusButtonTitle, useStripShortcuts } = await import('../ComposerStripHost');

// Полоса-вклад с ярлыками «Голос» / «Музыка»; доступность — рычагом теста
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
          { key: 'sound-voice', title: 'Голос', hint: 'озвучить текст', icon: null, onSelect: () => {} },
          { key: 'sound-music', title: 'Музыка', hint: 'песня, кавер', icon: null, onSelect: () => {} },
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
  it('пустая лента рисует кнопки «Голос» и «Музыка»', () => {
    lever.available = true;
    const html = empty();
    expect(html).toContain('data-empty-shortcuts');
    expect(html).toContain('>Голос<');
    expect(html).toContain('>Музыка<');
  });

  it('недоступная полоса (флаг выключен) ярлыков не отдаёт ни ленте, ни «＋»', () => {
    lever.available = false;
    expect(empty()).not.toContain('data-empty-shortcuts');
    expect(titles('p1')).toEqual([]);
  });

  it('хук отдаёт ярлыки доступной полосы по порядку и в личном чате', () => {
    lever.available = true;
    expect(titles('p1')).toEqual(['Голос', 'Музыка']);
    expect(titles(null)).toEqual(['Голос', 'Музыка']);
  });
});

describe('подсказка кнопки «＋»', () => {
  it('собирается из ярлыков полос, без ярлыков — только файл', () => {
    lever.available = true;
    let shortcuts: { title: string }[] = [];
    const Probe = () => { shortcuts = useStripShortcuts('p1', 's1'); return null; };
    renderToStaticMarkup(createElement(Probe));
    expect(plusButtonTitle(shortcuts)).toBe('Прикрепить файл, голос, музыка…');
    expect(plusButtonTitle([{ title: 'Видео' }])).toBe('Прикрепить файл, видео…');
    expect(plusButtonTitle([])).toBe('Прикрепить файл');
  });
});
