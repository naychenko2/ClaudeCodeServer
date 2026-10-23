/* Фронтовый контракт prompt cache (F-1a): пустое состояние, расчёт остатка,
   переход warm → cooling → cold, подписи причин промаха. */
import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../types';
import type { PromptCacheWire } from './promptCache';
import { promptCacheState, missReasonLabel } from './promptCache';

// Якорь — условный «момент последнего касания кэша» (мс эпохи)
const ANCHOR = 1_700_000_000_000;
const MIN = 60_000;

const resultItem = (promptCache?: PromptCacheWire): ChatItem =>
  ({ kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1, promptCache } as ChatItem);

const textItem = (t: string): ChatItem => ({ kind: 'text', text: t } as ChatItem);

const cache = (over: Partial<PromptCacheWire> = {}): PromptCacheWire =>
  ({ readTokens: 1000, writtenTokens: 500, anchorAtMs: ANCHOR, ttl: '1h', missReason: 'none', ...over });

describe('promptCacheState — пустое состояние', () => {
  it('нет ни одного result — индикатора нет', () => {
    expect(promptCacheState([], ANCHOR)).toBeNull();
    expect(promptCacheState([textItem('привет')], ANCHOR)).toBeNull();
  });

  it('ни один result не несёт данных кэша — индикатора нет (сторонний провайдер, старая история)', () => {
    expect(promptCacheState([resultItem(), resultItem()], ANCHOR)).toBeNull();
  });

  it('последний result без кэша — индикатор по предыдущему с данными (ход на DeepSeek не гасит тёплый кэш)', () => {
    const items = [resultItem(cache()), resultItem()];
    const s = promptCacheState(items, ANCHOR + MIN);
    expect(s).not.toBeNull();
    expect(s!.readTokens).toBe(1000);
    expect(s!.remainingMs).toBe(59 * MIN);
  });

  it('result без якоря или без срока пропускается, а не гасит индикатор', () => {
    const items = [
      resultItem(cache()),
      resultItem(cache({ anchorAtMs: undefined })),
      resultItem(cache({ ttl: '30m' })),
    ];
    expect(promptCacheState(items, ANCHOR + MIN)?.readTokens).toBe(1000);
  });

  it('данные кэша есть, но ни разу не был якорь и срок — индикатора нет', () => {
    expect(promptCacheState([resultItem(cache({ anchorAtMs: undefined }))], ANCHOR)).toBeNull();
    expect(promptCacheState([resultItem(cache({ ttl: undefined }))], ANCHOR)).toBeNull();
    expect(promptCacheState([resultItem(cache({ ttl: '30m' }))], ANCHOR)).toBeNull();
  });
});

describe('promptCacheState — остаток и состояния', () => {
  it('часовой кэш сразу после якоря — тёплый, остаток = якорь + 1 ч − now', () => {
    const s = promptCacheState([resultItem(cache())], ANCHOR)!;
    expect(s.status).toBe('warm');
    expect(s.remainingMs).toBe(60 * MIN);
    expect(s.expiresAt).toBe(ANCHOR + 60 * MIN);
    expect(s.readTokens).toBe(1000);
    expect(s.writtenTokens).toBe(500);
  });

  it('остаток ровно порог (5 мин у часового) — остывает', () => {
    const s = promptCacheState([resultItem(cache())], ANCHOR + 55 * MIN)!;
    expect(s.status).toBe('cooling');
    expect(s.remainingMs).toBe(5 * MIN);
  });

  it('остаток выше порога — тёплый', () => {
    const s = promptCacheState([resultItem(cache())], ANCHOR + 54 * MIN)!;
    expect(s.status).toBe('warm');
    expect(s.remainingMs).toBe(6 * MIN);
  });

  it('остаток истёк — холодный, остаток не уходит в минус', () => {
    const s = promptCacheState([resultItem(cache())], ANCHOR + 61 * MIN)!;
    expect(s.status).toBe('cold');
    expect(s.remainingMs).toBe(0);
  });

  it('короткий TTL: порог — пятая доля срока (1 мин)', () => {
    const c = cache({ ttl: '5m' });
    expect(promptCacheState([resultItem(c)], ANCHOR + 3 * MIN)!.status).toBe('warm');
    expect(promptCacheState([resultItem(c)], ANCHOR + 4 * MIN)!.status).toBe('cooling');
    expect(promptCacheState([resultItem(c)], ANCHOR + 5 * MIN)!.status).toBe('cold');
  });

  it("стрим-дельты между result'ами состояние не меняют", () => {
    const items = [resultItem(cache()), textItem('…'), textItem('…')];
    expect(promptCacheState(items, ANCHOR + MIN)).toEqual(promptCacheState([resultItem(cache())], ANCHOR + MIN));
  });

  it('промах с причиной попадает в lastMiss, «none» — нет', () => {
    const s = promptCacheState([resultItem(cache({ missReason: 'systemChanged', missedTokens: 4200 }))], ANCHOR)!;
    expect(s.lastMiss).toEqual({ reason: 'systemChanged', tokens: 4200, label: 'смена системного промпта' });
    const s2 = promptCacheState([resultItem(cache({ missReason: 'none' }))], ANCHOR)!;
    expect(s2.lastMiss).toBeUndefined();
  });

  it('lastMiss — последний промах в чате, а не промах решающего result’а', () => {
    const items = [
      resultItem(cache({ missReason: 'systemChanged', missedTokens: 4200 })),
      resultItem(cache({ missReason: 'none' })),
    ];
    const s = promptCacheState(items, ANCHOR)!;
    expect(s.readTokens).toBe(1000);
    expect(s.lastMiss).toEqual({ reason: 'systemChanged', tokens: 4200, label: 'смена системного промпта' });
  });

  it('промах из result’а без якоря тоже учитывается', () => {
    const items = [
      resultItem(cache()),
      resultItem(cache({ anchorAtMs: undefined, missReason: 'modelChanged' })),
    ];
    const s = promptCacheState(items, ANCHOR)!;
    expect(s.lastMiss).toEqual({ reason: 'modelChanged', label: 'смена модели' });
  });
});

describe('missReasonLabel — подписи причин', () => {
  it('camelCase-причины, все подписи — именными фразами одного вида', () => {
    expect(missReasonLabel('previousMessageNotFound')).toBe('истечение срока кэша');
    expect(missReasonLabel('systemChanged')).toBe('смена системного промпта');
    expect(missReasonLabel('messagesChanged')).toBe('изменение истории сообщений');
    expect(missReasonLabel('modelChanged')).toBe('смена модели');
    expect(missReasonLabel('toolsChanged')).toBe('смена инструментов');
    expect(missReasonLabel('unavailable')).toBe('неизвестная причина');
  });

  it('незнакомая причина показывается как есть', () => {
    expect(missReasonLabel('systemChanged2' as never)).toBe('systemChanged2');
  });
});
