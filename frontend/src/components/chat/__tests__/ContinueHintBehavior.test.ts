// Поведенческий тест подсказки «Продолжить в новом чате» (вариант C, полоса RowNotice):
// клик по ✕ «Не сейчас» вызывает onSnooze, клик по «Продолжить» вызывает onOpen.
// Мутация «убрать вызов onSnooze» (кнопка ✕ не вызывает snooze.onClick) обязана покрасить тест —
// в отличие от статической разметки, которая лишь проверяет наличие атрибутов.
// Окружение node (без jsdom): монтируем на минимальном фейк-DOM (см. fakeDom.ts).
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { ContinueHint } from '../ContinueChat';
import { findNodeByAttr, installFakeDom, mount, reactProps, uninstallFakeDom } from './fakeDom';

beforeAll(() => installFakeDom());
afterAll(() => uninstallFakeDom());

describe('ContinueHint: поведение (клики вызывают колбэки)', () => {
  it('клик по ✕ «Не сейчас» вызывает onSnooze', () => {
    const onSnooze = vi.fn();
    const { root, container } = mount(createElement(ContinueHint, {
      tokens: 312_000, onOpen: () => {}, onSnooze,
    }));
    const btn = findNodeByAttr(container, 'data-testid', 'continue-hint-snooze');
    expect(btn, 'кнопка ✕ «Не сейчас» не найдена').not.toBeNull();
    const props = reactProps(btn!);
    expect(props?.onClick, 'у кнопки ✕ нет onClick').toBeDefined();
    (props!.onClick as () => void)();
    expect(onSnooze, 'onSnooze не вызван кликом по ✕').toHaveBeenCalledTimes(1);
    act(() => { root.unmount(); });
  });

  it('клик по «Продолжить» вызывает onOpen', () => {
    const onOpen = vi.fn();
    const { root, container } = mount(createElement(ContinueHint, {
      tokens: 312_000, onOpen, onSnooze: () => {},
    }));
    const btn = findNodeByAttr(container, 'data-testid', 'continue-hint-open');
    expect(btn, 'кнопка «Продолжить» не найдена').not.toBeNull();
    const props = reactProps(btn!);
    expect(props?.onClick, 'у кнопки «Продолжить» нет onClick').toBeDefined();
    (props!.onClick as () => void)();
    expect(onOpen, 'onOpen не вызван кликом по «Продолжить»').toHaveBeenCalledTimes(1);
    act(() => { root.unmount(); });
  });
});
