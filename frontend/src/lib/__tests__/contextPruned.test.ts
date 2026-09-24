import { describe, it, expect } from 'vitest';
import type { ChatItem, ServerMessage } from '../../types';
import {
  prunedHeadline, prunedDetails, cachePct, summarizePruned, prunedSummaryText,
  type PrunedItem,
} from '../contextPruned';
import { applyServerMessage, initialChatState } from '../chatReducer';
import { estimateContext } from '../context';

const prune = (over: Partial<PrunedItem> = {}): PrunedItem => ({
  kind: 'context_pruned', pruneKind: 'prune',
  tokensBefore: 171_000, tokensAfter: 113_000,
  blocks: 25, resultBlocks: 20, inputBlocks: 3, thinkingBlocks: 2,
  prefillSeconds: 87, cacheReadTokens: 0, promptTokens: 113_000,
  ...over,
});

describe('prunedHeadline', () => {
  it('обрезка — объём до и после', () => {
    expect(prunedHeadline(prune())).toBe('контекст обрезан · 171k → 113k');
  });

  it('известен только объём до — показываем его одного', () => {
    expect(prunedHeadline(prune({ tokensAfter: 0 }))).toBe('контекст обрезан · было 171k');
  });

  it('объёмов нет вовсе — голый заголовок без хвоста', () => {
    expect(prunedHeadline(prune({ tokensBefore: 0, tokensAfter: 0 }))).toBe('контекст обрезан');
  });

  it('сжатие в облаке — секунды, а не объём', () => {
    expect(prunedHeadline(prune({ pruneKind: 'compact_cloud', prefillSeconds: 21.4 })))
      .toBe('сжатие в облаке · 21 с');
  });

  it('сжатие в облаке без замера — без хвоста', () => {
    expect(prunedHeadline(prune({ pruneKind: 'compact_cloud', prefillSeconds: undefined })))
      .toBe('сжатие в облаке');
  });

  it('тысячи ниже 10k — с десятой долей', () => {
    expect(prunedHeadline(prune({ tokensBefore: 9500, tokensAfter: 940 })))
      .toBe('контекст обрезан · 9.5k → 940');
  });
});

describe('prunedDetails', () => {
  it('полная строка: блоки с разбивкой, пересчёт, доля кэша', () => {
    expect(prunedDetails(prune()))
      .toBe('25 блоков (выводов 20, входов 3, размышлений 2) · пересчёт 87 с · из кэша 0%');
  });

  it('виды с нулём не перечисляются', () => {
    expect(prunedDetails(prune({ blocks: 20, resultBlocks: 20, inputBlocks: 0, thinkingBlocks: 0 })))
      .toBe('20 блоков (выводов 20) · пересчёт 87 с · из кэша 0%');
  });

  it('секунды округляются, доля кэша — тоже', () => {
    expect(prunedDetails(prune({ prefillSeconds: 86.6, cacheReadTokens: 56_500, promptTokens: 113_000 })))
      .toBe('25 блоков (выводов 20, входов 3, размышлений 2) · пересчёт 87 с · из кэша 50%');
  });

  it('пересчёт меньше половины секунды не показываем', () => {
    expect(prunedDetails(prune({ prefillSeconds: 0.3, cacheReadTokens: undefined, promptTokens: undefined })))
      .toBe('25 блоков (выводов 20, входов 3, размышлений 2)');
  });

  it('склонение блоков', () => {
    expect(prunedDetails(prune({ blocks: 1, resultBlocks: 1, inputBlocks: 0, thinkingBlocks: 0, prefillSeconds: undefined, promptTokens: undefined })))
      .toBe('1 блок (выводов 1)');
    expect(prunedDetails(prune({ blocks: 3, resultBlocks: 3, inputBlocks: 0, thinkingBlocks: 0, prefillSeconds: undefined, promptTokens: undefined })))
      .toBe('3 блока (выводов 3)');
  });

  it('нечего сказать — строки нет', () => {
    expect(prunedDetails(prune({
      blocks: 0, resultBlocks: 0, inputBlocks: 0, thinkingBlocks: 0,
      prefillSeconds: undefined, cacheReadTokens: undefined, promptTokens: undefined,
    }))).toBeNull();
  });

  it('у сжатия в облаке подробностей нет — всё сказано в заголовке', () => {
    expect(prunedDetails(prune({ pruneKind: 'compact_cloud' }))).toBeNull();
  });
});

describe('cachePct', () => {
  it('ноль — значимое число, а не «нет данных»', () => {
    expect(cachePct({ cacheReadTokens: 0, promptTokens: 113_000 })).toBe(0);
  });

  it('нет знаменателя или он пуст — null', () => {
    expect(cachePct({ cacheReadTokens: 100, promptTokens: undefined })).toBeNull();
    expect(cachePct({ cacheReadTokens: 100, promptTokens: 0 })).toBeNull();
    expect(cachePct({ cacheReadTokens: undefined, promptTokens: 100 })).toBeNull();
  });

  it('больше знаменателя не бывает — потолок 100', () => {
    expect(cachePct({ cacheReadTokens: 200, promptTokens: 100 })).toBe(100);
  });
});

