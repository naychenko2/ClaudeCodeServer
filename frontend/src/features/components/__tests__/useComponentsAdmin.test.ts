// useComponentsAdmin (S2c-2): загрузка списка, ошибка загрузки, запись тумблера (успех и
// отказ 503), независимость разных строк, отказ второй записи той же строки и отбрасывание
// ответа, вернувшегося после reload.
//
// Окружение node: фейк-DOM (createRoot + act) — паттерн useDraftDiff.test.ts. Модуль API
// мокнут: здесь проверяется состояние хука, а форма запроса/ответа — предмет
// lib/__tests__/subsystems.contract.test.ts.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Subsystem } from '../../../api/subsystems';

vi.mock('../../../api/subsystems', () => ({
  subsystemsApi: { get: vi.fn(), put: vi.fn() },
}));

const { subsystemsApi } = await import('../../../api/subsystems');
const { useComponentsAdmin } = await import('../useComponentsAdmin');
const { installFakeDom, mount, uninstallFakeDom } = await import('../../../components/chat/__tests__/fakeDom');

type Admin = ReturnType<typeof useComponentsAdmin>;
let latest: Admin;
const roots: { unmount: () => void }[] = [];

function Probe() {
  latest = useComponentsAdmin();
  return null;
}

// Строка такой, какой её отдаёт бэк: record целиком, null-значения использования
// сериализуются (SubsystemInfo в IAppSubsystem.cs).
function sys(key: string, over: Partial<Subsystem> = {}): Subsystem {
  return {
    key, title: key, description: '',
    enabled: true, active: true, restartRequired: false, canDisable: true,
    lastUsedAt: null, usedLast24h: null,
    ...over,
  };
}

// Ошибка такой, какой её бросает request() при !res.ok: Error с .status и .body — телом
// ответа. message ошибки — statusText (у тела { code, message } своего `error` нет),
// поэтому проверка ниже именно на .body.message: текст сервера доходит до строки.
function httpError(status: number, body: unknown): Error & { status: number; body: unknown } {
  const e = new Error('Internal Server Error') as Error & { status: number; body: unknown };
  e.status = status;
  e.body = body;
  return e;
}

function deferred() {
  let resolve!: (v: Subsystem) => void;
  const promise = new Promise<Subsystem>(r => { resolve = r; });
  return { promise, resolve };
}

beforeEach(() => {
  installFakeDom();
  vi.mocked(subsystemsApi.get).mockReset();
  vi.mocked(subsystemsApi.put).mockReset();
});
afterEach(() => { for (const r of roots.splice(0)) act(() => r.unmount()); });
afterAll(() => { uninstallFakeDom(); });

function open() {
  const m = mount(createElement(Probe));
  roots.push(m.root);
}

