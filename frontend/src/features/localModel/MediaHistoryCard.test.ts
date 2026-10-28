// Карточка истории медиа: файл результата не должен оставлять blob-URL, если карточку
// закрыли или перелистали, пока тело ехало (дефект 58c0c13b, замер QA7: 3,69 МБ на гонку
// и 10,81 МБ за два шага «→»).
//
// Окружение node, без jsdom: клиентский рендер настоящего MediaHistoryCard через createRoot
// на фейк-DOM (общий хелпер components/chat/__tests__/fakeDom, паттерн mediaHistorySkeleton.test.ts
// и CacheWarnDialog.test.ts). fetch и URL.createObjectURL/revokeObjectURL подменены счётчиком
// «создано/отозвано» — тем же, что ставил QA7 в странице: под тестом эффект карточки и его
// гонка «закрытие ↔ доезд тела», а не сеть и не браузер.
//
// Мутации, которые обязаны покрасить:
// - убрать `if (cancelled) return` после res.blob() → «закрыли во время загрузки» и «→»;
// - убрать ac.abort() в cleanup → «отмена дошла до fetch»;
// - снять проверку cancelled в .catch → «отмена не показана как сбой».

import { act, createElement } from 'react';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  installFakeDom, mount, textNodes, uninstallFakeDom,
} from '../../components/chat/__tests__/fakeDom';
import { MediaHistoryCard } from './MediaHistoryCard';
import type { MediaHistoryRecord } from '../../lib/api';

// Фаза «загрузка» рисует WaitingIndicator, а он тянет резолвер персон
// (lib/contextPersona → lib/nav → window.history) — к гонке «файл ↔ закрытие» отношения
// не имеет и в node без навигации падает. Сквозная заглушка (паттерн
// mediaHistorySkeleton.test.ts, где так же замокан стор истории).
vi.mock('../../components/ui/WaitingIndicator', () => ({ WaitingIndicator: () => null }));

const tick = () => new Promise<void>(r => setTimeout(r, 0));
const ERROR_TEXT = 'Не удалось загрузить результат.';

function rec(id: string): MediaHistoryRecord {
  return {
    id, kind: 'image', op: 'generate_image', model: 'flux', prompt: null, params: null,
    owner: 'me', status: 'completed', outcome: 'completed', error: null, runSeconds: 5,
    enqueuedAt: null, startedAt: null, finishedAt: null, source: 'local-media',
  };
}

// ── Счётчик blob-URL: «создано / отозвано / живые» — как BLOB_INIT в замере QA7 ──
let created: string[] = [];
let revoked: string[] = [];
const live = () => created.filter(u => !revoked.includes(u));

const originalCreate = URL.createObjectURL;
const originalRevoke = URL.revokeObjectURL;
const originalFetch = globalThis.fetch;

// ── fetch под контролем теста: «ответ пришёл» и «тело доехало» — два разных момента ──
interface Gate {
  resolve: (res: Response) => void;
  reject: (e: Error) => void;
  signal: AbortSignal | null;
}
let gates: Gate[] = [];
let urls: string[] = [];

const fetchMock = vi.fn((url: string, init?: { signal?: AbortSignal }) =>
  new Promise<Response>((resolve, reject) => {
    const g: Gate = { resolve, reject, signal: init?.signal ?? null };
    gates.push(g);
    urls.push(url);
    // Как настоящий fetch: отмена signal роняет незавершённый запрос AbortError'ом
    g.signal?.addEventListener('abort', () => {
      const e = new Error('aborted');
      e.name = 'AbortError';
      reject(e);
    });
  }));

// Ответ доставлен, тело ещё читается: blobDone() — момент, когда оно доехало.
// Намеренно НЕ зависит от отмены: это ровно то, что показал перехват Response.prototype.blob
// в замере (тело доехало уже после закрытия карточки).
function respond(g: Gate): { blobDone: () => void } {
  let done: (b: Blob) => void = () => {};
  const body = new Promise<Blob>(r => { done = r; });
  g.resolve({ ok: true, status: 200, blob: () => body, json: () => Promise.resolve({}) } as unknown as Response);
  return { blobDone: () => { done(new Blob(['x'])); } };
}

const lastGate = () => gates[gates.length - 1];

type Mounted = ReturnType<typeof mount>;

const cardProps = (record: MediaHistoryRecord) => ({
  record, isMobile: false, meUsername: 'me', onPrev: null, onNext: () => {}, onClose: () => {},
});

function openCard(record: MediaHistoryRecord): Mounted {
  return mount(createElement(MediaHistoryCard, cardProps(record)));
}

