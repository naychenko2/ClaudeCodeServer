// S2a-F6 (доработка по ревью 359990a8): тумблер «И для участников» в MCP-аккордеоне проекта.
// Механизм — общий fake-DOM (components/chat/__tests__/fakeDom) + createRoot/act; контролы
// НЕ подменены заглушками: проверяется раскрытый DOM с настоящим components/ui/Toggle
// (role=switch / aria-checked), поиск по aria-label и вызов onClick через reactProps.
// Проверяются решения секции: у кого тумблер есть и по какому состоянию (включая помеченный
// сервер вне mcpServersOn — пометку всегда можно снять, S2a-7), что уходит в PUT {id}/shared
// и что остаётся при отказе.
import { createElement } from 'react';
import type { ReactNode } from 'react';
import { act } from 'react';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  installFakeDom, uninstallFakeDom, mount, findNodeByAttr, textNodes, reactProps,
} from '../../../components/chat/__tests__/fakeDom';
import type { McpServer, Project } from '../../../types';

const apiMock = vi.hoisted(() => ({
  list: vi.fn(), setShared: vi.fn(), update: vi.fn(),
}));
vi.mock('../../../lib/api', () => ({
  api: { mcp: { list: apiMock.list, setShared: apiMock.setShared }, projects: { update: apiMock.update } },
}));
vi.mock('../../../lib/defaultPersona', () => ({ useMe: () => ({ userId: 'me-id' }) }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});
vi.mock('../../projects/dialogs/AccordionSection', () => ({
  AccordionSection: (p: { children: ReactNode }) => createElement('section', null, p.children),
}));

import { McpProjectSection } from '../McpProjectSection';

type FN = ReturnType<typeof mount>['container'];

// Данные: два сервера включены в проект (один уже помечен «для участников», другой нет),
// третий в mcpServersOn не входит и не помечен, четвёртый — помечен, но вне mcpServersOn
// (по семантике S2a-7 бэк доставит его в общие проекты: пометку должно быть чем снять).
// Проекты: свой общий (myRole owner + участники), личный (без участников), участник
// (myRole editor), старый бэк без myRole.
const srvOn = { id: 's1', key: 'srv1', label: 'github', enabled: true, sharedWithMembers: false } as McpServer;
const srvMarked = { id: 's2', key: 'srv2', label: 'tracker', enabled: true, sharedWithMembers: true } as McpServer;
const srvOff = { id: 's3', key: 'srv3', label: 'off-key', enabled: true, sharedWithMembers: false } as McpServer;
const srvMarkedOff = { id: 's4', key: 'srv4', label: 'marked-off', enabled: true, sharedWithMembers: true } as McpServer;

const ownShared = {
  id: 'p', name: 'П', rootPath: '/p', ownerId: 'me-id', updatedAt: '2026-10-30T00:00:00Z',
  myRole: 'owner', members: { count: 1, first: [] }, mcpServersOn: ['srv1', 'srv2'],
} as Project;
const plain = { ...ownShared, myRole: undefined, members: null } as Project;
const participant = { ...ownShared, myRole: 'editor' } as Project;
// Старый бэк без myRole: тот же результат по ownerId + составу (тот же приём, что в kindOf)
const legacyOwner = { ...ownShared, myRole: undefined } as Project;

let container: FN;

async function mountSection(project: Project) {
  const { container: c } = mount(createElement(McpProjectSection, { project }));
  container = c;
  // Эффект секции тянет реестр через api.mcp.list (мок) — промис дотекает в этом act
  await act(async () => {});
}

// Настоящий Toggle: узел по его aria-label; состояние — по aria-checked в раскрытом DOM
function switchNode(label: string): FN | null {
  return findNodeByAttr(container, 'aria-label', label);
}
function checkedAttr(label: string): string | null {
  return switchNode(label)?.getAttribute('aria-checked') ?? null;
}
async function flipSwitch(label: string) {
  const node = switchNode(label)!;
  const onClick = reactProps(node)!.onClick as () => void;
  await act(async () => { onClick(); });
}

