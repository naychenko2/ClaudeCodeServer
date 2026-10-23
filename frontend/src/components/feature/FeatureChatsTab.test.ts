// Вкладка «Чаты» страницы фичи (волна 3, Ф3; макет features-overview-3-final §4/§9а).
//
// Проводка данных — рендер вкладки с НАСТОЯЩИМИ chats[] (FeatureChatLink), не прямой проп:
//   • строка: имя персоны, роль (roleLabel; исполнитель — «Ш2 · фронт»), чип модели
//     (ModelChip: modelLabel + подсказка modelFull), «ключевой» (kind=key / keySessionId),
//     пара «чат · задача» (NavTargetPair: chatId=chat.id, taskId);
//   • порядок: ключевой → куратор/оркестратор → исполнители по шагам плана;
//   • «Архивные · N»: свёрнуты (aria-expanded=false, тела нет) до раскрытия;
//   • пустые: нет чатов → «У фичи пока нет чатов»; чата нет в снимке → «чата нет».
//
// Мутации (в отчёте):
//   (1) убрать сортировку (sortFeatureChats → как есть) → «порядок строк» краснеет;
//   (2) не сворачивать архивные (всегда рисовать тело) → «архив свёрнут» краснеет;
//   (3) потерять taskId в NavTargetPair → «задача: taskId» краснеет;
//   (4) чип без подсказки по тапу (isMobile) → «чип: подсказка по тапу» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureChatsTab, sortFeatureChats } from './FeatureChatsTab';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { __test as backNavTest } from '../../lib/featureBackNav';
import type { FeaturePage } from '../../types/featureIndex';
import type { FeatureChatLink } from '../../types/featureChats';

// === Мок персон (lib/personas): id → имя; аватар — инициалы (без image) ===
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) =>
    personaMap.has(id) ? { id, name: personaMap.get(id), avatar: { kind: 'initials' as const } } : undefined,
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
// lib/api — только avatarUrl (PersonaAvatar); остальное вкладкой не зовётся.
vi.mock('../../lib/api', () => ({ api: { personas: { avatarUrl: () => '' } } }));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));
// Навигация: журнал вызовов (chat / task).
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } }));
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

// ── Хелперы данных ──────────────────────────────────────────────────────────────
function chat(over: Partial<FeatureChatLink> = {}): FeatureChatLink {
  return {
    id: 'c1', kind: 'executor', taskId: null, archived: false,
    personaId: null, taskRole: null, stepRef: null, lane: null,
    modelLabel: null, modelFull: null,
    ...over,
  };
}

function pageWith(chats: FeatureChatLink[]): FeaturePage {
  return { run: {} as FeaturePage['run'], events: [], eventsTotal: 0, chats };
}

// Корни всех mount'ов: unmount в afterEach (иначе два живых корня дёргают общий стор).
const roots: import('react-dom/client').Root[] = [];
function render(props: { page: FeaturePage; keySessionId?: string | null; isMobile?: boolean }) {
  installFakeDom();
  const { root, container } = mount(createElement(FeatureChatsTab, {
    page: props.page,
    keySessionId: props.keySessionId ?? null,
    featureId: 'f1',
    isMobile: props.isMobile ?? false,
  }));
  roots.push(root);
  return container;
}
function unmountAll() {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
}

beforeEach(() => {
  personaMap.clear();
  personaMap.set('p-kira', 'Кира');
  personaMap.set('p-denis', 'Денис');
  personaMap.set('p-mark', 'Марк');
  mockOpenChat.calls = [];
  mockOpenTask.calls = [];
  backNavTest.reset();
});
afterEach(() => {
  unmountAll();
  uninstallFakeDom();
  vi.restoreAllMocks();
});

