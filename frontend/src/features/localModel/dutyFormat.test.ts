import { describe, expect, it } from 'vitest';
import type { LocalModelDutyAnswers, LocalModelDutyArbiter } from './api';
import {
  answersDash, answersSummary, answersWindowTitle, currentChainLink, dutyCounters,
  dutyReasonLine, dutyReasonsSummary, dutySwitchTimes, dutyWhoRows,
  formatClockLeft, nextChainHint, returnLineText,
} from './dutyFormat';

// Арбитр «Ждёт возврата»: уступила без причин, остаток паузы есть
const returning = (over: Partial<LocalModelDutyArbiter> = {}): LocalModelDutyArbiter => ({
  reachable: true,
  lastSeenAt: 1,
  state: 'yielded',
  mode: 'exclusive',
  quietLeftS: 160,
  sinceAt: null,
  yields: 0,
  returns: 0,
  reasons: [],
  leases: [],
  ...over,
});

describe('formatClockLeft: «вернётся через м:сс»', () => {
  it('75 с → «1:15»', () => {
    expect(formatClockLeft(75)).toBe('1:15');
  });
  it('секунды дополняются до двух цифр: 160 → «2:40», 65 → «1:05»', () => {
    expect(formatClockLeft(160)).toBe('2:40');
    expect(formatClockLeft(65)).toBe('1:05');
  });
  it('дробные секунды — целые: 74.8 → «1:14»', () => {
    expect(formatClockLeft(74.8)).toBe('1:14');
  });
  it('на нуле — «вот-вот», а не «0:00»', () => {
    expect(formatClockLeft(0)).toBe('вот-вот');
    expect(formatClockLeft(-3)).toBe('вот-вот');
  });
  it('нет значения — «—»', () => {
    expect(formatClockLeft(null)).toBe('—');
    expect(formatClockLeft(undefined)).toBe('—');
    expect(formatClockLeft(Number.NaN)).toBe('—');
  });
});

describe('returnLineText: строка «Вернётся»', () => {
  it('уступила с причиной — «после паузы», без чисел (пауз в /state нет)', () => {
    expect(returnLineText(returning({
      quietLeftS: null,
      reasons: [{ kind: 'comfy', raw: 'a1b2c3d4', jobKind: 'video', known: true }],
    }))).toBe('после паузы, когда задание закончится');
  });
  it('ждёт возврата — «через 2:40»', () => {
    expect(returnLineText(returning())).toBe('через 2:40');
  });
  it('остаток нулевой — «вот-вот»', () => {
    expect(returnLineText(returning({ quietLeftS: 0 }))).toBe('вот-вот');
  });
  it('ждёт возврата, но числа нет — «—», не выдуманное время', () => {
    expect(returnLineText(returning({ quietLeftS: null }))).toBe('—');
  });
  it('другие состояния — строки нет (null)', () => {
    expect(returnLineText(returning({ state: 'starting' }))).toBeNull();
    expect(returnLineText(returning({ state: 'waiting-vram' }))).toBeNull();
    expect(returnLineText(returning({ state: 'ready' }))).toBeNull();
    expect(returnLineText(null)).toBeNull();
  });
});

describe('dutyReasonLine: таблица причин (макет §5)', () => {
  it('comfy с этого сервера, вид опознан — подпись вида и переход', () => {
    expect(dutyReasonLine({ kind: 'comfy', raw: 'a1b2c3d4', jobKind: 'video', known: true }))
      .toEqual({ text: 'видео H3', toMedia: true });
    expect(dutyReasonLine({ kind: 'comfy', raw: 'a1b2c3d4', jobKind: 'image', known: true }).text).toBe('картинка');
    expect(dutyReasonLine({ kind: 'comfy', raw: 'a1b2c3d4', jobKind: 'audio', known: true }).text).toBe('звук');
    expect(dutyReasonLine({ kind: 'comfy', raw: 'a1b2c3d4', jobKind: 'lora', known: true }).text).toBe('обучение LoRA');
  });
  it('comfy с этого сервера, вид не опознан — без вида, переход остаётся', () => {
    expect(dutyReasonLine({ kind: 'comfy', raw: 'a1b2c3d4', jobKind: null, known: true }))
      .toEqual({ text: 'задание ComfyUI', toMedia: true });
  });
  it('comfy не с этого сервера — без перехода', () => {
    expect(dutyReasonLine({ kind: 'comfy', raw: 'a1b2c3d4', known: false }))
      .toEqual({ text: 'задание ComfyUI не с этого сервера', toMedia: false });
  });
  it('comfy, known неизвестен — без перехода', () => {
    expect(dutyReasonLine({ kind: 'comfy', raw: 'a1b2c3d4', known: null }))
      .toEqual({ text: 'задание ComfyUI', toMedia: false });
  });
  it('lease — только текстом, в кавычках, без моно и перехода', () => {
    expect(dutyReasonLine({ kind: 'lease', raw: 'whisperx' }))
      .toEqual({ text: 'аренда: «whisperx»', toMedia: false });
  });
  it('gpu — «чужой процесс на карте», команда отдельной моно-строкой, pid не показываем', () => {
    expect(dutyReasonLine({ kind: 'gpu', raw: '12345:python train.py --cfg lora.yaml' }))
      .toEqual({ text: 'чужой процесс на карте', mono: 'python train.py --cfg lora.yaml', toMedia: false });
  });
  it('неизвестный kind — raw как текст, без перехода', () => {
    expect(dutyReasonLine({ kind: 'weird', raw: 'что-то из сети' }))
      .toEqual({ text: 'что-то из сети', toMedia: false });
  });
});

