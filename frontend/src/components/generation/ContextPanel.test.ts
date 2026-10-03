// Панель «Контекст» (ADR-023 §Д1): секции, пустые состояния и закрытый набор параметров.
// Окружение node: рисуем в строку, как тест каркаса GenerationPanel.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.mock('../../pages/workspace/panelFill', () => ({ useRequestPanelFill: () => {} }));

const { ContextPanel, EMPTY } = await import('./ContextPanel');
type Props = import('./ContextPanel').ContextPanelProps;
const { stubActionRun } = await import('../../lib/chatContext/actionRunStub');

const base = { id: 'p1', kind: 'image', ref: { threadId: 't', versionId: 'v2' }, by: 'human' as const, addedAt: '2026-10-03T10:00:00Z', label: 'hero.png', version: 'v2 из 2 · 1024×768', thumb: null, missing: false };
const primary = { ...base, role: null };
const ref = (id: string, label: string, usedBy: string[] = ['edit']) => ({ ...base, id, ref: { path: label }, label, version: null, role: 'style', usedBy });
const edit = { id: 'edit', kind: 'run' as const, label: 'Изменить', hint: 'Изменить картинку', op: 'edit' };
const row = { id: 'auto', group: 'auto' as const, name: 'Авто', sub: 'локально', price: 'бесплатно · ~40 с' };

const panel = (o: Partial<Props> = {}) => renderToStaticMarkup(createElement(ContextPanel, {
  isMobile: false, git: null, primary: null, refs: [], iconOf: () => null, preview: null, editor: null, step: null,
  ret: null, onReturn: () => {}, action: null, exec: null, params: [], onParam: () => {}, addFrom: [],
  run: stubActionRun(null), flash: 0, onRelease: () => {}, onDetach: () => {}, onClear: () => {}, layout: 'column', ...o,
}));

const git = { label: 'feat/video-editor', changes: 3, ahead: 1, publishN: 1, onCommitOwn: () => {}, onCommitAll: () => {}, onPublish: () => {}, onShowChanges: () => {} };

describe('секции панели «Контекст»', () => {
  it('пустая панель: пустые состояния «С чем», «Чем», «Плюс», «Параметры»; «Где» без проекта нет', () => {
    const html = panel();
    expect(html).toContain(EMPTY.primary);
    expect(html).toContain(EMPTY.execNoObject);
    expect(html).toContain(EMPTY.refs);
    expect(html).toContain(EMPTY.paramsChat);
    expect(html).not.toContain('data-ctx-section="where"');
    expect(html).toContain('aria-label="Контекст"');
  });

  it('в проекте «Где» показывает ветку и число изменений', () => {
    const html = panel({ git });
    expect(html).toContain('data-ctx-section="where"');
    expect(html).toContain('feat/video-editor');
    expect(html).toContain('3 изменения');
  });

  it('«С чем» рисует подписи из DTO без форматирования; в «Чате» «Чем» — пустое состояние Р2', () => {
    const html = panel({ primary });
    expect(html).toContain('hero.png');
    expect(html).toContain('v2 из 2 · 1024×768');
    expect(html).toContain(EMPTY.execChat);
    expect(html).toContain(EMPTY.footChat);
  });

  it('«Открыть редактор» и ссылка «назад» берутся из вида и ContextReturn', () => {
    const html = panel({
      primary, editor: { label: 'Открыть редактор', hint: 'маска и «Без ИИ»', open: () => {} },
      ret: { prev: { ...primary, id: 'p0' }, label: 'К сцене «Утро»' },
    });
    expect(html).toContain('Открыть редактор');
    expect(html).toContain('маска и «Без ИИ»');
    expect(html).toContain('К сцене «Утро»');
  });

  it('стрелки версий есть только когда вид их отдал', () => {
    expect(panel({ primary })).not.toContain('Предыдущая версия');
    expect(panel({ primary, step: { prev: () => {}, next: null } })).toContain('Предыдущая версия');
  });

  it('при выбранном действии «Чем» — сводка исполнителя, серый референс — с причиной', () => {
    const html = panel({
      primary, action: edit, run: stubActionRun(edit),
      exec: { rows: [row], value: 'auto', onChange: () => {} },
      refs: [ref('r1', 'Аня'), ref('r2', 'palette.png', [])],
    });
    expect(html).toContain('Чем · для «Изменить»');
    expect(html).toContain('Авто');
    expect(html).toContain('data-ctx-ref="gray"');
    expect(html).toContain('не используется в «Изменить»');
    expect(html).toContain('✦ Изменить');
  });

  it('в «Чате» серых референсов нет', () => {
    const html = panel({ primary, refs: [ref('r1', 'Аня', [])] });
    expect(html).not.toContain('data-ctx-ref="gray"');
  });
});

describe('параметры запуска — закрытый набор', () => {
  it('известные kind рисуются', () => {
    const html = panel({
      primary, action: edit,
      params: [
        { kind: 'variants', min: 1, max: 4, value: 3 },
        { kind: 'duration', options: [4, 6, 8], value: 8 },
        { kind: 'aspect', options: ['16:9', '9:16'], value: '16:9' },
        { kind: 'fromQuestion', label: '9:16 · выбирается под чипами' },
      ],
    });
    for (const k of ['variants', 'duration', 'aspect', 'fromQuestion']) expect(html).toContain(`data-ctx-param="${k}"`);
    expect(html).toContain('9:16 · выбирается под чипами');
  });

  it('неизвестный kind не рисуется, а пустой набор даёт «параметров нет»', () => {
    const alien = { kind: 'seed', value: 7 } as unknown as Props['params'][number];
    const html = panel({ primary, action: edit, params: [alien] });
    expect(html).not.toContain('data-ctx-param');
    expect(html).not.toContain('seed');
    expect(html).toContain(EMPTY.paramsNone('Изменить'));
  });
});