// ═══ 1. Проводка данных: строка чата (имя, роль, чип, «ключевой», «чат · задача») ═══
describe('Строка чата (макет §9а)', () => {
  it('имя персоны, роль roleLabel, чип модели, «ключевой», пара «чат · задача»', () => {
    const c = chat({
      id: 'key', kind: 'curator', taskId: 't-epic', personaId: 'p-kira',
      taskRole: 'step', stepRef: 'Ш2', lane: 'front', modelLabel: 'Opus', modelFull: 'Opus 5.5',
    });
    const container = render({ page: pageWith([c]), keySessionId: 'key' });

    // Имя персоны.
    const name = findNodeByAttr(container, 'data-testid', 'chat-name');
    expect(name).not.toBeNull();
    expect(textNodes(name!).join('')).toBe('Кира');

    // Роль: исполнитель → «Ш2 · фронт» (roleLabel).
    const role = findNodeByAttr(container, 'data-testid', 'chat-role');
    expect(role).not.toBeNull();
    expect(textNodes(role!).join('')).toBe('Ш2 · фронт');

    // Чип модели: modelLabel виден, modelFull — в подсказке.
    const chip = findNodeByAttr(container, 'data-testid', 'timeline-model-chip');
    expect(chip).not.toBeNull();
    expect(textNodes(chip!).join('')).toBe('Opus');

    // «Ключевой»: id = keySessionId → пометка.
    const key = findNodeByAttr(container, 'data-testid', 'chat-key');
    expect(key).not.toBeNull();
    expect(textNodes(key!).join('')).toBe('ключевой');

    // Пара «чат · задача»: чат = chat.id, задача = taskId.
    const chatBtn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(chatBtn).not.toBeNull();
    const taskBtn = findNodeByAttr(container, 'data-testid', 'nav-target-task');
    expect(taskBtn).not.toBeNull();
  });

  it('чат без taskId — кнопки задачи нет (NavTargetPair: taskId=null)', () => {
    const c = chat({ id: 'no-task', kind: 'curator', taskId: null, personaId: 'p-denis' });
    const container = render({ page: pageWith([c]) });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).toBeNull();
  });

  it('чат без персоны и без модели — строка без аватара-имени и без чипа', () => {
    const c = chat({ id: 'bare', kind: 'orchestrator', taskId: 't1' });
    const container = render({ page: pageWith([c]) });
    expect(findNodeByAttr(container, 'data-testid', 'chat-name')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'timeline-model-chip')).toBeNull();
  });
});

// ═══ 2. Порядок: ключевой → куратор/оркестратор → исполнители по шагам плана ═══════
describe('Порядок строк (макет §9а)', () => {
  it('ключевой → куратор → оркестратор → исполнители по номеру шага', () => {
    // Ввод в ПЕРЕМЕШАННОМ порядке: сортировка должна выстроить по правилу.
    const chats = [
      chat({ id: 'exec2', kind: 'executor', stepRef: 'Ш2', lane: 'front', personaId: 'p-kira' }),
      chat({ id: 'orch', kind: 'orchestrator', personaId: 'p-mark' }),
      chat({ id: 'exec1', kind: 'executor', stepRef: 'Ш1', lane: 'back', personaId: 'p-denis' }),
      chat({ id: 'key', kind: 'curator', personaId: 'p-kira' }),
    ];
    const sorted = sortFeatureChats(chats, 'key');
    expect(sorted.map(c => c.id)).toEqual(['key', 'orch', 'exec1', 'exec2']);
  });

  it('рендер: порядок строк = отсортированный (мутация: убрать сортировку → red)', () => {
    const chats = [
      chat({ id: 'exec2', kind: 'executor', stepRef: 'Ш2', lane: 'front', personaId: 'p-kira' }),
      chat({ id: 'orch', kind: 'orchestrator', personaId: 'p-mark' }),
      chat({ id: 'exec1', kind: 'executor', stepRef: 'Ш1', lane: 'back', personaId: 'p-denis' }),
      chat({ id: 'key', kind: 'curator', personaId: 'p-kira' }),
    ];
    const container = render({ page: pageWith(chats), keySessionId: 'key' });
    // Собираем id строк в порядке DOM (обход контейнера).
    const order: string[] = [];
    const walk = (x: { nodeType: number; childNodes: unknown[] }) => {
      for (const c of x.childNodes as { nodeType: number; getAttribute: (a: string) => string | null }[]) {
        if (c.nodeType === 1) {
          const t = c.getAttribute('data-testid');
          if (t && t.startsWith('chat-row-')) order.push(t.slice('chat-row-'.length));
        }
        walk(c);
      }
    };
    walk(container);
    expect(order).toEqual(['key', 'orch', 'exec1', 'exec2']);
  });
});