describe('dutyReasonsSummary: несколько причин', () => {
  it('первая и «и ещё N»', () => {
    const s = dutyReasonsSummary([
      { kind: 'lease', raw: 'whisperx' },
      { kind: 'gpu', raw: '1:x' },
      { kind: 'comfy', raw: 'a1', known: false },
    ]);
    expect(s.main).toEqual({ text: 'аренда: «whisperx»', toMedia: false });
    expect(s.more).toBe(2);
  });
  it('пусто — строки нет', () => {
    expect(dutyReasonsSummary([])).toEqual({ main: null, more: 0 });
    expect(dutyReasonsSummary(null)).toEqual({ main: null, more: 0 });
  });
});

describe('цепочка: текущее звено', () => {
  it('модель активна — на Bonsai; уступила или недоступна — на страховку', () => {
    expect(currentChainLink('working')).toBe('bonsai');
    expect(currentChainLink('busy')).toBe('bonsai');
    expect(currentChainLink('yielded')).toBe('insurance');
    expect(currentChainLink('down')).toBe('insurance');
  });
  it('подпись следующего вызова — по текущему звену', () => {
    expect(nextChainHint('bonsai')).toBe('Следующий вызов места пойдёт на Bonsai');
    expect(nextChainHint('insurance')).toBe('Следующий вызов места пойдёт на MiniMax');
  });
});

describe('ответы мест за час', () => {
  const NOW = new Date(2026, 9, 13, 14, 9, 0).getTime();
  const fullHour: LocalModelDutyAnswers = { bonsai: 212, insurance: 31, claude: 2, missed: 1, sinceAt: NOW - 60 * 60_000 };

  it('доля считается от сумм answersLastHour окна', () => {
    const s = answersSummary(fullHour);
    expect(s.total).toBe(246);
    expect(s.segments[0].widthPerc).toBeCloseTo((212 / 246) * 100, 6);
    expect(s.segments[0].count).toBe(212);
    expect(s.segments[3].label).toBe('без ответа');
  });
  it('нулевой сегмент полосой не рисуется, но остаётся в легенде с числом 0', () => {
    const s = answersSummary({ bonsai: 10, insurance: 0, claude: 5, missed: 0, sinceAt: NOW });
    expect(s.segments[1].widthPerc).toBeNull();
    expect(s.segments[1].count).toBe(0);
    expect(s.segments[3].widthPerc).toBeNull();
    expect(s.segments[3].count).toBe(0);
    expect(s.segments[0].widthPerc).toBeCloseTo((10 / 15) * 100, 6);
  });
  it('0 ответов — «—», а не NaN', () => {
    expect(answersDash({ bonsai: 0, insurance: 0, claude: 0, missed: 0, sinceAt: NOW })).toBe('—');
    const s = answersSummary({ bonsai: 0, insurance: 0, claude: 0, missed: 0, sinceAt: NOW });
    expect(s.total).toBe(0);
    for (const seg of s.segments) {
      expect(seg.widthPerc).toBeNull();
      expect(Number.isNaN(seg.widthPerc ?? 0)).toBe(false);
    }
  });
  it('данных нет (блок null) — тоже «—»', () => {
    expect(answersDash(null)).toBe('—');
  });
  it('данные есть — строки «—» нет, вместо неё полоса', () => {
    expect(answersDash(fullHour)).toBeNull();
  });
  it('сервер работает меньше часа: «· 23 мин» и «сервер перезапущен в 13:46»', () => {
    const w = answersWindowTitle({ ...fullHour, sinceAt: NOW - 23 * 60_000 }, NOW);
    expect(w.title).toBe('Ответы мест · 23 мин');
    expect(w.hint).toBe('сервер перезапущен в 13:46');
  });
  it('полное окно — «за час», без подсказки', () => {
    const w = answersWindowTitle(fullHour, NOW);
    expect(w.title).toBe('Ответы мест за час');
    expect(w.hint).toBeUndefined();
  });
  it('часы рассинхронизированы (sinceAt в будущем) — окно считаем полным', () => {
    const w = answersWindowTitle({ ...fullHour, sinceAt: NOW + 10 * 60_000 }, NOW);
    expect(w.title).toBe('Ответы мест за час');
  });
});

