import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Окружение node: окно с matchMedia для хуков пустой ленты
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { registerSubsystem, SLOT_CONTEXT_KIND } = await import('../../../lib/subsystems/registryCore');
const { ChatEmptyState } = await import('../EmptyState');
const { plusButtonTitle, useCreateShortcuts } = await import('../composerShortcuts');

// Вид контекста с `create` («Звук») — единственный источник ярлыков «＋» и пустой ленты
registerSubsystem({
  key: 'test-kind', title: 'Тест', core: true,
  slots: {
    [SLOT_CONTEXT_KIND]: [{
      name: 'audio', order: 25,
      action: {
        kinds: ['audio'], icon: () => null, preview: () => null, actions: () => [],
        create: { title: 'Звук', hint: 'голос, музыка, обработка', icon: null, run: () => {} },
      },
    }],
  },
});

const empty = () => renderToStaticMarkup(createElement(ChatEmptyState, {
  hasProject: true, hasCLAUDEmd: true, onHint: () => {}, session: { id: 's1' } as never,
}));
function titles(projectId: string | null, sessionId: string | null = 's1'): string[] {
  let out: string[] = [];
  const Probe = () => { out = useCreateShortcuts(projectId, sessionId).map(s => s.title); return null; };
  renderToStaticMarkup(createElement(Probe));
  return out;
}

describe('ярлыки «＋» и пустой ленты от видов контекста', () => {
  it('пустая лента рисует одну кнопку «Звук», без «Голос» и «Музыка»', () => {
    const html = empty();
    expect(html).toContain('data-empty-shortcuts');
    expect(html).toContain('>Звук<');
    expect(html).not.toContain('>Голос<');
    expect(html).not.toContain('>Музыка<');
  });

  it('«Звук» в «＋» один — не два пункта (дефект 9de927dc)', () => {
    expect(titles('p1')).toEqual(['Звук']);
    expect(titles(null)).toEqual(['Звук']);
  });

  it('без чата ярлыков нет', () => {
    expect(titles('p1', null)).toEqual([]);
  });
});

describe('подсказка кнопки «＋»', () => {
  it('собирается из ярлыков, без ярлыков — только файл', () => {
    expect(plusButtonTitle([{ title: 'Звук' }])).toBe('Прикрепить файл, звук…');
    expect(plusButtonTitle([{ title: 'Видео' }, { title: 'Картинка' }])).toBe('Прикрепить файл, видео, картинка…');
    expect(plusButtonTitle([])).toBe('Прикрепить файл');
  });
});
