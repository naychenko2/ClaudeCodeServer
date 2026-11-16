// Тесты карточки «Движок» (EngineCard): строки нагрузки vLLM и ComfyUI, проценты из долей kv,
// пропуски как «нет данных» (не «0 %»), строка-причина падения скорости (danger/info),
// приглушение цифр и подпись «последние данные» при stale, строка движка для сводки (engineLine).
//
// Окружение node (без jsdom): renderToStaticMarkup — как HwTile/GpuDetailCard в hardware.test.ts.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { LocalModelMedia, LocalModelStats, MediaQueue, MediaRunning } from './types';
import { EngineCard, engineLine } from './EngineCard';

// === Фикстуры: снимок движка (числа макета v3: KV 42 %, префикс 67 %, 71.4 ток/с) ===

function vllmStats(over: Partial<LocalModelStats> = {}): LocalModelStats {
  return {
    configured: true,
    status: 'working',
    running: 3,
    waiting: 1,
    speed: { genPerStream: 71.4 },
    kv: { usagePerc: 0.42, prefixHitPerc: 0.67, preemptions15m: 2 },
    ...over,
  };
}

const runItem = (): MediaRunning => ({ kind: 'image', op: 'generate_image', heavy: false, own: true });
const queueItem = (position: number): MediaQueue => ({ position, kind: 'image', op: 'generate_image', heavy: false, own: false });

// 2 в работе, 3 в очереди — как в признаке готовности (у ComfyUI списки обязательны:
// пустой список это ноль, а не «нет данных»).
function comfyMedia(over: Partial<LocalModelMedia> = {}): LocalModelMedia {
  return {
    configured: true,
    comfy: { status: 'working', downSince: null, version: null },
    slots: { used: 2, limit: 4 },
    gpus: null,
    running: [runItem(), runItem()],
    queue: [1, 2, 3].map(queueItem),
    lastHour: [],
    ...over,
  };
}

const html = (props: Parameters<typeof EngineCard>[0]) => renderToStaticMarkup(createElement(EngineCard, props));
const count = (hay: string, needle: string) => hay.split(needle).length - 1;

// === 1. vLLM: строки нагрузки, доли kv → проценты ===

describe('EngineCard — vLLM', () => {
  it('заголовок и строки: «3 / 1», KV 42 %, префикс 67 %, скорость 71, вытеснения 2', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: null, stale: false });
    expect(h).toContain('Движок · vLLM · HyperQwen');
    expect(h).toContain('В работе / в очереди');
    expect(h).toContain('3 / 1');
    expect(h).toContain('KV-кэш');
    expect(h).toContain('42 %');
    // полоса KV-кэша — ui/ProgressBar с тем же числом
    expect(h).toContain('role="progressbar"');
    expect(h).toContain('aria-valuenow="42"');
    expect(h).toContain('Префикс-кэш, попадания');
    expect(h).toContain('67 %');
    expect(h).toContain('Скорость, ток/с');
    expect(h).toContain('71');
    expect(h).toContain('Вытеснения за 15 мин');
  });

  it('нет kv.prefixHitPerc — «нет данных», и ни одного «0 %» в разметке', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats({ kv: { usagePerc: 0.42, preemptions15m: 2 } }), throttled: null, stale: false });
    expect(h).toContain('Префикс-кэш, попадания');
    expect(h).toContain('нет данных');
    expect(count(h, '0 %')).toBe(0);
  });

  // running/waiting — одна строка: без второй половины состав не собрать, не «3 / —»
  it('нет одной половины парной строки — вся строка «нет данных»', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats({ waiting: undefined }), throttled: null, stale: false });
    expect(h).toContain('нет данных');
    expect(h).not.toContain('3 /');
  });

  it('stats=null — всё тело «нет данных», заголовок остаётся', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: null, throttled: null, stale: false });
    expect(h).toContain('Движок · vLLM · HyperQwen');
    expect(count(h, 'нет данных')).toBe(1);
    expect(h).not.toContain('3 / 1');
    expect(h).not.toContain('42 %');
  });

  it('без title — заголовок без пустого сегмента', () => {
    const h = html({ kind: 'vllm', title: '', stats: vllmStats(), throttled: null, stale: false });
    expect(h).toContain('Движок · vLLM');
    expect(h).not.toContain('vLLM · ');
  });
});

// === 2. ComfyUI: только «В работе / в очереди» ===

