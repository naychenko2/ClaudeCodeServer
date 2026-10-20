// FeatureCenter: страница фичи — СОСЕД центра. Глеб M2 = Майя M1: на ДЕСКТОПЕ страница
// обёрнута в Island (R.xxl=14, рамка, тень, зазор centerGap — ровно как centerIsland у файла),
// на МОБИЛЕ — без острова (как и файл). Чат под страницей остаётся в DOM (display:none).
//
// Окружение node (без jsdom): fakeDom (mount) + управляемый стор useFeaturePage.
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureCenter } from './FeatureCenter';
import { __test as pageTest } from '../../lib/featurePage';
import { setAllFlags } from '../../lib/featureFlags';
import { api } from '../../lib/api';
import { mount, installFakeDom, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { FeaturePage as FeaturePageData } from '../../types/featureIndex';

// Минимальная валидная страница (run + events + eventsTotal) — чтобы FeaturePage прошёл loading → ok.
function pageData(): FeaturePageData {
  return {
    run: {
      featureId: 'f1',
      epic: { id: 'f1', title: 'Моя фича' },
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    },
    events: [], eventsTotal: 0,
  };
}

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async () => null },
    tasks: {
      featuresIndex: async () => ({ features: [], chats: {} }),
      getFeature: async () => pageData(),
      mergeFeature: async () => null,
    },
  },
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: () => {} }));

// Узел fakeDom: ищем элемент, чей style содержит borderRadius (Island = R.xxl=14px).
type FNode = { nodeType: number; childNodes: FNode[]; getAttribute?: (a: string) => string | null; style?: Record<string, unknown> };
function hasIsland(n: FNode): boolean {
  if (n.nodeType === 1) {
    const br = (n.style as Record<string, unknown>)?.borderRadius;
    if (br != null && String(br).includes('14')) return true;
  }
  for (const c of n.childNodes) if (hasIsland(c)) return true;
  return false;
}

function renderCenter(isMobile: boolean) {
  installFakeDom();
  // FeaturePage → useEscapeClose зовёт window.addEventListener: installFakeDom даёт
  // window без него — ставим управляемые add/remove/dispatch (тот же приём, что в mountPage).
  const win = globalThis as any;
  if (!win.window.addEventListener) {
    const listeners: Record<string, Set<Function>> = {};
    win.window.addEventListener = (t: string, f: Function) => { (listeners[t] ??= new Set()).add(f); };
    win.window.removeEventListener = (t: string, f: Function) => { listeners[t]?.delete(f); };
    win.window.dispatchEvent = (e: { type: string }) => { listeners[e.type]?.forEach(f => f(e)); return true; };
  }
  const { container } = mount(createElement(FeatureCenter, {
    open: true, featureId: 'f1', projectId: 'p1', isMobile,
    onBack: () => {},
    children: createElement('div', null, 'центр'),
  }));
  return container as unknown as FNode;
}

describe('FeatureCenter: остров на десктопе (Глеб M2 = Майя M1)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    pageTest.reset();
    setAllFlags({ 'features-overview': true });
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
    pageTest.reset();
  });

  it('десктоп: страница в Island (R.xxl=14, рамка, тень)', async () => {
    const c = renderCenter(false);
    expect(hasIsland(c), 'остров (Island) на десктопе').toBe(true);
  });

  it('мобила: без острова (как и файл)', async () => {
    const c = renderCenter(true);
    expect(hasIsland(c), 'остров (Island) на мобиле').toBe(false);
  });
});
