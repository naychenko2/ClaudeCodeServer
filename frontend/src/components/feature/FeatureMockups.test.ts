// FS-F1f: карточка макета открывает файл с ownerTaskId автора.
//
// «Открыть макет» (область-заглушка и кнопка) уводит файл в FileViewer через onOpenFile
// вторым аргументом { taskId: ownerTaskId, branch: файла } — автор и ветка из ответа
// (поля завели FS-F1c/FS-B3b), фронт их не вычисляет. Без ownerTaskId — вызов одним
// аргументом, как раньше. Клик или Enter — ровно один вызов onOpenFile.
//
// Окружение node (без jsdom): fakeDom (mount) + reactProps для клика/Enter.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { MockupCard } from './FeatureMockups';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureMockup } from '../../types/featurePlans';

type FNode = ReturnType<typeof findNodeByAttr> extends infer T ? (T extends null ? never : NonNullable<T>) : never;

function mockup(over: Partial<FeatureMockup> = {}): FeatureMockup {
  return {
    path: 'docs/mockups/x.html', title: 'X', status: 'approved',
    files: [{ path: 'docs/mockups/x.html', exists: true, branch: 'b' }],
    source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null,
    ...over,
  };
}

// Клик по data-testid: вызываем onClick из reactProps, в act(). Заглушка события —
// потому что Button прокидывает его в свой onClick дальше (stopPropagation у обработчика).
function click(container: FNode, testId: string): void {
  const node = findNodeByAttr(container, 'data-testid', testId);
  expect(node, `node ${testId}`).not.toBeNull();
  const props = reactProps(node!);
  expect(props?.onClick, `onClick on ${testId}`).toBeTruthy();
  act(() => { (props!.onClick as (e: unknown) => void)({ stopPropagation: () => {}, preventDefault: () => {} }); });
}

// Enter по data-testid: вызываем onKeyDown из reactProps (тот же путь, что реальная клавиша).
function pressEnter(container: FNode, testId: string): void {
  const node = findNodeByAttr(container, 'data-testid', testId);
  expect(node, `node ${testId}`).not.toBeNull();
  const props = reactProps(node!);
  expect(props?.onKeyDown, `onKeyDown on ${testId}`).toBeTruthy();
  act(() => { (props!.onKeyDown as (e: unknown) => void)({ key: 'Enter', preventDefault: () => {} }); });
}

function mountCard(m: FeatureMockup, onOpenFile: ReturnType<typeof vi.fn>): FNode {
  const { container } = mount(createElement(MockupCard, { mockup: m, plans: [], onOpenFile, isMobile: false }));
  return container as FNode;
}

describe('MockupCard: открытие файла макета (FS-F1f)', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); vi.restoreAllMocks(); });

  // === 1. Клик по области «Открыть макет» → onOpenFile(main, { taskId, branch }) ===
  // Мутация «не передавать opts в onClick области» краснеет здесь.
  it('клик по области открытия → onOpenFile(main, { taskId: ownerTaskId, branch })', () => {
    const onOpenFile = vi.fn();
    const container = mountCard(mockup({ ownerTaskId: 't1' }), onOpenFile);
    click(container, 'mockup-open-area');
    expect(onOpenFile).toHaveBeenCalledTimes(1);
    expect(onOpenFile).toHaveBeenCalledWith('docs/mockups/x.html', { taskId: 't1', branch: 'b' });
  });

  // === 2. Enter по области открытия → те же opts (отдельный тест: мутация
  // «не передавать opts в обработчике клавиши» краснеет ровно здесь) ===
  it('Enter по области открытия → onOpenFile(main, { taskId: ownerTaskId, branch })', () => {
    const onOpenFile = vi.fn();
    const container = mountCard(mockup({ ownerTaskId: 't1' }), onOpenFile);
    pressEnter(container, 'mockup-open-area');
    expect(onOpenFile).toHaveBeenCalledTimes(1);
    expect(onOpenFile).toHaveBeenCalledWith('docs/mockups/x.html', { taskId: 't1', branch: 'b' });
  });

  // === 3. Кнопка «Открыть макет» → те же opts, ровно один вызов ===
  // Мутация «не передавать opts в onClick кнопки» краснеет здесь.
  it('клик по кнопке «Открыть макет» → onOpenFile(main, { taskId: ownerTaskId, branch })', () => {
    const onOpenFile = vi.fn();
    const container = mountCard(mockup({ ownerTaskId: 't1' }), onOpenFile);
    click(container, 'mockup-open-btn');
    expect(onOpenFile).toHaveBeenCalledTimes(1);
    expect(onOpenFile).toHaveBeenCalledWith('docs/mockups/x.html', { taskId: 't1', branch: 'b' });
  });

  // === 4. Без ownerTaskId → onOpenFile(main) без второго аргумента (как раньше) ===
  // WithArguments сравнивает весь список: второй аргумент здесь дал бы красный.
  it('без ownerTaskId → onOpenFile(main) без второго аргумента (клик и Enter)', () => {
    const onOpenFile = vi.fn();
    const container = mountCard(mockup(), onOpenFile);
    click(container, 'mockup-open-area');
    expect(onOpenFile).toHaveBeenCalledTimes(1);
    expect(onOpenFile).toHaveBeenCalledWith('docs/mockups/x.html');
    pressEnter(container, 'mockup-open-area');
    expect(onOpenFile).toHaveBeenCalledTimes(2);
    expect(onOpenFile.mock.calls[1]).toEqual(['docs/mockups/x.html']);
  });

  // === 5. Нет существующего html (exists=false) → открытия нет (прежнее поведение) ===
  it('exists=false → onOpenFile не вызывается', () => {
    const onOpenFile = vi.fn();
    const container = mountCard(mockup({ files: [{ path: 'docs/mockups/x.html', exists: false }] }), onOpenFile);
    click(container, 'mockup-open-area');
    expect(onOpenFile).not.toHaveBeenCalled();
  });
});
