// Тело «Формата» (ADR-025): что рисуется по ответу ручки. Окружение node: рисуем в строку.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { FormatBody } = await import('./FormatBody');
type Model = import('../../../lib/chatContext/formatTypes').FormatModel;
type State = import('../../../lib/chatContext/formatTypes').MediaFormatState;

const GREY = 'Модель рисует один из трёх размеров около 1,6 Мп — «Высокое» и «Макс» у неё недоступны';
const now = '2026-10-06T12:00:00Z';
const gpt: State = {
  caps: { modelLabel: 'GPT Image', aspects: ['1:1', '3:2', '2:3'], tiers: [] },
  wish: { mode: 'aspect', aspect: '16:9', tier: 'high', by: 'human', at: now },
  resolved: { mode: 'aspect', aspect: '3:2', tier: 'standard', width: 1536, height: 1024, substituted: true, rounded: false, note: '16:9 нет у GPT Image — взяли 3:2.' },
  tiers: [
    { tier: 'standard', label: '1 Мп', size: { w: 1536, h: 1024 }, price: { amount: 0.04, unit: 'usd' } },
    { tier: 'high', label: 'нет у модели', disabledReason: GREY },
    { tier: 'max', label: 'нет у модели', disabledReason: GREY },
  ],
  exactUnavailableReason: '«Точно…» недоступно: GPT Image рисует только три размера — 1024×1024, 1536×1024 и 1024×1536.',
};
const model = (state: State | null, over: Partial<Model> = {}): Model => ({
  kind: 'generate', title: 'Формат новой картинки', state, by: null, chip: { main: '3:2', tone: 'normal' },
  footer: p => `Запомнится для картинок проекта «${p}».`, onChange: () => {}, ...over,
});
const html = (m: Model) => renderToStaticMarkup(createElement(FormatBody, { m, isMobile: false, projectName: 'AI Home', onOpenExec: () => {} }));

describe('тело «Формата»', () => {
  it('подмена: пояснение сервера, активная пропорция — действующая (3:2), а не желание', () => {
    const h = html(model(gpt));
    expect(h).toContain('16:9 нет у GPT Image — взяли 3:2.');
    expect(h).toMatch(/data-fmt-aspect="3:2" aria-pressed="true"/);
    expect(h).toMatch(/data-fmt-aspect="1:1" aria-pressed="false"/);
  });

  it('серые ступени — не кнопки, с причиной; «Точно…» недоступно с текстом сервера', () => {
    const h = html(model(gpt));
    expect(h).toMatch(/<div data-fmt-tier="high" data-fmt-off=""/);
    expect(h).toMatch(/<button[^>]*data-fmt-tier="standard"/);
    expect(h).toContain(GREY);
    expect(h).toContain('«Точно…» недоступно: GPT Image рисует только три размера');
    expect(h).not.toContain('data-fmt-exact-open');
  });

  it('цена и размер под ступенью — из ответа ручки', () => {
    const h = html(model(gpt));
    expect(h).toContain('1536×1024');
    expect(h).toContain('$0.04');
  });

  it('модель без размеров: «Как у модели», причина и ссылка на «Чем», пропорций нет', () => {
    const h = html(model({ caps: null, wish: null, resolved: { mode: 'model', substituted: false, rounded: false }, tiers: [] },
      { hint: 'Higgsfield не сообщил, какие размеры умеет Soul, — рисуем её размером по умолчанию.' }));
    expect(h).toContain('Higgsfield не сообщил, какие размеры умеет Soul');
    expect(h).toContain('Сменить модель в «Чем»');
    expect(h).not.toContain('data-fmt-aspect');
  });

  it('только чтение («Увеличить»): пояснение вместо органов управления', () => {
    const h = html(model(null, { kind: 'readonly', readonlyNote: 'У «Увеличить» размер задаёт сама операция. Пропорции не меняются.', footer: () => '' }));
    expect(h).toContain('У «Увеличить» размер задаёт сама операция');
    expect(h).not.toContain('data-fmt-aspect');
    expect(h).not.toContain('data-fmt-foot');
  });

  it('низ: проект подставляет хост, у формата агента — «поставил Claude ✦»', () => {
    expect(html(model(gpt))).toContain('Запомнится для картинок проекта «AI Home».');
    const agent = model(gpt, { footer: p => `Запомнится для картинок проекта «${p}» · поставил Claude ✦.` });
    expect(html(agent)).toContain('поставил Claude ✦');
  });
});
