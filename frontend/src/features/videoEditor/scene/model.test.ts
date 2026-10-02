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
    const rows = executorRows(CATALOG, true, '16:9', '');
    const local = rows.find(r => r.id === 'local|minimax-h3')!;
    expect(local).toMatchObject({ disabled: true, locked: true, reason: PERSONAL_LOCAL_REASON, group: 'local' });
    expect(rows.find(r => r.id === 'higgsfield|kling3_0')).toMatchObject({ disabled: true, reason: 'Нет доступа к Higgsfield' });
    expect(rows.find(r => r.id === 'fal|no-last')).toMatchObject({ disabled: true, reason: 'Не умеет «кадр A → кадр B»' });
    expect(rows.find(r => r.id === 'fal|seedance-2.5')!.disabled).toBeUndefined();
    expect(executorRows(CATALOG, false, '9:16', '').find(r => r.id === 'fal|seedance-2.5')).toMatchObject({ reason: 'Не снимает в пропорциях 9:16' });
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
