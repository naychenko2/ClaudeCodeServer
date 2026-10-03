import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';

// Без DOM: чипы строки читаем по data-атрибутам статической разметки

import { ContextRowView, rowFacts, showsRow, type ContextRowViewProps, type RowExec, type RowGit } from '../ContextRowView';
import type { ChatContextPrimary, ChatContextRef } from '../../../lib/chatContext/types';

const base = { id: 'x', ref: {}, addedAt: '', thumb: null, missing: false };
const primary = (over: Partial<ChatContextPrimary> = {}): ChatContextPrimary =>
  ({ ...base, id: 'p', kind: 'image', by: 'human', label: 'hero.png', version: 'v2', role: null, ...over });
const ref = (id: string, over: Partial<ChatContextRef> = {}): ChatContextRef =>
  ({ ...base, id, kind: 'image', by: 'human', label: id, version: null, role: 'style', usedBy: ['edit'], ...over });
const git: RowGit = {
  label: 'feat/video-editor', changes: 3, ahead: 1, publishN: 1,
  onCommitOwn: () => {}, onCommitAll: () => {}, onPublish: () => {}, onShowChanges: () => {},
};
const exec: RowExec = {
  rows: [{ id: 'auto', group: 'auto', name: 'Qwen-Image Edit', sub: 'локально', price: 'бесплатно · ~40 с', free: true, amount: null, unit: 'free', etaSeconds: 40 }],
  value: 'auto', onChange: () => {}, title: 'Чем выполнить',
};
const props = (over: Partial<ContextRowViewProps> = {}): ContextRowViewProps => ({
  width: 926, isMobile: false, git, primary: primary(), refs: [], exec: null, actionLabel: null,
  iconOf: () => null, offer: null, onUndo: () => {}, onRelease: () => {}, onDetach: () => {}, onClear: () => {}, ...over,
});
const html = (p: ContextRowViewProps) => renderToStaticMarkup(createElement(ContextRowView, p));
const has = (h: string, chip: string) => h.includes(`data-chip="${chip}"`);

describe('ContextRowView', () => {
  it('«Чем» в «Чате» не рисуется, при выбранном действии — есть', () => {
    expect(has(html(props({ exec: null })), 'exec')).toBe(false);
    expect(has(html(props({ exec })), 'exec')).toBe(true);
  });

  it('«Чем» не бывает без объекта', () => {
    expect(has(html(props({ primary: null, exec })), 'exec')).toBe(false);
  });

  it('личный чат без объекта: строки нет; с объектом — начинается с объекта, без ветки', () => {
    const empty = props({ git: null, primary: null });
    expect(showsRow(empty)).toBe(false);
    expect(html(empty)).toBe('');
    const h = html(props({ git: null }));
    expect(has(h, 'primary')).toBe(true);
    expect(has(h, 'git')).toBe(false);
  });

  it('проект без объекта: только чип ветки', () => {
    const h = html(props({ primary: null }));
    expect(has(h, 'git')).toBe(true);
    expect(has(h, 'primary')).toBe(false);
  });

  it('строка не форматирует подписи: label и version — ровно из DTO', () => {
    const h = html(props({ primary: primary({ label: 'Подпись из dto', version: 'версия из dto' }), refs: [ref('r1', { label: 'реф из dto' })] }));
    expect(h).toContain('>Подпись из dto<');
    expect(h).toContain('· версия из dto');
    expect(h).toContain('>реф из dto<');
    // версия без значения — ничего не выдумываем
    expect(html(props({ primary: primary({ version: null }) }))).not.toContain('· null');
  });

  it('✦ у объекта агента — отдельная кнопка; у человека её нет', () => {
    expect(html(props({ primary: primary({ by: 'agent' }) }))).toContain('data-agent-mark');
    expect(html(props())).not.toContain('data-agent-mark');
  });

  it('серый референс только при выбранном действии; в «Чате» серых нет', () => {
    const refs = [ref('voice', { usedBy: [] }), ref('style', { usedBy: ['edit'] })];
    const inChat = html(props({ refs, actionLabel: null }));
    expect(inChat).not.toContain('line-through');
    const withAction = html(props({ refs, actionLabel: 'Стемы' }));
    expect(withAction).toContain('line-through');
    expect(withAction).toContain('Не используется в операции «Стемы»');
  });

  it('серость считается по операции действия: голос при «Стемы» серый, а при «Озвучить» нет', () => {
    const voice = ref('voice', { usedBy: ['speak', 'dialogue', 'convertVoice'] });
    expect(html(props({ refs: [voice], actionLabel: 'Стемы', actionOp: 'separate' }))).toContain('line-through');
    expect(html(props({ refs: [voice], actionLabel: 'Озвучить', actionOp: 'speak' }))).not.toContain('line-through');
  });

  it('ступень лестницы: 360 px — прокрутка, 926 — всё на виду', () => {
    const many = props({ refs: [ref('a'), ref('b')], exec });
    expect(html({ ...many, width: 300 })).toContain('data-ladder-scroll="1"');
    expect(html({ ...many, width: 1200 })).toContain('data-ladder-scroll="0"');
    expect(html({ ...many, width: 1200 })).toContain('data-ladder-step="0"');
  });

  it('референсы, не влезшие в строку, уходят в «+N ›»', () => {
    const refs = [ref('a'), ref('b'), ref('c'), ref('d')];
    const h = html(props({ refs, exec, width: 620 }));
    expect(h.match(/data-chip="ref"/g)?.length ?? 0).toBeLessThan(4);
    expect(h).toContain('data-chip="more"');
  });

  it('телефон: ветка иконкой с бейджем, лестницы нет', () => {
    const h = html(props({ isMobile: true, width: 342 }));
    expect(h).toContain('data-ladder-scroll="1"');
    expect(h).not.toContain('>feat/video-editor<');
    expect(h).toContain('>3<');
  });

  it('плашка «Вернуть» после снятия объекта: текст по макету, отсчёт от срока из стора', () => {
    const h = html(props({ offer: { text: 'hero.png', until: Date.now() + 3200 } }));
    expect(h).toContain('Вернуть');
    expect(h).toContain('Выбор снят — поле снова «Чат»');
    expect(h).toContain('>4 с<');
    expect(html(props())).not.toContain('data-undo');
  });

  it('чип «Чем» берёт цену и тон из полей строки, а не из подписи price', () => {
    const cloud: RowExec = {
      ...exec, value: 'k',
      rows: [{ id: 'k', group: 'cloud', name: 'FLUX Kontext', price: 'бесплатно · но это подпись', free: false, amount: 0.04, unit: 'usd' }],
    };
    const h = html(props({ exec: cloud }));
    expect(h).toContain('$0.04');
    expect(h).not.toContain('бесплатно');
    // Тон «бесплатно» (зелёный) — от free, а не от слова в подписи
    expect(h).not.toContain('var(--c-success-bg)');
    const free = html(props({ exec }));
    expect(free).toContain('бесплатно');
    expect(free).toContain('var(--c-success-bg)');
  });

  it('факты лестницы собираются из модели', () => {
    expect(rowFacts(props({ refs: [ref('a')], exec }))).toEqual({ project: true, hasPrimary: true, hasExec: true, refs: 1, mobile: false });
  });
});
