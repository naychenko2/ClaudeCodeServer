// Тесты стора доски агентов: троттл и дедуп полётов.
//
// Дефект (дефектная карточка 034a96c3, профиль CDP): ответ доски — самый крупный из
// регулярно опрашиваемых (~0,86 МБ), а status_changed приходит пачками на каждом ходу.
// Прежняя схема ограничивала только частоту ВЫЗОВОВ (debounce 500 мс), но не полёты:
// пока запрос летел, следующее событие успевало взвести новый. Замер: 302 запроса за час.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
}));

vi.mock('../api', () => ({
  api: { board: { agents: vi.fn(() => Promise.resolve({ items: [] })) } },
}));

import type { ServerMessage } from '../../types';
import { api } from '../api';
import { onMessage } from '../signalr';
import { ensureAgentsLoaded, refreshAgentBoard, resetBoardStore, subscribe } from '../agentBoard';

// Обработчик, который стор передал в onMessage при wireRealtime
function handler(): (msg: ServerMessage) => void {
  const calls = vi.mocked(onMessage).mock.calls;
  return calls[calls.length - 1][0] as (msg: ServerMessage) => void;
}

// Симулирует смонтированный компонент: подписчик на доску. Вызывается ПОСЛЕ
// первичной загрузки (ensureAgentsLoaded), чтобы не дублировать запрос, но чтобы
// guard «не ходить, пока _listeners пуст» видел слушателя и пускал фоновые полёты.
function mountBoard(): void {
  subscribe(() => {});
}

const statusChanged = (sessionId: string) =>
  ({ type: 'status_changed', sessionId, status: 'working' }) as unknown as ServerMessage;

const agentsCalls = () => vi.mocked(api.board.agents);

beforeEach(() => {
  vi.useFakeTimers();
  resetBoardStore();
  vi.mocked(onMessage).mockClear();
  vi.mocked(api.board.agents).mockClear();
  vi.mocked(api.board.agents).mockResolvedValue({ items: [] });
});

afterEach(() => {
  vi.useRealTimers();
});

describe('доска агентов: троттл и дедуп полётов', () => {
  it('первичная загрузка идёт сразу, без ожидания зазора', async () => {
    await ensureAgentsLoaded();
    expect(agentsCalls()).toHaveBeenCalledTimes(1);
  });

  it('пачка событий подряд даёт ОДИН запрос, а не по одному на событие', async () => {
    await ensureAgentsLoaded();
    mountBoard();
    const send = handler();
    for (let i = 0; i < 20; i++) send(statusChanged('s1'));

    // ни один таймер ещё не продвинулся — запросов быть не должно
    expect(agentsCalls()).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(5_000);
    expect(agentsCalls()).toHaveBeenCalledTimes(2);
  });

  it('пока летит запрос, следующий полёт не начинается', async () => {
    let release!: (v: { items: [] }) => void;
    vi.mocked(api.board.agents).mockReturnValueOnce(new Promise(r => { release = r; }));

    const first = ensureAgentsLoaded();
    mountBoard();
    refreshAgentBoard();
    refreshAgentBoard();
    refreshAgentBoard();
    expect(agentsCalls()).toHaveBeenCalledTimes(1);

    release({ items: [] });
    await first;
    // события, пришедшие в полёте, не потеряны — взведён догон, но не второй полёт
    expect(agentsCalls()).toHaveBeenCalledTimes(1);
  });

  it('событие во время полёта не поднимает второй запрос, даже когда таймер сработал', async () => {
    let release!: (v: { items: [] }) => void;
    vi.mocked(api.board.agents).mockReturnValueOnce(new Promise(r => { release = r; }));

    const first = ensureAgentsLoaded();
    mountBoard();
    // событие приходит, пока предыдущий запрос ещё летит
    handler()(statusChanged('s1'));
    // таймер догона отрабатывает — и должен уперться в занятый полёт
    await vi.advanceTimersByTimeAsync(5_000);
    expect(agentsCalls()).toHaveBeenCalledTimes(1);

    release({ items: [] });
    await first;
  });

  it('событие, пришедшее в полёте, догоняется следующим запросом', async () => {
    let release!: (v: { items: [] }) => void;
    vi.mocked(api.board.agents).mockReturnValueOnce(new Promise(r => { release = r; }));

    const first = ensureAgentsLoaded();
    mountBoard();
    // событие в полёте: догон
    refreshAgentBoard();
    release({ items: [] });
    await first;

    await vi.advanceTimersByTimeAsync(5_000);
    expect(agentsCalls()).toHaveBeenCalledTimes(2);
  });

  it('зазор между стартами соблюдается даже при непрерывном потоке', async () => {
    await ensureAgentsLoaded();
    mountBoard();
    const send = handler();

    // минута работы: событие каждые 100 мс
    for (let t = 0; t < 60_000; t += 100) {
      send(statusChanged('s1'));
      await vi.advanceTimersByTimeAsync(100);
    }
    // ~60 с потока при зазоре 5 с — десятки запросов, но не сотни
    expect(agentsCalls().mock.calls.length).toBeLessThanOrEqual(13);
  });

  it('сброс стора снимает взведённый таймер', async () => {
    await ensureAgentsLoaded();
    mountBoard();
    handler()(statusChanged('s1'));
    resetBoardStore();

    await vi.advanceTimersByTimeAsync(60_000);
    expect(agentsCalls()).toHaveBeenCalledTimes(1);
  });

  it('пока доску никто не смотрит, события не вызывают запросов', async () => {
    await ensureAgentsLoaded();   // подписчиков нет
    const send = handler();
    send(statusChanged('s1'));
    refreshAgentBoard();

    await vi.advanceTimersByTimeAsync(60_000);
    expect(agentsCalls()).toHaveBeenCalledTimes(1);
  });

  it('сброс стора отписывается от signalr', async () => {
    const off = vi.fn();
    vi.mocked(onMessage).mockReturnValueOnce(off);
    await ensureAgentsLoaded();
    resetBoardStore();
    expect(off).toHaveBeenCalledTimes(1);
  });

  // Догон: событие, пришедшее в полёте, помечает _pending; когда полёт
  // завершается, блок if (_pending) в finally взводит единственный догон.
  // Удаление этого блока даёт красный: таймер не взводится, запроса не будет.
  it('догон после полёта: _pending взводит единственный свежий запрос', async () => {
    let release!: (v: { items: [] }) => void;
    vi.mocked(api.board.agents).mockReturnValueOnce(new Promise(r => { release = r; }));

    const first = ensureAgentsLoaded();
    // Подписчик в полёте: fetchBoard дедупится и помечает _pending (без отдельного
    // refreshAgentBoard — единственный повод догона именно блок if (_pending) в finally)
    mountBoard();
    release({ items: [] });
    await first;

    // Пока таймер не продвинут — догон ещё не ушёл
    expect(agentsCalls()).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(agentsCalls()).toHaveBeenCalledTimes(2);
  });
});