beforeAll(() => { installFakeDom(); });
afterAll(() => { uninstallFakeDom(); });

beforeEach(() => {
  vi.clearAllMocks();
  apiMock.list.mockResolvedValue([srvOn, srvMarked, srvOff, srvMarkedOff]);
  apiMock.update.mockResolvedValue(ownShared);
});

describe('S2a-F6: тумблер «И для участников»', () => {
  it('владелец общего проекта: тумблер у включённых и у любых помеченных; состояние — из записи', async () => {
    await mountSection(ownShared);
    // включённый без пометки — есть и выключен; включённый с пометкой — есть и включён
    expect(switchNode('github для участников')).toBeTruthy();
    expect(checkedAttr('github для участников')).toBe('false');
    expect(checkedAttr('tracker для участников')).toBe('true');
    // помеченный вне mcpServersOn — тумблер тоже есть (иначе пометку нечем снять, S2a-7)
    expect(switchNode('marked-off для участников')).toBeTruthy();
    expect(checkedAttr('marked-off для участников')).toBe('true');
    // не входящий и не помеченный — без тумблера пометки (у него только основной)
    expect(switchNode('off-key для участников')).toBeNull();
    expect(switchNode('off-key в проекте')).toBeTruthy();
    // честная подсказка: пометка действует во всех общих проектах владельца
    expect(textNodes(container).join(' ')).toContain(
      'Включено — сервер доступен участникам во всех ваших общих проектах; без пометки он не доступен в их ходах.',
    );
  });

  it('помеченный вне mcpServersOn: тумблер виден и пометка снимается', async () => {
    apiMock.setShared.mockResolvedValue({ ...srvMarkedOff, sharedWithMembers: false });
    await mountSection(ownShared);
    expect(checkedAttr('marked-off для участников')).toBe('true');
    await flipSwitch('marked-off для участников');
    expect(apiMock.setShared).toHaveBeenCalledWith('s4', false);
    // Снятая пометка — истина: сервер вне mcpServersOn и не помечен → строка пометки
    // больше не рисуется (включить его в проект — отдельным тумблером)
    expect(switchNode('marked-off для участников')).toBeNull();
    // основной тумблер строки не меняется: сервер по-прежнему выключен в этом проекте
    expect(checkedAttr('marked-off в проекте')).toBe('false');
  });

  it('личный проект и участник — тумблера нет вовсе', async () => {
    await mountSection(plain);
    expect(switchNode('github для участников')).toBeNull();
    // и помеченный сервер в личном проекте без тумблера пометки не показывается:
    // здесь нет участников, а право менять пометку — только у владельца общего
    expect(switchNode('marked-off для участников')).toBeNull();
    expect(switchNode('github в проекте')).toBeTruthy(); // основной тумблер остаётся
    await mountSection(participant);
    expect(switchNode('github для участников')).toBeNull();
  });

  it('старый бэк без myRole: владелец по ownerId и составу — тумблер есть', async () => {
    await mountSection(legacyOwner);
    expect(switchNode('github для участников')).toBeTruthy();
  });

  it('переключение шлёт PUT {id}/shared с { sharedWithMembers:true }; ответ бэка становится истиной', async () => {
    apiMock.setShared.mockResolvedValue({ ...srvOn, sharedWithMembers: true });
    await mountSection(ownShared);
    await flipSwitch('github для участников');
    expect(apiMock.setShared).toHaveBeenCalledWith('s1', true);
    expect(checkedAttr('github для участников')).toBe('true');
  });

  it('отказ (403/сеть): тумблер возвращается в прежнее состояние, рядом тихая подпись', async () => {
    apiMock.setShared.mockRejectedValue({ status: 403, body: { error: 'Пометку ставит только владелец' } });
    await mountSection(ownShared);
    await flipSwitch('github для участников');
    expect(checkedAttr('github для участников')).toBe('false');
    expect(textNodes(container).join(' ')).toContain('Не удалось сохранить');
  });
});
