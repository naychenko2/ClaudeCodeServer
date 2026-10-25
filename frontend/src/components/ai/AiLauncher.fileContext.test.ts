// Палитра AI: пункт «Сохранить как Markdown» следит за признаком fromRoot открытого файла
// (lib/ai/fileContext) без переоткрытия палитры. Признак приходит из эффекта FileViewer уже
// после загрузки содержимого, поэтому список обязан пересчитываться по подписке.
// Полный AiLauncher в node-окружении не монтируется (таймеры, layout, порталы), поэтому:
// поведение — на пробнике с тем же шаблоном (useFileContext → useMemo → rankedActions), а
// проводка в самом AiLauncher — сторожем по исходнику.
// Мутации:
//   • убрать `_listeners.forEach` из setFileContext/clearFileContext → тест (б) краснеет;
//   • убрать `fileCtx` из зависимостей useMemo в AiLauncher → сторож (г) краснеет.

import { act, createElement, useMemo } from 'react';
import { readFileSync } from 'node:fs';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

vi.hoisted(() => { (globalThis as any).self = globalThis; });

import { rankedActions, type AiActionCtx } from '../../lib/ai/actions';
import { setFileContext, clearFileContext, getFileContext, subscribeFileContext, useFileContext } from '../../lib/ai/fileContext';
import { installFakeDom, uninstallFakeDom, mount } from '../chat/__tests__/fakeDom';

const TITLE = 'Сохранить как Markdown';
const FILE = 'docs/spec.pdf';

describe('fileContext: подписка', () => {
  afterEach(() => clearFileContext(FILE));

  it('set и clear уведомляют подписчиков, повтор того же значения — нет, отписка работает', () => {
    const fn = vi.fn();
    const off = subscribeFileContext(fn);
    setFileContext(FILE, true);
    expect(fn).toHaveBeenCalledTimes(1);
    setFileContext(FILE, true);
    expect(fn, 'то же значение — без шума').toHaveBeenCalledTimes(1);
    setFileContext(FILE, false);
    expect(fn).toHaveBeenCalledTimes(2);
    clearFileContext('other.pdf');
    expect(fn, 'чужой путь не сбрасывает').toHaveBeenCalledTimes(2);
    clearFileContext(FILE);
    expect(fn).toHaveBeenCalledTimes(3);
    expect(getFileContext()).toBeUndefined();
    off();
    setFileContext(FILE, true);
    expect(fn, 'после отписки тишина').toHaveBeenCalledTimes(3);
  });
});

describe('список палитры пересчитывается по смене признака', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { clearFileContext(FILE); uninstallFakeDom(); });

  const nav = { screen: 'project', project: { id: 'p1', name: 'P', rootPath: '/p' }, file: FILE } as never;
  let titles: string[] = [];

  // Пробник повторяет шаблон AiLauncher: подписка на признак + useMemo с ним в зависимостях
  function Probe() {
    const fileCtx = useFileContext();
    titles = useMemo(() => {
      const ctx = { nav, online: true, flag: () => true, subsystem: () => true, caps: { semantic: false },
        chat: { active: false, hasMessages: false }, file: getFileContext() } as unknown as AiActionCtx;
      return rankedActions(ctx, '').map(r => r.action.title);
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [fileCtx]);
    return null;
  }

  it('(б) корень → не из корня → корень: пункт исчезает и возвращается без переоткрытия', async () => {
    setFileContext(FILE, true);
    mount(createElement(Probe));
    expect(titles, 'файл из корня — пункт есть').toContain(TITLE);
    await act(async () => { setFileContext(FILE, false); });
    expect(titles, 'не из корня — пункт убран').not.toContain(TITLE);
    await act(async () => { setFileContext(FILE, true); });
    expect(titles, 'снова из корня — вернулся').toContain(TITLE);
  });
});

describe('(г) AiLauncher читает признак реактивно', () => {
  const src = readFileSync(new URL('./AiLauncher.tsx', import.meta.url), 'utf8');
  it('useFileContext вызван и входит в зависимости useMemo списка', () => {
    expect(src).toMatch(/const fileCtx = useFileContext\(\)/);
    expect(src).toMatch(/\[open, q, online, semanticCaps, recs, gitRepo, fileCtx\]\)/);
  });
});
