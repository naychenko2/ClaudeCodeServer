// NavTargetButton / NavTargetPair (волна 3, Ф9a, макет features-overview-3-final §9а,
// «Навигация в один клик»). Один компонент, два режима (chat/task) + пара «чат · задача».
//
// Состояния: chatId==null → «чата нет» (текст, не кнопка); chatArchived → «в архиве»;
// taskId==null → нет кнопки задачи; taskClosed → «закрыта». Навигацию задаёт вызывающий
// (onOpen) — переиспользует openChatById/openTaskInSection. На 360 тач-цель ≥ 40.
//
// Окружение node (без jsdom): fakeDom (mount) + spy onOpen. Мутации (в отчёте):
//   (1) перепутать chatId/taskId в NavTargetButton → «чата нет» краснеет;
//   (2) показывать кнопку при chatId==null → «чата нет» краснеет.

import { createElement } from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { NavTargetButton, NavTargetPair } from './NavTargetButton';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';

type FNodeLike = { nodeType: number; nodeName: string; childNodes: FNodeLike[]; attributes?: Record<string, string> };

// ── Рендер-хелперы ─────────────────────────────────────────────────────────────
function renderChat(over: Parameters<typeof NavTargetButton>[0] = {}) {
  installFakeDom();
  const onOpen = vi.fn();
  const { container } = mount(createElement(NavTargetButton, { kind: 'chat', onOpen, ...over }));
  return { container, onOpen };
}
function renderTask(over: Parameters<typeof NavTargetButton>[0] = {}) {
  installFakeDom();
  const onOpen = vi.fn();
  const { container } = mount(createElement(NavTargetButton, { kind: 'task', onOpen, ...over }));
  return { container, onOpen };
}
function renderPair(over: Parameters<typeof NavTargetPair>[0] = {}) {
  installFakeDom();
  const onOpenChat = vi.fn();
  const onOpenTask = vi.fn();
  const { container } = mount(createElement(NavTargetPair, { onOpenChat, onOpenTask, ...over }));
  return { container, onOpenChat, onOpenTask };
}

afterEach(() => { uninstallFakeDom(); vi.restoreAllMocks(); });

// ═══ 1. Кнопки: вид, иконка, клик → onOpen один раз ═════════════════════════════
describe('NavTargetButton: chat', () => {
  it('чат: кнопка «чат» (облачко), клик → onOpen один раз', () => {
    const { container, onOpen } = renderChat({ chatId: 'c1' });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(btn, 'нет кнопки чата').not.toBeNull();
    expect((btn as unknown as { nodeName: string }).nodeName).toBe('BUTTON');
    expect(textNodes(btn!).join('')).toContain('чат');
    (reactProps(btn!)!.onClick as () => void)();
    (reactProps(btn!)!.onClick as () => void)();
    expect(onOpen, 'клик не вызвал onOpen').toHaveBeenCalledTimes(2);
  });

  it('чат: aria-label с подсказкой (title) — скринридер слышит цель', () => {
    const { container } = renderChat({ chatId: 'c1', title: 'Открыть чат «Денис»' });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    expect((btn as unknown as { attributes: Record<string, string> }).attributes['aria-label']).toBe('Открыть чат «Денис»');
  });
});

describe('NavTargetButton: task', () => {
  it('задача: кнопка «задача» (список), клик → onOpen один раз', () => {
    const { container, onOpen } = renderTask({ taskId: 't1' });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-task');
    expect(btn, 'нет кнопки задачи').not.toBeNull();
    expect((btn as unknown as { nodeName: string }).nodeName).toBe('BUTTON');
    expect(textNodes(btn!).join('')).toContain('задача');
    (reactProps(btn!)!.onClick as () => void)();
    expect(onOpen, 'клик не вызвал onOpen').toHaveBeenCalledTimes(1);
  });
});

