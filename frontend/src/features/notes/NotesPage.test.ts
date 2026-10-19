// Рендер-тест фильтров «Заметки» (NotesPage, мобильный режим): чи́пы-фильтры — общий
// ui/Chip variant="toggle" (волна 2 «Обзор фич», Ф3.1). Клик переключает фильтр:
// «Избранное», тег, статус комментария. Фильтр = оператор в строке поиска (tag:/status:).
//
// Окружение node (без jsdom): fakeDom + mount + reactProps. Мобильный режим (useIsMobile=true):
// чи́пы живут в сайдбаре (inline, без порталов/IslandScaffold). Проверка «по тексту и роли»:
// ищем чип по подписи, сверяем, что это <button>, кликаем и смотрим, что selected переключился
// (background transparent → var(--c-accent) — заливка C.accent по плану).

import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { NoteSummary } from '../../types';
import { NotesPage } from './NotesPage';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';

// fakeDom не экспортирует FNode — тип узла берём из сигнатуры reactProps.
type AnyNode = {
  nodeType: number; nodeName: string; nodeValue?: string;
  childNodes: AnyNode[]; style: Record<string, string>;
};
function textOf(n: AnyNode, out: string[] = []): string[] {
  if (n.nodeType === 3) out.push(n.nodeValue ?? '');
  for (const c of n.childNodes) textOf(c, out);
  return out;
}
function findButtonByText(container: AnyNode, text: string): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textOf(x).join(' ').includes(text)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// === Мок leaf-зависимостей (kit реэкспортирует из тех же модулей) ===
const { notesMock } = vi.hoisted(() => ({ notesMock: [] as NoteSummary[] }));
vi.mock('../../lib/notes', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/notes')>();
  return { ...orig, useNotes: () => notesMock, ensureNotesLoaded: async () => {} };
});
vi.mock('../../hooks/useOnline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../hooks/useOnline')>();
  return { ...orig, useOnline: () => true };
});
vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => true, useWindowWidth: () => 360 };
});
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, notes: { ...orig.api.notes, caps: async () => ({ semantic: false }), list: async () => [], semantic: async () => ({ results: [] }) } } };
});
// HubHeader (в NotesPage) подписывается на onMessage → строит реальное SignalR-соединение
// '/hubs/session', которое в node не резолвится. no-op-подписка, как в FeatureBannerSheet.test.
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});

function note(id: string, title: string, tags: string[], annotation?: NoteSummary['annotation']): NoteSummary {
  return {
    id, title, source: 'personal', sourceLabel: 'Личный', path: '',
    tags, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    annotation: annotation ?? null,
  };
}

function mountNotes() {
  const { root, container } = mount(
    createElement(NotesPage, {
      auth: { user: 'u', token: 't' } as never,
      onLogout: () => {},
      onHubTab: () => {},
    }),
  );
  return { root, container };
}

describe('NotesPage: клик по ui/Chip переключает фильтр', () => {
  beforeEach(() => {
    installFakeDom();
    // HubHeader читает localStorage напрямую (не под guard'ом) — в node его нет, stub.
    vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {} });
    // fake-window не несёт addEventListener — HubHeader вешает слушателей в эффекте.
    const w = (globalThis as { window: Record<string, unknown> }).window;
    if (typeof w.addEventListener !== 'function') { w.addEventListener = () => {}; w.removeEventListener = () => {}; }
    // parseHash читает window.location.hash; эффект consume читает sessionStorage — оба под stub.
    if (!w.location) w.location = { hash: '' };
    vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {} });
    notesMock.length = 0;
    notesMock.push(
      note('n1', 'Н1', ['идея', 'избранное']),
      note('n2', 'Н2', ['план']),
    );
  });
  afterEach(() => {
    notesMock.length = 0;
    vi.unstubAllGlobals();
    uninstallFakeDom();
  });

  it('«Избранное»: чип button; клик → selected (background var(--c-accent))', () => {
    const { root, container } = mountNotes();
    const btn = findButtonByText(container, 'Избранное');
    expect(btn, 'чип «Избранное» найден').not.toBeNull();
    expect(btn!.nodeName, 'роль — button').toBe('BUTTON');
    expect(btn!.style.background, 'до клика — не выбран').toBe('transparent');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background, 'после клика — selected, заливка C.accent').toBe('var(--c-accent)');
    root.unmount();
  });

  // Теги и статусы живут внутри CollapseGroup «Фильтры» (defaultOpen=false, условный
  // рендер) — сначала раскрываем группу кликом по её кнопке.
  function expandFilters(container: AnyNode) {
    act(() => {
      const toggle = findButtonByText(container, 'Фильтры');
      (reactProps(toggle!)!.onClick as () => void)();
    });
  }

  it('тег «#идея»: чип button; клик → selected', () => {
    const { root, container } = mountNotes();
    expandFilters(container);
    // «#» и имя тега — отдельные text-ноды (JSX #{tag} · {count}); в fake-DOM join(' ')
    // вставляет пробел, поэтому ищем по имени тега, а не по «#идея»
    const btn = findButtonByText(container, 'идея');
    expect(btn, 'чип тега «#идея» найден').not.toBeNull();
    expect(btn!.nodeName).toBe('BUTTON');
    expect(btn!.style.background).toBe('transparent');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background).toBe('var(--c-accent)');
    root.unmount();
  });

  it('статус комментария «Открытые»: чип button; клик → selected', () => {
    notesMock.push(note('n3', 'Н3', [], { status: 'open', isReply: false } as never));
    const { root, container } = mountNotes();
    expandFilters(container);
    const btn = findButtonByText(container, 'Открытые');
    expect(btn, 'чип «Открытые» найден').not.toBeNull();
    expect(btn!.nodeName).toBe('BUTTON');
    expect(btn!.style.background).toBe('transparent');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background).toBe('var(--c-accent)');
    root.unmount();
  });

  it('повторный клик снимает фильтр (selected → transparent)', () => {
    const { root, container } = mountNotes();
    expandFilters(container);
    const btn = findButtonByText(container, 'идея');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background).toBe('var(--c-accent)');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background, 'повторный клик — снято').toBe('transparent');
    root.unmount();
  });

  it('M1: на мобиле у чипов minHeight 40px (Избранное, тег, статусы комментариев)', () => {
    notesMock.push(note('n3', 'Н3', [], { status: 'open', isReply: false } as never));
    const { root, container } = mountNotes();
    // «Избранное» — всегда в сайдбаре (mobile: useIsMobile=true в этом тесте)
    const fav = findButtonByText(container, 'Избранное');
    expect(fav, 'чип «Избранное» найден').not.toBeNull();
    expect(fav!.style.minHeight, '«Избранное»: minHeight 40px').toBe('40px');
    // Раскрываем «Фильтры» → теги и статусы комментариев
    expandFilters(container);
    const tag = findButtonByText(container, 'идея');
    expect(tag, 'чип тега «#идея» найден').not.toBeNull();
    expect(tag!.style.minHeight, 'тег: minHeight 40px').toBe('40px');
    const open = findButtonByText(container, 'Открытые');
    expect(open, 'чип «Открытые» найден').not.toBeNull();
    expect(open!.style.minHeight, 'статус комментария: minHeight 40px').toBe('40px');
    root.unmount();
  });
});
