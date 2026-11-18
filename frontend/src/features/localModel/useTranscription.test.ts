// Опрос снимка конвейера (вкладка «Встречи»): чистый контроллер
// (createTranscriptionPoller) — тест без React/DOM. Паттерн — как
// useLocalModelMedia.test.ts: фейковые таймеры, инъектированные setTimer/clearTimer.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createTranscriptionPoller, TRANSCRIPTION_POLL_MS } from './useTranscription';
import type { TranscriptionSnapshot } from './types';

// Снимок по контракту предложения v1 (таблица «Контракт данных»): все блоки.
const snapshot: TranscriptionSnapshot = {
  phase: 'batch',
  updatedAt: 1_763_000_000_000,
  host: 'ai',
  pid: 4123,
  alive: true,
  outputHost: 'ai',
  outputDir: '/mnt/media/Transcripts',
  level: 'ok',
  file: '2026-11-18 планёрка.md',
  folder: 'Video/2026-11-18 планёрка',
  stage: 'asr',
  stageStartedAt: 1_762_999_960_000,
  durationMin: 4,
  batchIndex: 2,
  batchSize: 5,
  asrQueue: [{ file: 'next.mp4', folder: 'Video/next', since: 1_762_999_000_000 }],
  summaryQueue: 1,
  fails: [{ at: 1_762_900_000_000, folder: 'Video/x', file: 'x.mp4', minutes: 12, comment: 'ASR упала' }],
  recent: [{ at: 1_762_990_000_000, folder: 'Video/y', file: 'y.md', minutes: 31, speakers: 3, comment: '', mdPath: '/srv/meetings/y.md' }],
  dify: [{ folder: 'Video/y', local: 1, inDify: 1 }],
  checkedAt: 1_762_995_000_000,
  deps: [{ key: 'comfy', state: 'ok', checkedAt: 1_762_999_000_000 }],
};

describe('createTranscriptionPoller: 5-с опрос вкладки «Встречи»', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  const make = () => {
    const fetch = vi.fn(async () => snapshot);
    const p = createTranscriptionPoller(fetch, TRANSCRIPTION_POLL_MS, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    return { fetch, p };
  };

  it('вкладка не открыта (и не-админ: панель не отдаёт вкладку) — таймера нет, опроса нет', async () => {
    const { fetch, p } = make();
    p.setActive(false, true);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetch).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
    expect(p.getSnapshot()).toEqual({ data: null, loading: false });
  });

  it('открытая вкладка и видимый браузер — опрос сразу и каждые 5 с', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1); // первый снимок сразу
    await vi.advanceTimersByTimeAsync(TRANSCRIPTION_POLL_MS);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(TRANSCRIPTION_POLL_MS * 2);
    expect(fetch).toHaveBeenCalledTimes(4);
    p.dispose();
    expect(vi.getTimerCount()).toBe(0); // dispose — без утечки таймера
  });

  it('закрыли вкладку во время опроса — опрос встаёт, таймера не остаётся', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    p.setActive(false, true); // человек ушёл с вкладки
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('вкладка браузера скрыта — опрос стоит; снова видима — продолжается', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    p.setActive(true, false); // скрыли вкладку браузера
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetch).toHaveBeenCalledTimes(1);
    p.setActive(true, true); // вернули: немедленный снимок + таймер
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(TRANSCRIPTION_POLL_MS);
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('снапшот: данные после первого ответа, loading гаснет', async () => {
    const { p } = make();
    p.setActive(true, true);
    expect(p.getSnapshot().data).toBeNull(); // до первого ответа — пустое состояние
    await vi.advanceTimersByTimeAsync(0);
    const snap = p.getSnapshot();
    expect(snap.data).toEqual(snapshot);
    expect(snap.loading).toBe(false);
  });

  it('ошибка сети не роняет: прежние данные, loading гаснет, исключения нет', async () => {
    const fetch = vi.fn(async () => snapshot);
    const p = createTranscriptionPoller(fetch, TRANSCRIPTION_POLL_MS, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toEqual(snapshot);
    fetch.mockImplementationOnce(async () => { throw new Error('offline'); });
    await vi.advanceTimersByTimeAsync(TRANSCRIPTION_POLL_MS);
    expect(p.getSnapshot().data).toEqual(snapshot); // прежние данные
    expect(p.getSnapshot().loading).toBe(false);
  });

  it('пустой ответ (null) — пустое состояние, не исключение', async () => {
    const fetch = vi.fn((): Promise<TranscriptionSnapshot> => Promise.resolve(null as unknown as TranscriptionSnapshot));
    const p = createTranscriptionPoller(fetch, TRANSCRIPTION_POLL_MS, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toBeNull();
    expect(p.getSnapshot().loading).toBe(false);
  });

  it('неизвестные поля не роняют разбор: известные читаются, чужые игнорируются', async () => {
    const weird = {
      ...snapshot,
      unexpectedBlock: { deep: { nested: 1 } },
      phaseExtra: 'something',
      deps: [...snapshot.deps!, { key: 'future', state: 'unknown', checkedAt: null, extraField: true }],
    } as unknown as TranscriptionSnapshot;
    const fetch = vi.fn(async () => weird);
    const p = createTranscriptionPoller(fetch, TRANSCRIPTION_POLL_MS, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    const data = p.getSnapshot().data!;
    expect(data.phase).toBe('batch');
    expect(data.level).toBe('ok');
    expect(data.asrQueue).toHaveLength(1);
    expect(data.recent?.[0].mdPath).toBe('/srv/meetings/y.md');
    expect(data.deps).toHaveLength(2); // чужая зависимость прочитана как есть, не отброшена
  });

  it('частичный ответ («Сейчас» при простое) — снимок принимается без падения', async () => {
    const idle = { phase: 'idle', updatedAt: 1_763_000_000_000, level: 'ok' } as TranscriptionSnapshot;
    const fetch = vi.fn(async () => idle);
    const p = createTranscriptionPoller(fetch, TRANSCRIPTION_POLL_MS, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    const data = p.getSnapshot().data!;
    expect(data.phase).toBe('idle');
    expect(data.file).toBeUndefined(); // «Ничего не обрабатывается» — пустой блок, не исключение
    expect(data.dify).toBeUndefined();
  });

  it('getSnapshot: два подряд вызова без событий — один объект; после fetch — новый', async () => {
    const { p } = make();
    const s1 = p.getSnapshot();
    const s2 = p.getSnapshot();
    expect(s1).toBe(s2); // === : кэш, а не новый объект на каждый вызов
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    const s3 = p.getSnapshot();
    expect(s3).not.toBe(s1); // после завершения fetch — пересобранный снимок
  });
});
