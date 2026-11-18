// Подключение вкладки «Встречи» к панели (Ф2): видимость вкладки (админ/не-админ),
// запоминание выбора (cc_local_model_tab), точка на вкладке по level из снимка,
// «узкая лента» (<340 px — неактивные вкладки только иконкой).
//
// Плюс Ф6: точка на «Стендах» и доступное имя панели «Ферма».
//
// Окружение node (без jsdom): чистые функции — прямыми вызовами, переключатель —
// renderToStaticMarkup (паттерн hardware.test.ts), имя панели — сторожем по исходнику.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFileSync } from 'node:fs';
import { afterEach, describe, expect, it } from 'vitest';
import type { TranscriptionLevel, TranscriptionSnapshot } from './types';
import { loadTab, resolveTab, transcriptionDot, TabSwitch } from './LocalModelPanel';

// === 1. Видимость вкладки: админ / не-админ / сохранённая transcription ===

describe('resolveTab — видимость вкладки «Встречи»', () => {
  it('админ: сохранённая transcription → transcription', () => {
    expect(resolveTab(true, 'transcription')).toBe('transcription');
  });
  it('не-админ + сохранённая transcription → text (недоступную не показываем)', () => {
    // Тот же порядок, что с 'hardware': вкладка только для админа.
    expect(resolveTab(false, 'transcription')).toBe('text');
  });
});

// === 2. Выбор вкладки помнится (тот же TAB_KEY, значение 'transcription') ===

describe('loadTab — запоминание вкладки «Встречи»', () => {
  afterEach(() => { delete (globalThis as { localStorage?: unknown }).localStorage; });
  it('cc_local_model_tab=transcription → transcription', () => {
    (globalThis as unknown as { localStorage: unknown }).localStorage = { getItem: () => 'transcription', setItem: () => {} };
    expect(loadTab()).toBe('transcription');
  });
  it('чужого значения нет → text (прежний дефолт)', () => {
    (globalThis as unknown as { localStorage: unknown }).localStorage = { getItem: () => 'nope', setItem: () => {} };
    expect(loadTab()).toBe('text');
  });
});

// === 3. Точка на вкладке: только показываем level из снимка (считает бэкенд) ===

function snap(level?: TranscriptionLevel): TranscriptionSnapshot {
  return { phase: 'idle', updatedAt: 1_763_000_000_000, ...(level ? { level } : {}) };
}

describe('transcriptionDot — точка по level снимка', () => {
  it('снимка нет / level ok / поля нет — точки нет', () => {
    expect(transcriptionDot(null)).toBeNull();
    expect(transcriptionDot(snap('ok'))).toBeNull();
    expect(transcriptionDot(snap())).toBeNull();
  });
  it('warn → warn (жёлтая), danger → danger (красная)', () => {
    expect(transcriptionDot(snap('warn'))).toBe('warn');
    expect(transcriptionDot(snap('danger'))).toBe('danger');
  });
});

// === 4. Переключатель: «Встречи» после «Стендов», точка, узкая лента ===

function switcherHtml(over: {
  value?: 'text' | 'media' | 'hardware' | 'transcription';
  dotLevel?: 'warn' | 'danger';
  transDotLevel?: TranscriptionLevel;
  narrow?: boolean;
} = {}): string {
  return renderToStaticMarkup(createElement(TabSwitch, {
    value: over.value ?? 'text',
    onChange: () => {},
    isMobile: false,
    ...(over.dotLevel ? { dotLevel: over.dotLevel } : {}),
    ...(over.transDotLevel ? { transDotLevel: over.transDotLevel } : {}),
    ...(over.narrow ? { narrow: true } : {}),
  }));
}

describe('TabSwitch — вкладка «Встречи»', () => {
  it('«Встречи» есть и идёт после «Стенды»', () => {
    const h = switcherHtml();
    expect(h).toContain('Встречи');
    expect(h.indexOf('Стенды')).toBeLessThan(h.indexOf('Встречи'));
  });
  it('danger — красная точка (C.danger); warn — жёлтая (C.warning); без уровня — точки нет', () => {
    expect(switcherHtml({ transDotLevel: 'danger' })).toContain('var(--c-danger)');
    expect(switcherHtml({ transDotLevel: 'warn' })).toContain('var(--c-warning)');
    // «Стенды» (dotLevel через tabIcon) — тот же контроль, что у «Встреч»: без него
    // точка этой вкладки вообще ничем не была покрыта (хвост ревью R-Ф2).
    expect(switcherHtml({ dotLevel: 'danger' })).toContain('var(--c-danger)');
    expect(switcherHtml({ dotLevel: 'warn' })).toContain('var(--c-warning)');
    expect(switcherHtml()).not.toContain('var(--c-danger)');
    expect(switcherHtml()).not.toContain('var(--c-warning)');
  });
  it('узкая лента (<340 px): неактивные — только иконка (aria-label + title), активная — с подписью', () => {
    const h = switcherHtml({ narrow: true });
    // Видимых подписей неактивных нет (текстовый узел «>Медиа<»), имя — в aria-label/title.
    expect(h).not.toContain('>Медиа<');
    expect(h).not.toContain('>Стенды<');
    expect(h).not.toContain('>Встречи<');
    expect(h).toContain('aria-label="Медиа"');
    expect(h).toContain('title="Встречи"');
    // Активная («LLM») остаётся с подписью.
    expect(h).toContain('>LLM<');
  });
});

// === 5. Доступное имя панели — то же, что видимый заголовок (Ф6, хвост Ф5) ===
// Сторожем по исходнику (паттерн §6 HardwareTab.test.ts): рендер самой панели
// потребовал бы всех её хуков и опросов, а проверяемое — одна строка атрибута.
describe('имя панели для читателей с экрана — «Ферма»', () => {
  it('aria-label панели «Ферма»; прежнего имени в исходнике нет', () => {
    const src = readFileSync(new URL('./LocalModelPanel.tsx', import.meta.url), 'utf8');
    expect(src).toContain('aria-label="Ферма"');
    expect(src).not.toContain('aria-label="Локальные модели"');
  });
});
