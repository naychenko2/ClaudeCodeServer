// Тесты стора «Стены»: слоты от ширины, мутации состава, дебаунс-PUT,
// приоритет live-статуса над снимком.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// SignalR мокаем целиком: стор дёргает joinProject/joinUser/onMessage/onReconnected,
// а реального соединения в тестах нет
vi.mock('../../../lib/signalr', () => ({
  joinProject: vi.fn(() => Promise.resolve()),
  joinUser: vi.fn(() => Promise.resolve()),
  onMessage: vi.fn(),
  onReconnected: vi.fn(() => () => {}),
}));

vi.mock('../../../lib/api', () => ({
  api: {
    wall: {
      get: vi.fn(() => Promise.resolve({ chats: [] })),
      put: vi.fn((ids: string[]) => Promise.resolve({ chats: ids.map(fakeSession) })),
      candidates: vi.fn(() => Promise.resolve([])),
    },
    projects: { list: vi.fn(() => Promise.resolve([])) },
  },
}));

import type { Project, Session } from '../../../types';
import { api } from '../../../lib/api';
import { onMessage } from '../../../lib/signalr';
import {
  slotCount, MAX_SLOTS, addChat, removeChat, reorderChat, moveToVisible,
  getWallState, chatStatus, initWall, refresh, focusChat, getWallFocusProject, __resetWallForTests,
} from '../wallStore';

function fakeSession(id: string, projectId?: string): Session {
  return { id, projectId, name: `chat-${id}`, status: 'active' } as unknown as Session;
}

beforeEach(() => {
  vi.useFakeTimers();
  __resetWallForTests();
});

afterEach(() => {
  vi.useRealTimers();
  vi.clearAllMocks();
});

describe('slotCount', () => {
  it('узкое окно даёт минимум одну колонку', () => {
    expect(slotCount(500)).toBe(1);
  });

  it('растёт с шириной монотонно', () => {
    expect(slotCount(1280)).toBe(2);
    expect(slotCount(1600)).toBe(3);
    expect(slotCount(2560)).toBe(5);
  });

  it('упирается в потолок MAX_SLOTS на сверхшироком', () => {
    expect(slotCount(10_000)).toBe(MAX_SLOTS);
  });
});

describe('мутации состава', () => {
  it('addChat добавляет в конец и ставит фокус', () => {
    addChat(fakeSession('a'));
    addChat(fakeSession('b'));
    expect(getWallState().chats.map(c => c.id)).toEqual(['a', 'b']);
    expect(getWallState().focusId).toBe('b');
  });

  it('addChat не дублирует уже взятый чат', () => {
    addChat(fakeSession('a'));
    addChat(fakeSession('a'));
    expect(getWallState().chats).toHaveLength(1);
  });

  it('removeChat чинит фокус на первый оставшийся', () => {
    addChat(fakeSession('a'));
    addChat(fakeSession('b'));
    removeChat('b');
    expect(getWallState().chats.map(c => c.id)).toEqual(['a']);
    expect(getWallState().focusId).toBe('a');
  });

  it('reorderChat переставляет монеты', () => {
    addChat(fakeSession('a'));
    addChat(fakeSession('b'));
    addChat(fakeSession('c'));
    reorderChat(2, 0);
    expect(getWallState().chats.map(c => c.id)).toEqual(['c', 'a', 'b']);
  });

  it('moveToVisible меняет скрытый чат с последней видимой колонкой и даёт фокус', () => {
    for (const id of ['a', 'b', 'c', 'd']) addChat(fakeSession(id));
    // 2 слота: видимые a,b; d — вне экрана
    moveToVisible('d', 2);
    expect(getWallState().chats.map(c => c.id)).toEqual(['a', 'd', 'c', 'b']);
    expect(getWallState().focusId).toBe('d');
  });

  it('moveToVisible по видимому чату просто фокусирует, состав не трогая', () => {
    for (const id of ['a', 'b', 'c']) addChat(fakeSession(id));
    moveToVisible('a', 2);
    expect(getWallState().chats.map(c => c.id)).toEqual(['a', 'b', 'c']);
    expect(getWallState().focusId).toBe('a');
  });
});

