// Тест навигации для раздела «Специальности» в режиме specialties:
// разбор и сборка хеша #/personas/specialties, алиас #/agents/specialties,
// и невывод personaId из зарезервированного сегмента «specialties».
//
// Контракт (см. parseHash/toHash в nav.ts):
//   #/personas/specialties  → screen=personas, personaView=specialties, БЕЗ personaId
//   #/agents/specialties    → то же (алиас)
//   #/personas/{id}         → screen=personas, personaId={id}
//   #/personas              → screen=personas (центральная зона раздела)
//   #/personas/specialties/automation  → screen=personas, personaView=automation
//
// Тест «падает при возврате присвоения personaId до резервирования сегмента» —
// если кто-то переставит ветки if в parseHash и сначала будет присваивать
// personaId из parts[1], то «specialties» уедет в personaId и personaView
// никогда не выставится. Здесь это ловится тем, что для '#/personas/specialties'
// результат НЕ содержит personaId, а personaView === 'specialties'.
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

describe('parseHash — режим «Специальности»', () => {
  it('#/personas/specialties → personaView=specialties, БЕЗ personaId', () => {
    const target = parseHash('#/personas/specialties');
    expect(target).toEqual({ screen: 'personas', personaView: 'specialties' });
    // Главное: personaId НЕ выводится из сегмента «specialties»
    expect(target?.personaId).toBeUndefined();
  });

  it('алиас #/agents/specialties работает так же (экран переименован, диплинки старые)', () => {
    const target = parseHash('#/agents/specialties');
    expect(target).toEqual({ screen: 'personas', personaView: 'specialties' });
    expect(target?.personaId).toBeUndefined();
  });

  it('«specialties» — зарезервированный сегмент, обычный id не путается с ним', () => {
    const target = parseHash('#/personas/special-id');
    expect(target?.screen).toBe('personas');
    expect(target?.personaId).toBe('special-id');
    // Если порядок проверок в parseHash сломают, parts[1]==='special-id' НЕ равен
    // 'specialties', поэтому personaView не выставится; этот инвариант и держим.
    expect(target?.personaView).toBeUndefined();
  });

  it('#/personas → центральная зона раздела, без personaId и без personaView', () => {
    const target = parseHash('#/personas');
    expect(target).toEqual({ screen: 'personas' });
    expect(target?.personaId).toBeUndefined();
    expect(target?.personaView).toBeUndefined();
  });

  it('#/personas/specialties/automation → вкладка автоматизации (спец-режим не съел её)', () => {
    // На '#/personas/specialties/automation':
    //   parts[1] === 'specialties' → сначала ставится personaView='specialties';
    //   parts[2] === 'automation' → затем personaView='automation' перетирает.
    // Итог — personaView === 'automation', personaId пуст.
    // (Точка наблюдения для регрессии: если убрать блок 'specialties' и оставить
    // только присвоение personaId, parts[2] нечем будет триггерить — тест провалится.)
    const target = parseHash('#/personas/specialties/automation');
    expect(target?.screen).toBe('personas');
    expect(target?.personaView).toBe('automation');
    expect(target?.personaId).toBeUndefined();
  });

  it('алиас #/agents/{id} → обычная персона, не специальности', () => {
    const target = parseHash('#/agents/abc-123');
    expect(target?.screen).toBe('personas');
    expect(target?.personaId).toBe('abc-123');
    expect(target?.personaView).toBeUndefined();
  });

  it('round-trip: parseHash(toHash для specialties) → тот же personaView', () => {
    // Проверка связки разбор↔сборка через единственный публичный канал — navPush.
    // Здесь только проверяем, что parseHash разбирает то, что отдаёт сборка.
    // Саму сборку отдельно проверяет describe ниже через pushState.
    // Сейчас — sanity: parseHash('#/personas/specialties') даёт personaView=specialties.
    const parsed = parseHash('#/personas/specialties');
    expect(parsed?.personaView).toBe('specialties');
  });
});

describe('navPush — сборка URL для режима «Специальности»', () => {
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

  it('navPush({ personaView: specialties }) → URL #/personas/specialties (без id)', () => {
    // Решение владельца: режим «Специальности» центральной зоны — отдельный путь,
    // БЕЗ сегмента персоны. Даже если в снапшоте зачем-то проставлен persona —
    // путь остаётся '/specialties', иначе кнопка «Назад» уведёт не туда.
    const s: NavSnapshot = { screen: 'personas', personaView: 'specialties', project: minimalProject };
    navPush(s);
    expect(pushStateMock).toHaveBeenCalledTimes(1);
    // Третий аргумент pushState — URL; берём его из первого вызова.
    const url = pushStateMock.mock.calls[0][2];
    expect(url).toBe('#/personas/specialties');
  });

  it('navPush({ persona: id }) → URL #/personas/{id}', () => {
    const s: NavSnapshot = { screen: 'personas', persona: 'abc-123', project: minimalProject };
    navPush(s);
    expect(pushStateMock.mock.calls[0][2]).toBe('#/personas/abc-123');
  });

  it('personaView имеет приоритет над persona в сборке — «specialties» резервирует сегмент', () => {
    // Если бы приоритет был обратный, toHash поставил бы '/{persona}' и режим
    // специальностей «потерялся» бы при сборке URL. Проверка сборки дополняет
    // проверку разбора: даже если вызывающий собрал снапшот странно, берётся
    // personaView, а не persona.
    const s: NavSnapshot = {
      screen: 'personas',
      persona: 'abc-123',
      personaView: 'specialties',
      project: minimalProject,
    };
    navPush(s);
    expect(pushStateMock.mock.calls[0][2]).toBe('#/personas/specialties');
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