// ═══ 2. Состояния: «чата нет» / архив / нет задачи / закрыта ═══════════════════
describe('NavTargetButton: состояния', () => {
  it('chatId == null → «чата нет» (текст, НЕ кнопка)', () => {
    const { container } = renderChat({ chatId: null });
    const noChat = findNodeByAttr(container, 'data-testid', 'nav-target-no-chat');
    expect(noChat, 'нет «чата нет»').not.toBeNull();
    expect(textNodes(noChat!).join('')).toBe('чата нет');
    // Кнопки чата при этом нет.
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat'), 'кнопка чата при chatId==null').toBeNull();
  });

  // Мутация (1): перепутать chatId/taskId — компонент читает taskId (='t1') вместо
  // chatId (=null) → рисует кнопку, а не «чата нет» → тест краснеет.
  it('мутация: chatId==null, но taskId задан → всё равно «чата нет» (не taskId)', () => {
    const { container } = renderChat({ chatId: null, taskId: 't1' });
    const noChat = findNodeByAttr(container, 'data-testid', 'nav-target-no-chat');
    expect(noChat, '«чата нет» по chatId (не taskId)').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat'), 'кнопка чата по taskId (перепутано)').toBeNull();
  });

  // Мутация (2): показывать кнопку при chatId==null (игнорировать состояние) →
  // «чата нет» исчезает → тест краснеет.
  it('мутация: chatId==null → не кнопка (а не «всегда кнопка»)', () => {
    const { container } = renderChat({ chatId: null });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat'), 'кнопка при chatId==null (должна быть «чата нет»)').toBeNull();
  });

  it('chatArchived → пометка «в архиве» (пунктирная рамка, клик ведёт)', () => {
    const { container } = renderChat({ chatId: 'c1', chatArchived: true });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    expect(textNodes(btn).join('')).toContain('в архиве');
    // Пунктирная рамка (макет: «у цели пунктирная рамка»).
    expect((btn as unknown as { style: Record<string, string> }).style.border).toContain('dashed');
  });

  // minor 2: подпись и пометка НЕ сливаются — пробел в текстовом узле:
  // «чат в архиве» / «задача закрыта» (раньше «чатв архиве»/«задачазакрыта»).
  it('видимый текст: «чат в архиве» (пробел, не «чатв архиве»)', () => {
    const { container } = renderChat({ chatId: 'c1', chatArchived: true });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    expect(textNodes(btn).join(''), 'пробел между подписью и пометкой').toBe('чат в архиве');
  });
  it('видимый текст: «задача закрыта» (пробел, не «задачазакрыта»)', () => {
    const { container } = renderTask({ taskId: 't1', taskClosed: true });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-task')!;
    expect(textNodes(btn).join(''), 'пробел между подписью и пометкой').toBe('задача закрыта');
  });

  // Ф9b: пометка слышна скринридеру ВСЕГДА — и когда вызывающий передал свой title
  // (на страницах фичи title передан везде), и на 360, где видимой подписи нет.
  it('aria-label несёт пометку при заданном title: «в архиве» / «задача закрыта»', () => {
    const chat = renderChat({ chatId: 'c1', chatArchived: true, title: 'Открыть чат. Страница фичи скроется.' });
    const cb = findNodeByAttr(chat.container, 'data-testid', 'nav-target-chat')!;
    expect((cb as unknown as { attributes: Record<string, string> }).attributes['aria-label'])
      .toBe('Открыть чат. Страница фичи скроется. (чат в архиве)');
    const task = renderTask({ taskId: 't1', taskClosed: true, title: 'Открыть задачу' });
    const tb = findNodeByAttr(task.container, 'data-testid', 'nav-target-task')!;
    expect((tb as unknown as { attributes: Record<string, string> }).attributes['aria-label']).toBe('Открыть задачу (задача закрыта)');
  });

  it('без пометок aria-label — чистая подсказка; на 360 пометка только в aria (текста нет)', () => {
    const plain = renderChat({ chatId: 'c1', title: 'Открыть чат' });
    const pb = findNodeByAttr(plain.container, 'data-testid', 'nav-target-chat')!;
    expect((pb as unknown as { attributes: Record<string, string> }).attributes['aria-label']).toBe('Открыть чат');
    const mob = renderChat({ chatId: 'c1', chatArchived: true, isMobile: true });
    const mb = findNodeByAttr(mob.container, 'data-testid', 'nav-target-chat')!;
    expect(textNodes(mb).join('')).toBe('');
    expect((mb as unknown as { attributes: Record<string, string> }).attributes['aria-label']).toBe('Открыть чат (чат в архиве)');
  });

  it('taskId == null → кнопки задачи нет (null)', () => {
    const { container } = renderTask({ taskId: null });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task'), 'кнопка задачи при taskId==null').toBeNull();
  });

  it('taskClosed → пометка «закрыта» (клик ведёт, пометка вторичная)', () => {
    const { container } = renderTask({ taskId: 't1', taskClosed: true });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-task')!;
    expect(textNodes(btn).join('')).toContain('закрыта');
    expect((btn as unknown as { style: Record<string, string> }).style.border).toContain('dashed');
  });
});

