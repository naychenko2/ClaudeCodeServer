// Тест навигации раздела «Специальности» и раздела «Персоны».
//
// «Специальности» — САМОСТОЯТЕЛЬНЫЙ раздел хаба (#/specialties), а не режим
// «Персон»: переключатель «Персоны | Специальности» убран, вход — меню аватара.
// Отсюда контракт (см. parseHash/toHash в nav.ts):
//   #/specialties                    → screen=specialties
//   #/specialties/{roleKey}          → + specialtyKey
//   #/specialties/{roleKey}/edit     → + specialtyEdit
//   #/personas/specialties           → это ПЕРСОНА с id «specialties», спец-режима нет
//   #/personas/{id}                  → screen=personas, personaId={id}
//   #/personas/{id}/automation       → вкладка автоматизации студии
//
// toHash в nav.ts — приватная функция (используется только внутри navPush/navReplace).
// Тест сборки идёт через navPush: замокированный window.history.pushState получает
// готовый URL, и мы проверяем его. Так покрывается ОБЕ стороны контракта без
// необходимости править сам nav.ts.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { parseHash, navPush, type NavSnapshot } from '../nav';

// Минимальный снимок проекта — нужен только для экрана 'project', в этом тесте
// используется лишь как заполнитель типа (NavSnapshot.project обязателен).
const minimalProject = { id: 'p1' } as unknown as NavSnapshot['project'];

describe('parseHash — раздел «Специальности»', () => {
  it('#/specialties → витрина ролей, без ключа роли', () => {
    const target = parseHash('#/specialties');
    expect(target).toEqual({ screen: 'specialties' });
  });

  it('#/specialties/{roleKey} → визитка роли', () => {
    const target = parseHash('#/specialties/analyst');
    expect(target?.screen).toBe('specialties');
    expect(target?.specialtyKey).toBe('analyst');
    expect(target?.specialtyEdit).toBeUndefined();
  });

  it('#/specialties/{roleKey}/edit → экран настройки роли', () => {
    const target = parseHash('#/specialties/backendExecutor/edit');
    expect(target?.screen).toBe('specialties');
    expect(target?.specialtyKey).toBe('backendExecutor');
    expect(target?.specialtyEdit).toBe(true);
  });

  it('ключ роли URL-декодируется', () => {
    const target = parseHash('#/specialties/role%20key');
    expect(target?.specialtyKey).toBe('role key');
  });

  it('раздел «Персоны» больше не знает про сегмент «specialties»', () => {
    // Спец-режима внутри «Персон» нет: сегмент разбирается как обычный id персоны.
    const target = parseHash('#/personas/specialties');
    expect(target?.screen).toBe('personas');
    expect(target?.personaId).toBe('specialties');
    expect(target?.personaView).toBeUndefined();
  });
});

describe('parseHash — раздел «Персоны»', () => {
  it('#/personas/{id} → персона', () => {
    const target = parseHash('#/personas/special-id');
    expect(target?.screen).toBe('personas');
    expect(target?.personaId).toBe('special-id');
    expect(target?.personaView).toBeUndefined();
  });

  it('#/personas → центральная зона раздела, без personaId и без personaView', () => {
    const target = parseHash('#/personas');
    expect(target).toEqual({ screen: 'personas' });
  });

  it('#/personas/{id}/automation → вкладка автоматизации студии', () => {
    const target = parseHash('#/personas/abc-123/automation');
    expect(target?.screen).toBe('personas');
    expect(target?.personaId).toBe('abc-123');
    expect(target?.personaView).toBe('automation');
  });

  it('алиас #/agents/{id} → обычная персона (раздел переименован, диплинки старые)', () => {
    const target = parseHash('#/agents/abc-123');
    expect(target?.screen).toBe('personas');
    expect(target?.personaId).toBe('abc-123');
    expect(target?.personaView).toBeUndefined();
  });
});

describe('navPush — сборка URL', () => {
  // navPush использует window.history.pushState и window.dispatchEvent (NAV_CHANGE_EVENT).
  // В vitest node-окружении window нет — мокаем минимум, который нужен сборке URL.
  // window.history.pushState(s, '', url) — третий аргумент это URL, его мы и проверяем.

  let pushStateMock: ReturnType<typeof vi.fn>;
  let dispatchEventMock: ReturnType<typeof vi.fn>;
  let originalWindow: unknown;

  beforeEach(() => {
    pushStateMock = vi.fn();
    dispatchEventMock = vi.fn();
    originalWindow = (globalThis as { window?: unknown }).window;
    (globalThis as { window?: unknown }).window = {
      history: { pushState: pushStateMock, replaceState: vi.fn() },
      dispatchEvent: dispatchEventMock,
    };
  });

  afterEach(() => {
    (globalThis as { window?: unknown }).window = originalWindow;
    vi.restoreAllMocks();
  });

  it('navPush({ screen: specialties }) → URL #/specialties', () => {
    const s: NavSnapshot = { screen: 'specialties', project: minimalProject };
    navPush(s);
    expect(pushStateMock).toHaveBeenCalledTimes(1);
    // Третий аргумент pushState — URL; берём его из первого вызова.
    expect(pushStateMock.mock.calls[0][2]).toBe('#/specialties');
  });

  it('navPush({ specialty }) → URL #/specialties/{roleKey}', () => {
    const s: NavSnapshot = { screen: 'specialties', specialty: 'analyst' };
    navPush(s);
    expect(pushStateMock.mock.calls[0][2]).toBe('#/specialties/analyst');
  });

  it('navPush({ specialty, specialtyEdit }) → URL с хвостом /edit', () => {
    const s: NavSnapshot = { screen: 'specialties', specialty: 'analyst', specialtyEdit: true };
    navPush(s);
    expect(pushStateMock.mock.calls[0][2]).toBe('#/specialties/analyst/edit');
  });

  it('specialtyEdit без роли не даёт хвоста /edit — правится всегда конкретная роль', () => {
    const s: NavSnapshot = { screen: 'specialties', specialty: null, specialtyEdit: true };
    navPush(s);
    expect(pushStateMock.mock.calls[0][2]).toBe('#/specialties');
  });

  it('navPush({ persona: id }) → URL #/personas/{id}', () => {
    const s: NavSnapshot = { screen: 'personas', persona: 'abc-123', project: minimalProject };
    navPush(s);
    expect(pushStateMock.mock.calls[0][2]).toBe('#/personas/abc-123');
  });

  it('кодирование: personaId с зарезервированными символами → URL-кодируется', () => {
    const s: NavSnapshot = {
      screen: 'personas',
      persona: 'id with spaces',
      project: minimalProject,
    };
    navPush(s);
    expect(pushStateMock.mock.calls[0][2]).toBe('#/personas/id%20with%20spaces');
  });
});
