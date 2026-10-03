import { beforeEach, describe, expect, it } from 'vitest';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;

import type { Project, ServerMessage } from '../../types';
import {
  __resetHandsStrip, getHandsSession, handsProjectInfo, handsStripAvailable, handsStatusLoaded, handsStripOnMessage, setHandsProject,
} from './handsStrip';

const P = 'p1';
const S = 's1';

const hands = (state: string, reason?: string) =>
  ({ sessionId: S, type: 'hands_status', state, reason }) as ServerMessage;

const avail = () => handsStripAvailable({ projectId: P, sessionId: S });
const state = () => {
  const st = getHandsSession(S).state;
  return st.kind === 'status' ? st.status.state : st.kind;
};

beforeEach(() => {
  store.clear();
  __resetHandsStrip();
  setHandsProject(P, { available: true, deviceName: 'Ноутбук' });
});

describe('руки чата — состояние из событий', () => {
  it('hands_status меняет состояние чата, первая отрисовка тоже', () => {
    handsStripOnMessage(S, hands('active'));
    expect(state()).toBe('active');
    handsStripOnMessage(S, hands('stopped', 'tray-stop'));
    expect(state()).toBe('stopped');
  });

  it('первая отрисовка с active: состояние выставлено', () => {
    handsStatusLoaded(S, { state: 'active', reason: null, deviceName: 'Ноутбук' });
    expect(state()).toBe('active');
  });

  it('событие чужого чата состояние не трогает', () => {
    handsStripOnMessage(S, { ...hands('active'), sessionId: 'other' } as ServerMessage);
    expect(state()).not.toBe('active');
  });
});

describe('руки чата — доступность пилюли', () => {
  it('при handsRefusal проекта рук нет', () => {
    setHandsProject(P, { available: false, deviceName: 'Ноутбук' });
    expect(avail()).toBe(false);
  });

  it('сервер ответил «у чата рук нет» — пилюли нет', () => {
    handsStatusLoaded(S, { state: null });
    expect(avail()).toBe(false);
  });

  it('руки проекта доступны и сервер не отказал — пилюля есть', () => {
    expect(avail()).toBe(true);
  });

  it('личный чат вне проекта (projectId = null) — рук нет, даже когда руки чата активны', () => {
    handsStripOnMessage(S, hands('active'));
    expect(handsStripAvailable({ projectId: P, sessionId: S })).toBe(true);
    expect(handsStripAvailable({ projectId: null, sessionId: S })).toBe(false);
  });
});

// Проект в том виде, в каком его сейчас отдаёт GET /api/projects: руки решают
// тумблер handsEnabled и отказ матрицы handsRefusal. Ни флага local-hands, ни списка
// провайдеров рук в ответе больше нет
const dto = (patch: Partial<Project> = {}): Project => ({
  id: P, name: 'Test local', rootPath: 'c:/Sources/test', createdAt: '', updatedAt: '',
  deviceId: 'd1', handsEnabled: true, handsRefusal: null,
  device: { id: 'd1', name: 'WORK', online: true, platform: 'windows', harnessReady: true, harnessProblem: null },
  ...patch,
}) as unknown as Project;

describe('руки чата — из DTO проекта', () => {
  it('hands_status=active: пилюля доступна без флага и провайдеров', () => {
    setHandsProject(P, handsProjectInfo(dto()));
    handsStripOnMessage(S, hands('active'));
    expect(avail()).toBe(true);
    expect(state()).toBe('active');
  });

  it('руки включили тумблером в открытом чате: свежий DTO проекта открывает пилюлю', () => {
    // Чат открыт до тумблера: чаты видят проект с выключенными руками
    setHandsProject(P, handsProjectInfo(dto({ handsEnabled: false })));
    expect(avail()).toBe(false);
    // Сохранение секции «Руки на устройстве» доносит свежий DTO до чатов (App → WorkspacePage)
    setHandsProject(P, handsProjectInfo(dto()));
    expect(avail()).toBe(true);
  });

  it('отказ матрицы при включённом тумблере — пилюли нет', () => {
    setHandsProject(P, handsProjectInfo(dto({ handsRefusal: 'Агент устройства устарел' })));
    expect(avail()).toBe(false);
  });
});
