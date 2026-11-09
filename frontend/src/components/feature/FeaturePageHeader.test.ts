// Единая тревога страницы фичи (Ш4): слова и тон — из alarmView (F2), «молчит/стоит/тихо» —
// из analysisLineState. Чистый рендер (renderToStaticMarkup): без DOM и сторов.
//
// Мутации (в отчёте): (1) заменить tone на свой switch по attention.kind — краснеет тест
// «тон из alarmView»; (2) убрать ветку silent — краснеют «стоит»/«тихо»; (3) вернуть
// «ждёт вашего ответа» в head — краснеет «слова alarmView».

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFileSync } from 'node:fs';
import { describe, it, expect, vi } from 'vitest';
import { FeaturePageAlert } from './FeaturePageHeader';
import { alarmView } from '../chat/feature/featureTruthModel';
import type { FeatureAttention, FeatureRun } from '../../types/featureRun';

vi.mock('../../lib/api', () => ({ api: { personas: { avatarUrl: () => '' } } }));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return {
    ...orig,
    usePersonas: () => [],
    usePersonasVersion: () => 0,
    getPersonaById: (id: string) => (id === 'p-kira' ? { id, name: 'Кира' } : undefined),
    ensurePersonasLoaded: async () => {},
  };
});

const HOUR = 3_600_000;

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running', epic: { id: 'f1', title: 'Фича' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    ...over,
  } as FeatureRun;
}

function att(kind: FeatureAttention['kind'], over: Partial<FeatureAttention> = {}): FeatureAttention {
  return {
    kind, taskId: 't1', since: new Date(Date.now() - 30 * 60000).toISOString(),
    reason: null, personaId: 'p-kira', linkedSessionId: 'chat-kira', ...over,
  };
}

const html = (run: FeatureRun, isMobile = false) =>
  renderToStaticMarkup(createElement(FeaturePageAlert, { run, feature: null, isMobile }));
const tone = (h: string) => /data-tone="([^"]*)"/.exec(h)?.[1] ?? null;
const text = (h: string) => h.replace(/<[^>]*>/g, '');

describe('FeaturePageAlert: слова и тон — alarmView', () => {
  it('тон из alarmView: wait/merge — warn, stopped — stop', () => {
    expect(tone(html(mkRun({ state: 'waiting', attention: att('wait') })))).toBe(alarmView('waiting', null, 'Кира', false).tone);
    expect(tone(html(mkRun({ state: 'merge_pending', attention: att('merge') })))).toBe(alarmView('merge_pending', null, null, false).tone);
    expect(tone(html(mkRun({ state: 'stopped', attention: att('stopped') })))).toBe(alarmView('stopped', null, 'Кира', false).tone);
    expect(tone(html(mkRun({ state: 'waiting', attention: att('wait') })))).toBe('warn');
    expect(tone(html(mkRun({ state: 'stopped', attention: att('stopped') })))).toBe('stop');
  });

  it('слова alarmView + длительность; старой фразы «ждёт вашего ответа» нет', () => {
    const t = text(html(mkRun({ state: 'waiting', attention: att('wait', { reason: 'уточни отступы' }) })));
    expect(t).toContain('Ждёт вас · уточни отступы · 30 мин');
    expect(t).not.toContain('ждёт вашего ответа');
    expect(text(html(mkRun({ state: 'stopped', attention: att('stopped') })))).toContain('Стоит · Кира · 30 мин');
    expect(text(html(mkRun({ state: 'merge_pending', attention: att('merge') })))).toContain('Ждёт согласия на мерж');
  });

  it('360: причина ждущего вопроса не во фразе (alarmView даёт имя), а второй строкой', () => {
    const t = text(html(mkRun({ state: 'waiting', attention: att('wait', { reason: 'уточни отступы' }) }), true));
    expect(t).toContain('Ждёт вас · Кира · 30 мин');
    expect(t).toContain('уточни отступы');
  });
});