// ═══ 3. «Архивные · N»: свёрнуты, раскрытие по клику/клавиатуре ═══════════════════
describe('«Архивные · N» (макет §9а)', () => {
  it('архивные свёрнуты: aria-expanded=false, тела нет; нет архивных — нет раздела', () => {
    const live = chat({ id: 'live', kind: 'curator', personaId: 'p-kira' });
    const arch = chat({ id: 'arch', kind: 'executor', archived: true, personaId: 'p-denis' });
    const container = render({ page: pageWith([live, arch]) });

    // Раздел есть (архивных = 1).
    const toggle = findNodeByAttr(container, 'data-testid', 'chats-archived-toggle');
    expect(toggle).not.toBeNull();
    expect(toggle!.getAttribute('aria-expanded')).toBe('false');
    // Тело архива ещё не нарисовано (свёрнуто).
    expect(findNodeByAttr(container, 'data-testid', 'chats-archived-body')).toBeNull();
    // Подпись «Архивные · 1».
    expect(textNodes(toggle!).join('')).toContain('Архивные · 1');
  });

  it('нет архивных — раздела нет (мутация: не сворачивать → red)', () => {
    const container = render({ page: pageWith([chat({ id: 'a', kind: 'curator' }), chat({ id: 'b', kind: 'orchestrator' })]) });
    expect(findNodeByAttr(container, 'data-testid', 'chats-archived-toggle')).toBeNull();
  });

  it('раскрытие по клику: aria-expanded=true, тело с архивными строками', async () => {
    const live = chat({ id: 'live', kind: 'curator', personaId: 'p-kira' });
    const arch = chat({ id: 'arch', kind: 'executor', archived: true, personaId: 'p-denis' });
    const container = render({ page: pageWith([live, arch]) });

    const toggle = findNodeByAttr(container, 'data-testid', 'chats-archived-toggle')!;
    const onClick = reactProps(toggle)!.onClick as () => void;
    await act(async () => { onClick(); });

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const body = findNodeByAttr(container, 'data-testid', 'chats-archived-body');
    expect(body).not.toBeNull();
    // Архивная строка нарисована в теле.
    expect(findNodeByAttr(container, 'data-testid', 'chat-row-arch')).not.toBeNull();
  });
});

// ═══ 3а. Разделение live/archived + пометка «в архиве» (макет §9а) ═══════════════
describe('Разделение live/archived + «в архиве»', () => {
  it('live — в основном списке, archived — только в свёрнутом разделе (мутация: не разделять → red)', () => {
    const live = chat({ id: 'live', kind: 'curator', personaId: 'p-kira' });
    const arch = chat({ id: 'arch', kind: 'executor', archived: true, personaId: 'p-denis' });
    const container = render({ page: pageWith([live, arch]) });
    // Живой чат — в основном списке.
    expect(findNodeByAttr(container, 'data-testid', 'chat-row-live')).not.toBeNull();
    // Архивный чат — НЕ в основном списке: его строка появляется только после раскрытия
    // раздела «Архивные» (свёрнут → строки нет в DOM).
    expect(findNodeByAttr(container, 'data-testid', 'chat-row-arch')).toBeNull();
    // Раздел «Архивные · 1» есть.
    expect(findNodeByAttr(container, 'data-testid', 'chats-archived-toggle')).not.toBeNull();
  });

  it('архивный чат → пометка «в архиве» на кнопке «чат» (мутация: убрать chatArchived → red)', async () => {
    const c = chat({ id: 'arch', kind: 'executor', archived: true, personaId: 'p-denis' });
    const container = render({ page: pageWith([c]) });
    // Архивный чат — в свёрнутом разделе: раскрываем, чтобы увидеть кнопку «чат».
    const toggle = findNodeByAttr(container, 'data-testid', 'chats-archived-toggle')!;
    await act(async () => { (reactProps(toggle)!.onClick as () => void)(); });
    const chatBtn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    expect(textNodes(chatBtn).join('')).toContain('в архиве');
    // Живой (не архивный) чат — без пометки.
    const live = chat({ id: 'live', kind: 'curator', personaId: 'p-kira' });
    const c2 = render({ page: pageWith([live]) });
    const liveBtn = findNodeByAttr(c2, 'data-testid', 'nav-target-chat')!;
    expect(textNodes(liveBtn).join('')).not.toContain('в архиве');
  });

  // Ревью Ф9b, зам. 3: текст подсказки берёт архивность из marks (тот же источник, что
  // индикатор), а не из chat.archived. Мутация «вернуться на chat.archived» красит тест:
  // сам чат в снимке не архивный, но агрегат (исполнитель стадии) говорит — архивный.
  it('подсказка «Чат в архиве» — из marks, не из chat.archived (мутация: взять chat.archived → red)', () => {
    const c = chat({ id: 'c1', kind: 'executor', personaId: 'p-denis', archived: false });
    const page: FeaturePage = {
      run: {
        featureId: 'f1',
        epic: { id: 'f1', title: 'Фича', status: 'doing' },
        stages: [{ key: 'prod', state: 'active', tone: null, passes: 1, taskIds: [], rounds: [],
          executors: [{ personaId: 'p-denis', taskId: null, taskRole: null, stepRef: null, lane: null, status: 'done', chatId: 'c1', chatArchived: true, modelLabel: null, modelFull: null }] }],
      } as unknown as FeaturePage['run'],
      events: [], eventsTotal: 0, chats: [c],
    };
    const container = render({ page });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(btn, 'кнопка «чат»').not.toBeNull();
    expect(String(reactProps(btn!)!.title), 'подсказка не взяла архивность из marks').toContain('Чат в архиве');
  });
});

