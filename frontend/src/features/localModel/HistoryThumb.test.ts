// Миниатюра истории: ошибка загрузки <img> → плитка вместо img; смена src сбрасывает ошибку.
// Мутации, которые обязаны покрасить: убрать onError; сравнивать failed !== src вместо ===
// (ошибка не сбросится при смене src).

import { act, createElement } from 'react';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import { HistoryThumb } from './HistoryThumb';

beforeAll(() => { installFakeDom(); });
afterAll(() => { uninstallFakeDom(); });

type Mounted = ReturnType<typeof mount>;
const render = (m: Mounted, src: string) => act(() => {
  m.root.render(createElement(HistoryThumb, { src, fallback: createElement('i', { 'data-tile': '1' }) }));
});
const imgs = (m: Mounted) => m.container.querySelectorAll('img');
const tiles = (m: Mounted) => m.container.querySelectorAll('i');

describe('HistoryThumb', () => {
  it('рисует img, пока ошибки нет', () => {
    const m = mount(createElement(HistoryThumb, { src: '/a', fallback: createElement('i') }));
    expect(imgs(m)).toHaveLength(1);
    expect(tiles(m)).toHaveLength(0);
  });

  it('ошибка загрузки → плитка вместо img', () => {
    const m = mount(createElement(HistoryThumb, { src: '/a', fallback: createElement('i') }));
    const onError = reactProps(imgs(m)[0])?.onError as () => void;
    act(() => { onError(); });
    expect(imgs(m)).toHaveLength(0);
    expect(tiles(m)).toHaveLength(1);
  });

  it('смена src сбрасывает ошибку', () => {
    const m = mount(createElement(HistoryThumb, { src: '/a', fallback: createElement('i') }));
    act(() => { (reactProps(imgs(m)[0])?.onError as () => void)(); });
    expect(imgs(m)).toHaveLength(0);
    render(m, '/b');
    expect(imgs(m)).toHaveLength(1);
    expect(tiles(m)).toHaveLength(0);
  });
});