describe('EngineCard — ComfyUI', () => {
  it('2 в работе, 3 в очереди → «2 / 3»', () => {
    const h = html({ kind: 'comfy', media: comfyMedia(), throttled: null, stale: false });
    expect(h).toContain('Движок · ComfyUI');
    expect(h).toContain('В работе / в очереди');
    expect(h).toContain('2 / 3');
  });

  // пустые списки — обязательные поля ответа: это настоящий ноль, а не пропуск данных
  it('пустые running/queue — «0 / 0», не «нет данных»', () => {
    const h = html({ kind: 'comfy', media: comfyMedia({ running: [], queue: [] }), throttled: null, stale: false });
    expect(h).toContain('0 / 0');
    expect(h).not.toContain('нет данных');
  });

  it('media=null — «нет данных»', () => {
    const h = html({ kind: 'comfy', media: null, throttled: null, stale: false });
    expect(h).toContain('Движок · ComfyUI');
    expect(count(h, 'нет данных')).toBe(1);
    expect(h).not.toContain('2 / 3');
  });
});

// === 3. Строка-причина падения скорости ===

describe('EngineCard — строка-причина', () => {
  it('thermal — «Скорость упала» на dangerBg/dangerText', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: 'thermal', stale: false });
    expect(h).toContain('Скорость упала: карта сбрасывает частоту от перегрева');
    expect(h).toContain('var(--c-danger-bg)');
    expect(h).toContain('var(--c-danger-text)');
  });

  // по решению дизайна (spec.md:56, макеты «p-info»): информационная плашка, не warning
  it('powerCap — «Упирается в лимит мощности» на infoBg/info, без «Скорость упала»', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: 'powerCap', stale: false });
    expect(h).toContain('Упирается в лимит мощности');
    expect(h).not.toContain('Скорость упала');
    expect(h).toContain('var(--c-info-bg)');
    expect(h).toContain('var(--c-info)');
    expect(h).not.toContain('var(--c-warning-bg)');
    expect(h).not.toContain('var(--c-danger-bg)');
  });

  it('throttled=null — строки-причины нет вовсе', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: null, stale: false });
    expect(count(h, 'Скорость упала')).toBe(0);
  });

  // метрики скорости есть только у vLLM: причина к ComfyUI не относится
  it('у ComfyUI строки-причины нет', () => {
    const h = html({ kind: 'comfy', media: comfyMedia(), throttled: 'thermal', stale: false });
    expect(count(h, 'Скорость упала')).toBe(0);
  });
});

// === 4. stale: цифры приглушены + подпись «последние данные · HH:MM:SS» ===

describe('EngineCard — stale', () => {
  // локальные часы: 2026-11-16 09:05:07 — тест не зависит от таймзоны машины
  const since = new Date(2026, 10, 16, 9, 5, 7).getTime();

  it('свежие данные — значение textPrimary, подписи нет', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: null, stale: false });
    expect(h).toContain('color:var(--c-text-primary)">3 / 1');
    expect(h).not.toContain('последние данные');
  });

  it('stale — значение textMuted и строка «последние данные · 09:05:07»', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: null, stale: true, since });
    expect(h).toContain('последние данные · 09:05:07');
    expect(h).toContain('color:var(--c-text-muted)">3 / 1');
  });

  it('stale без since — подпись без времени', () => {
    const h = html({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: null, stale: true });
    expect(h).toContain('последние данные');
    expect(h).not.toContain('последние данные ·');
  });
});

// === 5. engineLine: строка движка для карточки узла в сводке ===

describe('engineLine', () => {
  it('vLLM 71.4 ток/с, KV 0.42 → «71 ток/с · KV 42 %»', () => {
    expect(engineLine({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats(), throttled: null, stale: false }))
      .toBe('71 ток/с · KV 42 %');
  });

  it('ComfyUI 2 в работе, 3 в очереди → «очередь 3 · идёт 2»', () => {
    expect(engineLine({ kind: 'comfy', media: comfyMedia(), throttled: null, stale: false }))
      .toBe('очередь 3 · идёт 2');
  });

  it('нет данных — та же формулировка, что в карточке', () => {
    expect(engineLine({ kind: 'vllm', title: 'HyperQwen', stats: null, throttled: null, stale: false })).toBe('нет данных');
    expect(engineLine({ kind: 'vllm', title: 'HyperQwen', stats: vllmStats({ speed: undefined }), throttled: null, stale: false })).toBe('нет данных');
    expect(engineLine({ kind: 'comfy', media: null, throttled: null, stale: false })).toBe('нет данных');
  });
});
