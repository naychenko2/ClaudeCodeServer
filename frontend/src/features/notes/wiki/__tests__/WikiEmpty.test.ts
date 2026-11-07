// Рендер-тест «Вики пока пустая» (LI-3): заголовок и первая строка — всегда; вторая
// строка — по состоянию ночного разбора из стора настроек (стор мокан на уровне хука:
// состояние приходит из него, нового запроса компонент не делает); кнопки «Завести вики»
// нет ни в одном состоянии; переход к «Настройкам» — только когда он достижим (off +
// колбэк вызывающего).
//
// Мутации этого прогона (вывод — в отчёт задачи):
// (1) вернуть в компоненте кнопку «Завести вики» → «ни в одном состоянии» краснеет;
// (2) убрать условие второй строки (показывать её всегда при снимке) → «нет настроек —
//     второй строки нет» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiEmpty } from '../WikiEmpty';

// Стор настроек — мок на уровне хука (тот же приём, что NotesPage.test): компонент
// читает только снимок, запрос к нему — не его работа
const { settingsMock } = vi.hoisted(() => ({ settingsMock: { value: null as unknown } }));
vi.mock('../../../../lib/wiki', async (orig) => ({
  ...await orig<typeof import('../../../../lib/wiki')>(),
  useWikiSettings: () => settingsMock.value,
}));

const settings = (enabled: boolean): Record<string, unknown> => ({
  autoApplyEnabled: false, classes: [],
  nightly: { enabled, windowStartHour: 3, windowEndHour: 6, maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10, inputTokenBudget: 700000 },
});

const roots: Root[] = [];

function renderEmpty(onOpenSettings?: () => void) {
  installFakeDom();
  const { root, container } = mount(createElement(WikiEmpty, { onOpenSettings }));
  roots.push(root);
  return container;
}

type Cont = ReturnType<typeof mount>['container'];

function texts(c: Cont): string[] { return textNodes(c).map(t => t.trim()).filter(Boolean); }
function has(c: Cont, needle: string): boolean { return texts(c).join(' ').includes(needle); }

function clickTestId(c: Cont, testId: string) {
  const node = findNodeByAttr(c, 'data-testid', testId);
  if (!node) throw new Error(`нет узла data-testid=${testId}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у ${testId} нет onClick`);
  act(() => { props.onClick(undefined); });
}

afterEach(() => {
  for (const r of roots.splice(0)) act(() => r.unmount());
  uninstallFakeDom();
  settingsMock.value = null;
});

describe('«Вики пока пустая» (LI-3)', () => {
  it('настроек ещё нет в сторе: первая строка есть, второй строки и кнопки нет', () => {
    settingsMock.value = null;
    const c = renderEmpty();
    expect(has(c, 'Вики пока пустая')).toBe(true);
    expect(has(c, 'Она наполнится сама, когда вы разберёте чат или перенесёте заметку')).toBe(true);
    // состояния ночного разбора не знаем — второй строки нет: выдумывать нечем
    expect(has(c, 'Ночной разбор')).toBe(false);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-empty-settings')).toBeNull();
  });

  it('ночной разбор включён: «первые страницы появятся после ночи», ссылки на настройки нет', () => {
    settingsMock.value = settings(true);
    const c = renderEmpty(() => {});
    expect(has(c, 'Ночной разбор включён — первые страницы появятся после ночи')).toBe(true);
    expect(has(c, 'Ночной разбор выключен')).toBe(false);
    // переход к настройкам относится к выключенному разбору: при включённом он не нужен
    expect(findNodeByAttr(c, 'data-testid', 'wiki-empty-settings')).toBeNull();
  });

  it('ночной разбор выключен: строка про настройки и кнопка «Открыть настройки» зовёт колбэк', () => {
    let opened = 0;
    settingsMock.value = settings(false);
    const c = renderEmpty(() => { opened += 1; });
    expect(has(c, 'Ночной разбор выключен — его можно включить в настройках вики')).toBe(true);
    expect(has(c, 'Ночной разбор включён')).toBe(false);
    clickTestId(c, 'wiki-empty-settings');
    expect(opened, 'кнопка открывает «Настройки» вызывающего').toBe(1);
  });

  it('без колбэка вызывающего текст про настройки остаётся без кнопки', () => {
    settingsMock.value = settings(false);
    const c = renderEmpty();
    expect(has(c, 'Ночной разбор выключен')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-empty-settings')).toBeNull();
  });

  it('кнопки «Завести вики» нет ни в одном состоянии', () => {
    for (const v of [null, settings(true), settings(false)]) {
      settingsMock.value = v;
      installFakeDom();
      const { root, container } = mount(createElement(WikiEmpty, { onOpenSettings: () => {} }));
      roots.push(root);
      expect(has(container, 'Завести вики'), `состояние ${JSON.stringify(v)}`).toBe(false);
      expect(findNodeByAttr(container, 'data-testid', 'wiki-init-btn')).toBeNull();
      act(() => root.unmount());
      roots.splice(0);
      uninstallFakeDom();
    }
  });
});