// ═══ 4. Навигация: «чат · задача» (taskId) ════════════════════════════════════════
describe('Навигация «чат · задача» (макет §9а)', () => {
  it('клик «задача» → openTaskInSection(taskId); клик «чат» → openChatById(chat.id)', async () => {
    const c = chat({ id: 'c-1', kind: 'executor', taskId: 't-9', personaId: 'p-kira' });
    const container = render({ page: pageWith([c]) });

    const taskBtn = findNodeByAttr(container, 'data-testid', 'nav-target-task')!;
    const taskClick = reactProps(taskBtn)!.onClick as () => void;
    await act(async () => { taskClick(); });
    expect(mockOpenTask.calls).toEqual([{ id: 't-9' }]);

    const chatBtn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    const chatClick = reactProps(chatBtn)!.onClick as () => void;
    await act(async () => { chatClick(); });
    expect(mockOpenChat.calls).toEqual(['c-1']);
  });

  it('мутация: потерять taskId в NavTargetPair → кнопки задачи нет (red)', () => {
    // taskId=null → NavTargetPair не рисует «задача» (hasTask=false).
    const c = chat({ id: 'c-2', kind: 'executor', taskId: null, personaId: 'p-denis' });
    const container = render({ page: pageWith([c]) });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).toBeNull();
  });
});

// ═══ 5. Чип модели: подсказка modelFull по ТАПУ (360) ═════════════════════════════
describe('Чип модели: подсказка по тапу (макет §9а)', () => {
  it('тап по чипу (360) поднимает подсказку с modelFull', async () => {
    const c = chat({ id: 'c-model', kind: 'executor', personaId: 'p-kira', modelLabel: 'Opus', modelFull: 'Opus 5.5' });
    const container = render({ page: pageWith([c]), isMobile: true });

    const chip = findNodeByAttr(container, 'data-testid', 'timeline-model-chip')!;
    const onClick = reactProps(chip)!.onClick as (() => void) | undefined;
    expect(onClick, 'у чипа на 360 должен быть onClick (тап)').toBeDefined();
    await act(async () => { onClick!(); });

    // Подсказка — портал в document.body (role="tooltip") с полным именем модели.
    const body = (globalThis as any).document.body;
    const tip = findNodeByAttr(body, 'role', 'tooltip');
    expect(tip, 'подсказка по тапу').not.toBeNull();
    expect(textNodes(tip!).join('')).toContain('Opus 5.5');
  });
});

// ═══ 6. Пустые состояния ═════════════════════════════════════════════════════════
describe('Пустые состояния', () => {
  it('нет чатов → «У фичи пока нет чатов»', () => {
    const container = render({ page: pageWith([]) });
    const empty = findNodeByAttr(container, 'data-testid', 'chats-empty');
    expect(empty).not.toBeNull();
    expect(textNodes(empty!).join('')).toBe('У фичи пока нет чатов');
  });

  it('чат удалён/нет в снимке → «чата нет» (NavTargetButton, неактивна)', () => {
    // Контракт: чат в списке, но его id не в снимке → chatId=null → «чата нет».
    // В нашей модели chat.id всегда есть; «чата нет» — когда NavTargetPair получает chatId=null.
    // Проверяем через NavTargetButton-поведение: строка с taskId, но «чат» — текст.
    const c = chat({ id: 'c-x', kind: 'curator', taskId: 't1' });
    const container = render({ page: pageWith([c]) });
    // Чат есть (id не null) → кнопка «чат», не «чата нет».
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-no-chat')).toBeNull();
  });
});
