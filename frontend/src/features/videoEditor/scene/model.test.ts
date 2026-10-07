import { describe, expect, it } from 'vitest';
import { CATALOG, PREFS, QUOTE, scene, version } from '../mocks';
import {
  executorRows, PERSONAL_LOCAL_REASON, priceLines, resolveScene, runReason, settingsOf, snapTo, staleNotes,
} from './model';

describe('модель сцены: цепочка настроек', () => {
  it('сцена старше правки, правка старше префов', () => {
    const r = resolveScene(scene('s1'), { durationSec: 6 }, PREFS, CATALOG);
    expect(r.model?.id).toBe('veo-3.1');
    expect(r.durationSec).toBe(6);
    expect(r.count).toBe(2);
    expect(r.sound).toBe(true);
  });

  it('без сцены — префы области; длительность прижимается к значениям модели', () => {
    const r = resolveScene(null, { durationSec: 7 }, PREFS, CATALOG);
    expect(r.provider?.key).toBe('fal');
    expect(r.durationSec).toBe(6);
    expect(snapTo([4, 6, 8], 9)).toBe(8);
  });

  it('модель без звука звук не включает, пропорции — из тех, что понимает модель', () => {
    const r = resolveScene(null, { model: 'seedance-2.5', aspect: '9:16', sound: true }, PREFS, CATALOG);
    expect(r.sound).toBe(false);
    expect(r.aspect).toBe('16:9');
  });

  it('«Авто» — поставщик не закреплён, в PUT сцены не уходит', () => {
    const r = resolveScene(null, null, {}, CATALOG);
    expect(r.auto).toBe(true);
    expect(settingsOf(r).provider).toBeUndefined();
  });
});

describe('причины «Снять» (тексты макета v7)', () => {
  const base = { sessionId: 's1', personal: false, providerOk: true, running: false };
  it('нет кадров, нет текста', () => {
    expect(runReason({ ...base, r: resolveScene(null, null, PREFS, CATALOG) })).toBe('Нужны оба кадра: выберите кадр A и кадр B выше');
    const noText = resolveScene(scene('s1'), { text: '  ' }, PREFS, CATALOG);
    expect(runReason({ ...base, r: noText })).toBe('Напишите текст сцены — что происходит от кадра A к кадру B');
  });
  it('нечем снимать — причина про поставщика, а не про кадры', () => {
    const r = resolveScene(null, null, PREFS, CATALOG);
    expect(runReason({ ...base, providerOk: false, providerReason: 'Снимать нечем', r })).toBe('Снимать нечем');
  });
  it('личный чат и локальные модели — причина видна', () => {
    const r = resolveScene(scene('s1'), { provider: 'local', model: 'minimax-h3' }, PREFS, CATALOG);
    expect(runReason({ ...base, personal: true, r })).toBe(PERSONAL_LOCAL_REASON);
  });
  it('всё на месте — снять можно', () => {
    expect(runReason({ ...base, r: resolveScene(scene('s1'), null, PREFS, CATALOG) })).toBeNull();
  });
});

describe('исполнитель и цена', () => {
  it('личный чат: local под замком, недоступный поставщик и модель без кадра B — серые с причиной', () => {
    const rows = executorRows(CATALOG, true, '');
    const local = rows.find(r => r.id === 'local|minimax-h3')!;
    expect(local).toMatchObject({ disabled: true, locked: true, reason: PERSONAL_LOCAL_REASON, group: 'local' });
    expect(rows.find(r => r.id === 'higgsfield|kling3_0')).toMatchObject({ disabled: true, reason: 'Нет доступа к Higgsfield' });
    expect(rows.find(r => r.id === 'fal|no-last')).toMatchObject({ disabled: true, reason: 'Не умеет «кадр A → кадр B»' });
    expect(rows.find(r => r.id === 'fal|seedance-2.5')!.disabled).toBeUndefined();
  });
  it('цена в две строки: итог и расшифровка; local — бесплатно с очередью', () => {
    expect(priceLines(QUOTE, 2, 8)).toEqual(['≈ $3.20', '2 × $1.60 за 8 с']);
    expect(priceLines({ ...QUOTE, price: { unit: 'credits', amount: 32, approx: false, source: 'get_cost' } }, 2, 8))
      .toEqual(['32 кредита', '2 × 16 кр за 8 с']);
    expect(priceLines({ ...QUOTE, count: 1, price: { unit: 'free', approx: false, source: 'local', eta: 1860, queueLength: 0 } }, 1, 5))
      .toEqual(['Бесплатно', '~31 мин · очередь GPU: 0']);
  });
});

describe('«изменено после съёмки»', () => {
  it('только у сцены с клипом', () => {
    const stale = { text: false, frameA: false, frameB: true, versionId: 'ver-1' };
    expect(staleNotes(scene('s1', { stale }))).toEqual([]);
    expect(staleNotes(scene('s1', { stale, versions: [version(1)] }))).toEqual(['Кадр B изменён после съёмки — переснимите']);
  });
});

describe('«Формат» сцены: желание и рамка кадров', () => {
  const wish = { mode: 'aspect' as const, aspect: '9:16', tier: 'max' as const, by: 'human' as const, at: '2026-10-07T00:00:00Z' };

  it('желание: правка старше сцены, сцена старше префов; в PUT уходят format и зеркало aspect', () => {
    const base = scene('s1');
    const r = resolveScene({ ...base, settings: { ...base.settings, format: { ...wish, aspect: '16:9' } } }, { format: wish, aspect: '9:16' }, PREFS, CATALOG);
    expect(r.format?.aspect).toBe('9:16');
    expect(r.aspect).toBe('9:16');
    expect(settingsOf(r)).toMatchObject({ format: wish, aspect: '9:16' });
  });

  it('без желания format в PUT не появляется, рамка по умолчанию — центр и в PUT не пишется', () => {
    const r = resolveScene(scene('s1'), null, PREFS, CATALOG);
    expect(r.format).toBeNull();
    expect(r.frameAnchor).toBe('center');
    const out = settingsOf(r);
    expect(out).not.toHaveProperty('format');
    expect(out).not.toHaveProperty('frameFit');
  });

  it('рамка подрезки кадров едет в PUT, когда не по центру', () => {
    const base = scene('s1');
    const r = resolveScene({ ...base, settings: { ...base.settings, frameFit: { anchor: 'left' } } }, null, PREFS, CATALOG);
    expect(settingsOf(r).frameFit).toEqual({ anchor: 'left' });
  });

  it('«Чем»: пропорции не серят модели (перевод на ближайшую делает «Формат»)', () => {
    expect(executorRows(CATALOG, false, '').find(x => x.id === 'fal|veo-3.1')?.disabled).toBeUndefined();
  });
});