describe('счётчики уступок', () => {
  it('yields==returns==0 — «с запуска арбитра», даже когда метка времени есть', () => {
    const sinceAt = new Date(2026, 9, 13, 13, 41, 0).getTime();
    const c = dutyCounters(returning({ yields: 0, returns: 0, sinceAt }));
    expect(c).toEqual({ value: '0 · 0', period: 'с запуска арбитра' });
  });
  it('ненулевые счётчики — период «с HH:MM» по sinceAt', () => {
    const sinceAt = new Date(2026, 9, 13, 13, 41, 0).getTime();
    const c = dutyCounters(returning({ yields: 14, returns: 14, sinceAt }));
    expect(c).toEqual({ value: '14 · 14', period: 'с 13:41' });
  });
  it('ненулевые счётчики без sinceAt — «с запуска арбитра» (метки времени нет)', () => {
    const c = dutyCounters(returning({ yields: 3, returns: 2, sinceAt: null }));
    expect(c?.period).toBe('с запуска арбитра');
  });
  it('данных о счётчиках нет — строки нет', () => {
    expect(dutyCounters(null)).toBeNull();
    expect(dutyCounters(returning({ yields: null, returns: null }))).toBeNull();
  });
});

describe('dutySwitchTimes: «Уступает · возвращается»', () => {
  it('последние длительности: «0,2 с · 6,1 с»', () => {
    expect(dutySwitchTimes(returning({ lastYieldS: 0.2, lastReturnS: 6.1 }))).toBe('0,2 с · 6,1 с');
  });
  it('целое — без хвоста: «6 с»', () => {
    expect(dutySwitchTimes(returning({ lastYieldS: 0, lastReturnS: 6 }))).toBe('0 с · 6 с');
  });
  it('нет обоих значений — строки нет', () => {
    expect(dutySwitchTimes(returning({ lastYieldS: null, lastReturnS: null }))).toBeNull();
    expect(dutySwitchTimes(null)).toBeNull();
  });
  it('одно без значения — «—» на своём месте', () => {
    expect(dutySwitchTimes(returning({ lastYieldS: null, lastReturnS: 6.1 }))).toBe('— · 6,1 с');
  });
});

describe('dutyWhoRows: строки «Кто сейчас» для Bonsai', () => {
  it('зеновое имя и длительность; очередь — «ждёт в очереди»', () => {
    const rows = dutyWhoRows([
      { place: 'Заголовок чата', link: 'bonsai', durationS: 1.4 },
      { place: 'Классификация задачи', link: 'insurance', durationS: 2.1 },
      { place: 'Сводка хода', link: null, durationS: 0.3 },
    ]);
    expect(rows[0]).toEqual({ key: 'call-0', place: 'Заголовок чата', linkText: 'Bonsai', queued: false, duration: '1,4 с' });
    expect(rows[1].linkText).toBe('MiniMax · страховка');
    expect(rows[2]).toEqual({ key: 'call-2', place: 'Сводка хода', linkText: 'ждёт в очереди', queued: true, duration: '0,3 с' });
  });
  it('длительности нет (вызов ещё не начался) — значения не показываем', () => {
    const rows = dutyWhoRows([{ place: 'Теги заметки', link: 'claude', durationS: null }]);
    expect(rows[0].linkText).toBe('облако');
    expect(rows[0].duration).toBe('');
  });
  it('пусто или null — пустой список', () => {
    expect(dutyWhoRows([])).toEqual([]);
    expect(dutyWhoRows(null)).toEqual([]);
  });
});