// ═══ 3. Пара «чат · задача» (участник) ═════════════════════════════════════════
describe('NavTargetPair', () => {
  it('чат + задача: обе кнопки рядом', () => {
    const { container } = renderPair({ chatId: 'c1', taskId: 't1' });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat'), 'кнопка чата').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task'), 'кнопка задачи').not.toBeNull();
  });

  it('чат → onOpenChat, задача → onOpenTask (свои переходы)', () => {
    const { container, onOpenChat, onOpenTask } = renderPair({ chatId: 'c1', taskId: 't1' });
    (reactProps(findNodeByAttr(container, 'data-testid', 'nav-target-chat')!)!.onClick as () => void)();
    (reactProps(findNodeByAttr(container, 'data-testid', 'nav-target-task')!)!.onClick as () => void)();
    expect(onOpenChat).toHaveBeenCalledTimes(1);
    expect(onOpenTask).toHaveBeenCalledTimes(1);
  });

  it('chatId == null → «чата нет» (showNoChat), задача на месте', () => {
    const { container } = renderPair({ chatId: null, taskId: 't1' });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-no-chat'), '«чата нет»').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task'), 'кнопка задачи').not.toBeNull();
  });

  it('taskId == null → нет кнопки задачи (чат на месте)', () => {
    const { container } = renderPair({ chatId: 'c1', taskId: null });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat'), 'кнопка чата').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task'), 'кнопка задачи при taskId==null').toBeNull();
  });

  it('архивный чат в паре → «в архиве»', () => {
    const { container } = renderPair({ chatId: 'c1', chatArchived: true, taskId: 't1' });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    expect(textNodes(btn).join('')).toContain('в архиве');
  });
});

// ═══ 4. 360: тач-цель ≥ 44 (minHeight 44) ═══════════════════════════════════════
describe('NavTargetButton: 360', () => {
  it('на 360 кнопка чата — minHeight 44 (тач-цель)', () => {
    const { container } = renderChat({ chatId: 'c1', isMobile: true });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    expect((btn as unknown as { style: Record<string, string> }).style.minHeight).toBe('44px');
  });
  it('на 360 кнопка задачи — minHeight 44 (тач-цель)', () => {
    const { container } = renderTask({ taskId: 't1', isMobile: true });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-task')!;
    expect((btn as unknown as { style: Record<string, string> }).style.minHeight).toBe('44px');
  });
  it('десктоп (360=false) — без minHeight 44 (высота 24)', () => {
    const { container } = renderChat({ chatId: 'c1' });
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    expect((btn as unknown as { style: Record<string, string> }).style.height).toBe('24px');
  });
});

// ═══ 5. Клавиатура: цель — настоящий <button> (Enter/Space работают нативно) ═══
describe('NavTargetButton: клавиатура', () => {
  it('чат и задача — настоящие <button> (Enter/Space нативны)', () => {
    const { container: c1 } = renderChat({ chatId: 'c1' });
    expect((findNodeByAttr(c1, 'data-testid', 'nav-target-chat') as unknown as { nodeName: string }).nodeName).toBe('BUTTON');
    const { container: c2 } = renderTask({ taskId: 't1' });
    expect((findNodeByAttr(c2, 'data-testid', 'nav-target-task') as unknown as { nodeName: string }).nodeName).toBe('BUTTON');
  });
});