describe('сводка по чату', () => {
  const other: ChatItem = { kind: 'text', text: 'ответ' };

  it('считает сдвиги и суммарно срезанное', () => {
    const s = summarizePruned([
      other,
      prune({ tokensBefore: 171_000, tokensAfter: 113_000 }),
      prune({ tokensBefore: 150_000, tokensAfter: 120_000 }),
    ]);
    expect(s).toEqual({ count: 2, savedTokens: 88_000 });
    expect(prunedSummaryText(s!)).toBe('2 сдвига · −88k');
  });

  it('сжатие в облаке считается сдвигом, но объёма не добавляет', () => {
    const s = summarizePruned([prune({ pruneKind: 'compact_cloud', tokensBefore: 0, tokensAfter: 0 })]);
    expect(s).toEqual({ count: 1, savedTokens: 0 });
    expect(prunedSummaryText(s!)).toBe('1 сдвиг');
  });

  it('контекст не двигали — сводки нет', () => {
    expect(summarizePruned([other])).toBeUndefined();
  });

  it('попадает в оценку контекста', () => {
    const est = estimateContext([prune(), prune()]);
    expect(est.pruned).toEqual({ count: 2, savedTokens: 116_000 });
  });
});

describe('applyServerMessage: context_pruned', () => {
  const wire = (over: Partial<Extract<ServerMessage, { type: 'context_pruned' }>> = {}): ServerMessage => ({
    sessionId: 's1', type: 'context_pruned', kind: 'prune',
    tokensBefore: 171_000, tokensAfter: 113_000,
    blocks: 25, resultBlocks: 20, inputBlocks: 3, thinkingBlocks: 2,
    prefillSeconds: 87, cacheReadTokens: 0, promptTokens: 113_000,
    ...over,
  } as ServerMessage);

  it('kind события переезжает в pruneKind элемента (kind занят дискриминатором)', () => {
    const next = applyServerMessage(initialChatState(), wire());
    expect(next.items).toEqual([{
      kind: 'context_pruned', pruneKind: 'prune',
      tokensBefore: 171_000, tokensAfter: 113_000,
      blocks: 25, resultBlocks: 20, inputBlocks: 3, thinkingBlocks: 2,
      prefillSeconds: 87, cacheReadTokens: 0, promptTokens: 113_000,
    }]);
  });

  it('сжатие в облаке доезжает своим видом', () => {
    const next = applyServerMessage(initialChatState(), wire({ kind: 'compact_cloud' }));
    expect(next.items[0]).toMatchObject({ kind: 'context_pruned', pruneKind: 'compact_cloud' });
  });

  // Диагностика 2026-09-23: на экране две строки сдвига показывали одни и те же числа.
  // Механизм — веерная внеходовая рассылка: BroadcastSessionMessageAsync шлёт ОДНО событие
  // и в session-группу, и в project-группу, а вкладка открытого чата состоит в обеих
  // (useSession.joinSession + WorkspacePage.joinProject на одном соединении signalr.ts).
  // То есть редьюсер видел одно событие дважды и дописывал вторую строку с его числами.
  describe('веер session+project: повторная доставка не задваивает строку', () => {
    it('одно событие, доставленное дважды, даёт одну строку', () => {
      const событие = wire({ eventId: 'ev-1' });
      const после = applyServerMessage(applyServerMessage(initialChatState(), событие), событие);
      expect(после.items.filter(i => i.kind === 'context_pruned')).toHaveLength(1);
    });

    it('два разных сдвига — две строки, у каждой свои числа', () => {
      let s = initialChatState();
      s = applyServerMessage(s, wire({
        eventId: 'ev-1', tokensBefore: 258_707, tokensAfter: 184_041, blocks: 37,
        resultBlocks: 24, inputBlocks: 13, thinkingBlocks: 0,
        prefillSeconds: 2.64, cacheReadTokens: 198_720, promptTokens: 200_000,
      }));
      s = applyServerMessage(s, wire({
        eventId: 'ev-2', tokensBefore: 277_778, tokensAfter: 163_968, blocks: 47,
        resultBlocks: 31, inputBlocks: 16, thinkingBlocks: 0,
        prefillSeconds: 153.6, cacheReadTokens: 0, promptTokens: 180_000,
      }));
      // Каждая доставка приходит дважды — как в бою
      s = applyServerMessage(s, wire({ eventId: 'ev-1', tokensBefore: 258_707, tokensAfter: 184_041 }));
      s = applyServerMessage(s, wire({ eventId: 'ev-2', tokensBefore: 277_778, tokensAfter: 163_968 }));

      const строки = s.items.filter(i => i.kind === 'context_pruned') as PrunedItem[];
      expect(строки).toHaveLength(2);
      expect(строки.map(prunedHeadline)).toEqual([
        'контекст обрезан · 259k → 184k',
        'контекст обрезан · 278k → 164k',
      ]);
      expect(строки.map(prunedDetails)).toEqual([
        '37 блоков (выводов 24, входов 13) · пересчёт 3 с · из кэша 99%',
        '47 блоков (выводов 31, входов 16) · пересчёт 154 с · из кэша 0%',
      ]);
    });

    it('личности нет (карточка старой истории) — строки не схлопываются', () => {
      let s = applyServerMessage(initialChatState(), wire({ tokensBefore: 100_000 }));
      s = applyServerMessage(s, wire({ tokensBefore: 200_000 }));
      expect(s.items.filter(i => i.kind === 'context_pruned')).toHaveLength(2);
    });
  });

  it('необязательных полей нет — в элементе их тоже нет', () => {
    const next = applyServerMessage(initialChatState(),
      wire({ prefillSeconds: undefined, cacheReadTokens: undefined, promptTokens: undefined }));
    expect(next.items[0]).toEqual({
      kind: 'context_pruned', pruneKind: 'prune',
      tokensBefore: 171_000, tokensAfter: 113_000,
      blocks: 25, resultBlocks: 20, inputBlocks: 3, thinkingBlocks: 2,
    });
  });
});