describe('дебаунс-PUT', () => {
  it('серия мутаций складывается в один PUT с итоговым составом', async () => {
    addChat(fakeSession('a'));
    addChat(fakeSession('b'));
    removeChat('a');
    expect(api.wall.put).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(600);
    expect(api.wall.put).toHaveBeenCalledTimes(1);
    expect(api.wall.put).toHaveBeenCalledWith(['b']);
  });

  it('ответ сервера после чистки применяется к составу', async () => {
    (api.wall.put as ReturnType<typeof vi.fn>).mockResolvedValueOnce({ chats: [fakeSession('b')] });
    addChat(fakeSession('a'));
    addChat(fakeSession('b'));
    await vi.advanceTimersByTimeAsync(600);
    expect(getWallState().chats.map(c => c.id)).toEqual(['b']);
  });
});

describe('гонка refresh против незавершённой мутации (дроп на док → вход на стену)', () => {
  it('фаза дебаунса: refresh не перетирает локально добавленный чат', async () => {
    (api.wall.get as ReturnType<typeof vi.fn>).mockResolvedValue({ chats: [] }); // сервер ещё не знает про чат
    addChat(fakeSession('new'));
    await refresh(); // GET вернул пустой состав, но таймер PUT ещё ждёт

    expect(getWallState().chats.map(c => c.id)).toEqual(['new']);
    // Таймер доработал — PUT ушёл с новым составом, а не с откатом
    await vi.advanceTimersByTimeAsync(600);
    expect(api.wall.put).toHaveBeenCalledWith(['new']);
  });

  it('фаза полёта: refresh во время незавершённого PUT тоже не перетирает', async () => {
    let resolvePut!: (v: { chats: Session[] }) => void;
    (api.wall.put as ReturnType<typeof vi.fn>).mockReturnValueOnce(new Promise(r => { resolvePut = r; }));
    (api.wall.get as ReturnType<typeof vi.fn>).mockResolvedValue({ chats: [] });

    addChat(fakeSession('new'));
    await vi.advanceTimersByTimeAsync(600); // таймер сработал, PUT завис в полёте
    await refresh(); // GET со старым пустым составом

    expect(getWallState().chats.map(c => c.id)).toEqual(['new']);
    resolvePut({ chats: [fakeSession('new')] });
  });
});

describe('live-статусы', () => {
  it('status_changed по чату набора кладётся в statuses и сильнее снимка', () => {
    initWall('u1');
    addChat(fakeSession('a'));

    // Проведённый в initWall обработчик onMessage
    const handler = (onMessage as ReturnType<typeof vi.fn>).mock.calls[0][0];
    handler({ type: 'status_changed', sessionId: 'a', status: 'working' });

    expect(getWallState().statuses.get('a')).toBe('working');
    expect(chatStatus(getWallState().chats[0])).toBe('working'); // снимок говорит active — live сильнее
  });

  it('status_changed по чужому чату игнорируется', () => {
    initWall('u1');
    addChat(fakeSession('a'));
    const handler = (onMessage as ReturnType<typeof vi.fn>).mock.calls[0][0];
    handler({ type: 'status_changed', sessionId: 'zzz', status: 'working' });
    expect(getWallState().statuses.size).toBe(0);
  });
});

describe('проект фокусной колонки (цвет титлбара окна)', () => {
  const proj = { id: 'p1', name: 'Проект' } as unknown as Project;

  async function loadWall() {
    // mockResolvedValue (не Once): refresh из initWall может доехать позже нашего —
    // с одинаковым ответом порядок перестаёт быть важным
    (api.wall.get as ReturnType<typeof vi.fn>).mockResolvedValue({
      chats: [fakeSession('a', 'p1'), fakeSession('b')],
    });
    (api.projects.list as ReturnType<typeof vi.fn>).mockResolvedValue([proj]);
    await refresh();
  }

  it('отдаёт проект чата, стоящего в фокусе', async () => {
    await loadWall();
    focusChat('a');
    expect(getWallFocusProject()).toBe(proj);
  });

  it('внепроектный чат в фокусе — null (красим акцентом)', async () => {
    await loadWall();
    focusChat('b');
    expect(getWallFocusProject()).toBeNull();
  });

  it('пустая стена — null', () => {
    expect(getWallFocusProject()).toBeNull();
  });

  it('снимок стабилен по ссылке: status_changed его не меняет (нет лишнего ререндера)', async () => {
    initWall('u1');
    await loadWall();
    focusChat('a');
    const before = getWallFocusProject();
    const handler = (onMessage as ReturnType<typeof vi.fn>).mock.calls[0][0];
    handler({ type: 'status_changed', sessionId: 'a', status: 'working' });
    expect(getWallFocusProject()).toBe(before);
  });
});