// Тот же корень и та же карточка, другая запись — это «→» в ленте: MediaHistory меняет
// record, эффект карточки перезапускается по deps [record.id, …] со своим cleanup.
function switchTo(card: Mounted, record: MediaHistoryRecord): void {
  act(() => { card.root.render(createElement(MediaHistoryCard, cardProps(record))); });
}

// Карточка внутри Modal — на портале (createPortal без контейнера → document.body),
// поэтому дерево смотрим от body, а не от контейнера mount'а.
type FNodeLike = Parameters<typeof textNodes>[0];
const bodyRoot = (): FNodeLike => (globalThis as unknown as { document: { body: FNodeLike } }).document.body;

const imgSrc = (): string | null => bodyRoot().querySelectorAll('img')[0]?.getAttribute('src') ?? null;

beforeAll(() => { installFakeDom(); });
afterAll(() => {
  uninstallFakeDom();
  URL.createObjectURL = originalCreate;
  URL.revokeObjectURL = originalRevoke;
  globalThis.fetch = originalFetch;
});

beforeEach(() => {
  created = [];
  revoked = [];
  gates = [];
  urls = [];
  let n = 0;
  URL.createObjectURL = ((obj: Blob) => {
    const url = `blob:test:${++n}`;
    created.push(url);
    return url;
  }) as typeof URL.createObjectURL;
  URL.revokeObjectURL = ((url: string) => { revoked.push(url); }) as typeof URL.revokeObjectURL;
  globalThis.fetch = fetchMock as unknown as typeof fetch;
});

afterEach(() => {
  URL.createObjectURL = originalCreate;
  URL.revokeObjectURL = originalRevoke;
  globalThis.fetch = originalFetch;
});

describe('карточка истории: гонка «закрытие/листание ↔ доезд тела»', () => {
  it('закрыли во время загрузки: blob-URL не создаётся вовсе', async () => {
    const card = openCard(rec('a'));
    const a = respond(lastGate());
    await tick();                      // код карточки стоит на await res.blob()
    act(() => { card.root.unmount(); }); // «Закрыть (Esc)»
    a.blobDone();                      // тело доехало уже после закрытия
    await tick();
    expect(created).toEqual([]);       // не «создан и не отозван» — он не создан
    expect(revoked).toEqual([]);
  });

  it('пролистали «→» во время загрузки: от прежней записи не остаётся живых blob-URL', async () => {
    const card = openCard(rec('a'));
    const a = respond(lastGate());
    await tick();
    switchTo(card, rec('b'));          // «→»: карточка та же, запись другая
    expect(urls[1]).toContain('/api/local-model/history/b/file');
    const b = respond(lastGate());
    await tick();
    a.blobDone();                     // тело «a» доехало после смены записи
    b.blobDone();
    await tick();
    expect(created.length).toBe(1);   // не 2: «a» не стал blob-URL
    act(() => { card.root.unmount(); });
    await tick();
    expect(revoked).toEqual(created);
    expect(live()).toEqual([]);       // живых blob-ов от листания нет
  });

  it('штатный путь: догрузилось → показали → закрыли: создано 1, отозван ровно он', async () => {
    const card = openCard(rec('a'));
    expect(urls[0]).toContain('/api/local-model/history/a/file');
    const a = respond(lastGate());
    a.blobDone();
    await tick();
    expect(created.length).toBe(1);
    expect(imgSrc()).toBe(created[0]);  // карточка показывает результат по этому URL
    act(() => { card.root.unmount(); });
    await tick();
    expect(revoked).toEqual(created);
    expect(live()).toEqual([]);
  });

  it('отмена по размонтированию: запрос гасится, на экране нет сбоя', async () => {
    const card = openCard(rec('a'));
    const ga = lastGate();
    switchTo(card, rec('b'));          // cleanup карточки «a» — до доезда тела
    expect(ga.signal?.aborted).toBe(true);
    await tick();                     // AbortError дошёл до .catch
    expect(textNodes(bodyRoot()).join('')).not.toContain(ERROR_TEXT);
    const b = respond(lastGate());
    b.blobDone();
    await tick();
    expect(imgSrc()).toBe(created[0]);  // новая запись досчиталась как обычно
    act(() => { card.root.unmount(); });
  });

  it('сетевой сбой — по-прежнему показывается: отмена от него отличается', async () => {
    const card = openCard(rec('a'));
    const e = new Error('network down');
    lastGate().reject(e);
    await tick();
    expect(textNodes(bodyRoot()).join('')).toContain(ERROR_TEXT);
    expect(created).toEqual([]);
    act(() => { card.root.unmount(); });
  });
});