describe('useComponentsAdmin', () => {
  it('загрузка: пока списка нет — loading; пришёл — порядок сервера, ошибок нет', async () => {
    vi.mocked(subsystemsApi.get).mockResolvedValue([sys('notes'), sys('llm')]);
    open();
    // Ответ ещё не обработан: панель не должна показывать пустой список как «всё пусто».
    expect(latest.loading).toBe(true);
    await act(async () => {});
    expect(latest.items.map(s => s.key)).toEqual(['notes', 'llm']);
    expect(latest.loading).toBe(false);
    expect(latest.loadError).toBeNull();
    expect(latest.writeState.notes).toBe('idle');
  });

  it('ошибка загрузки: loadError текстом, пустого «всё выключено» нет', async () => {
    vi.mocked(subsystemsApi.get).mockRejectedValue(new Error('нет связи'));
    open();
    await act(async () => {});
    expect(latest.loadError).toBe('нет связи');
    expect(latest.items).toEqual([]);
    expect(latest.loading).toBe(false);
  });

  it('запись тумблера: PUT с телом {enabled}, строка = ответ сервера целиком, строка снова idle', async () => {
    vi.mocked(subsystemsApi.get).mockResolvedValue([sys('notes')]);
    // Ответ бэка — вся строка с уже записанным enabled и честным restartRequired
    // (SubsystemsController.Put: Ok(current with {…})).
    vi.mocked(subsystemsApi.put).mockResolvedValue(sys('notes', { enabled: false, restartRequired: true }));
    open();
    await act(async () => {});
    await act(async () => { await latest.toggle('notes', false); });
    expect(subsystemsApi.put).toHaveBeenCalledWith('notes', false);
    expect(latest.items[0].enabled).toBe(false);
    expect(latest.items[0].restartRequired).toBe(true);
    expect(latest.writeState.notes).toBe('idle');
  });

  it('отказ 503: message сервера — в строку как есть, items не тронуты, rejection не возник', async () => {
    vi.mocked(subsystemsApi.get).mockResolvedValue([sys('notes')]);
    vi.mocked(subsystemsApi.put).mockRejectedValue(httpError(503, { code: 'write_failed', message: 'disk full' }));
    open();
    await act(async () => {});
    // Вызов ждём: если бы хук реджектил, здесь же и упало бы (в UI toggle зовётся без
    // await — тот же провал был бы unhandled rejection). Не упало → rejection нет.
    await act(async () => { await latest.toggle('notes', false); });
    expect(latest.writeState.notes).toEqual({ error: 'disk full' });
    // Строка не «выключилась»: сервер ничего не записал — оптимистичного применения нет.
    expect(latest.items[0].enabled).toBe(true);
  });

  it('та же строка подряд: второй toggle отклоняется, пока первый в полёте — один запрос', async () => {
    vi.mocked(subsystemsApi.get).mockResolvedValue([sys('notes')]);
    const inFlight = deferred();
    vi.mocked(subsystemsApi.put).mockReturnValue(inFlight.promise);
    open();
    await act(async () => {});
    await act(async () => { latest.toggle('notes', false); latest.toggle('notes', true); });
    expect(subsystemsApi.put).toHaveBeenCalledTimes(1);
    expect(latest.writeState.notes).toBe('saving');
    await act(async () => { inFlight.resolve(sys('notes', { enabled: false, restartRequired: true })); });
    expect(latest.writeState.notes).toBe('idle');
    expect(latest.items[0].enabled).toBe(false);
  });

  it('разные строки: независимы, у каждой своё writeState', async () => {
    vi.mocked(subsystemsApi.get).mockResolvedValue([sys('notes'), sys('llm')]);
    const a = deferred();
    const b = deferred();
    vi.mocked(subsystemsApi.put).mockReturnValueOnce(a.promise).mockReturnValueOnce(b.promise);
    open();
    await act(async () => {});
    await act(async () => { latest.toggle('notes', false); latest.toggle('llm', false); });
    expect(subsystemsApi.put).toHaveBeenCalledTimes(2);
    expect(latest.writeState.notes).toBe('saving');
    expect(latest.writeState.llm).toBe('saving');
    await act(async () => { b.resolve(sys('llm', { enabled: false, restartRequired: true })); });
    expect(latest.items.find(s => s.key === 'llm')?.enabled).toBe(false);
    expect(latest.writeState.llm).toBe('idle');
    // Первая ещё в полёте и не тронута второй
    expect(latest.items.find(s => s.key === 'notes')?.enabled).toBe(true);
    expect(latest.writeState.notes).toBe('saving');
    await act(async () => { a.resolve(sys('notes', { enabled: false, restartRequired: true })); });
    expect(latest.items.find(s => s.key === 'notes')?.enabled).toBe(false);
    expect(latest.writeState.notes).toBe('idle');
  });

  it('toggle_staleResponseAfterReload_ignored: ответ полёта после reload не применяется', async () => {
    // Первый снимок — включено; reload даёт такой же снимок: горячего применения нет,
    // и по нему видно, что строка жива и «idle».
    vi.mocked(subsystemsApi.get)
      .mockResolvedValueOnce([sys('notes')])
      .mockResolvedValueOnce([sys('notes')]);
    const stale = deferred();
    vi.mocked(subsystemsApi.put).mockReturnValue(stale.promise);
    open();
    await act(async () => {});
    await act(async () => { latest.toggle('notes', false); });
    expect(latest.writeState.notes).toBe('saving');
    await act(async () => { latest.reload(); });
    expect(latest.items[0].enabled).toBe(true);
    expect(latest.writeState.notes).toBe('idle');
    // Поздний ответ прежней записи: её серии уже нет — строка не меняется.
    await act(async () => { stale.resolve(sys('notes', { enabled: false, restartRequired: true })); });
    expect(latest.items[0].enabled).toBe(true);
    expect(latest.writeState.notes).toBe('idle');
  });
});