describe('FeaturePageAlert: «молчит/стоит/тихо» сторожа переехали сюда', () => {
  it('stuck — «стоит 3 ч», тон stop; nudged — «молчит», warn; quiet — «тихо», warn', () => {
    const now = Date.now();
    const stuck = mkRun({
      state: 'stopped', lastActivityAt: new Date(now - 3 * HOUR).toISOString(),
      now: [{ taskId: 't-1', stage: 'prod', title: 'Шаг', stepRef: 'Ш1', staleAlertedAt: new Date(now - 24 * HOUR).toISOString() }],
    } as Partial<FeatureRun>);
    const h1 = html(stuck);
    expect(tone(h1)).toBe('stop');
    expect(text(h1)).toContain('стоит 3 ч');
    expect(text(h1)).toContain('Ш1 без движения');

    const nudged = mkRun({
      now: [{ taskId: 't-1', stage: 'prod', title: 'Шаг', stepRef: 'Ш1', nudgedAt: new Date(now - 3 * HOUR).toISOString() }],
    } as Partial<FeatureRun>);
    const h2 = html(nudged);
    expect(tone(h2)).toBe('warn');
    expect(text(h2)).toContain('молчит 3 ч');

    const quiet = mkRun({ quiet: true, lastActivityAt: new Date(now - 24 * HOUR).toISOString() });
    const h3 = html(quiet);
    expect(tone(h3)).toBe('warn');
    expect(text(h3)).toContain('тихо 24 ч');
  });

  it('attention важнее сторожа: wait + quiet → «Ждёт вас», не «тихо»', () => {
    const run = mkRun({
      state: 'waiting', quiet: true, lastActivityAt: new Date(Date.now() - 24 * HOUR).toISOString(),
      attention: att('wait', { reason: 'уточни отступы' }),
    });
    const t = text(html(run));
    expect(t).toContain('Ждёт вас');
    expect(t).not.toContain('тихо');
    expect(tone(html(run))).toBe('warn');
  });

  it('тревоги нет: идёт задача / смержена', () => {
    const now = Date.now();
    const moving = mkRun({ now: [{ taskId: 't-1', stage: 'prod', title: 'Шаг', startedAt: new Date(now - 30 * 60000).toISOString() }] } as Partial<FeatureRun>);
    expect(html(moving)).toBe('');
    expect(html(mkRun({ state: 'merged' }))).toBe('');
  });

  it('не начата — нейтральный блок, не тревога', () => {
    expect(tone(html(mkRun({ state: 'not_started' })))).toBe('neutral');
  });
});

// ── Ш9a: кнопка главного действия в тревоге на 360 читаема в обеих темах ────────────
describe('FeaturePageAlert 360: контраст кнопки главного действия', () => {
  const css = readFileSync(new URL('../../lib/theme.css', import.meta.url), 'utf8');
  const block = (head: string) => css.slice(css.indexOf(head), css.indexOf('}', css.indexOf(head)));
  const tokenOf = (b: string, name: string) => new RegExp(`--${name}:\\s*(#[0-9A-Fa-f]{6})`).exec(b)![1];
  const lum = (hex: string) => {
    const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255)
      .map(v => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const ratio = (a: string, b: string) => {
    const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p);
    return (x + 0.05) / (y + 0.05);
  };

  it('кнопка берёт пару фон/текст из токенов (не accent-фон с bgWhite-перебивкой)', () => {
    const h = html(mkRun({ state: 'waiting', attention: att('wait') }), true);
    const btn = /<button[^>]*>/.exec(h)![0];
    const style = /style="([^"]*)"/.exec(btn)![1];
    expect(style).toContain('background:var(--c-bg-white)');
    expect(style).toContain('color:var(--c-text-secondary)');
    expect(style).not.toContain('--c-on-accent');
  });

  it('пара bgWhite/textSecondary: контраст ≥ 4.5 в светлой и тёмной теме', () => {
    for (const head of [':root {', ':root[data-theme="dark"] {']) {
      const b = block(head);
      expect(ratio(tokenOf(b, 'c-bg-white'), tokenOf(b, 'c-text-secondary')), head).toBeGreaterThanOrEqual(4.5);
    }
  });
});
