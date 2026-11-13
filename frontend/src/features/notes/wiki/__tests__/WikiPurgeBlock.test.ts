// Рендер-тест полосы «Начать вики заново» (J-F4; М2 — полоса, А2/П4): разрушающая чистка —
// только через предпросмотр и подтверждение человека; блок — закреплённая полоса у нижнего
// края тела панели.
// — клик кнопки: ровно один previewWikiPurge, runWikiPurge 0; модалка начинается длинным
//   пояснением («Удалятся все страницы, написанные ИИ: …»), дальше — числа по разделам и
//   классам («Будет удалено 7 страниц: …») и строкой-гарантией;
// — «Отмена»: run 0 раз, модалка закрыта;
// — «Удалить 7 страниц»: run ровно 1 раз; итог — число удалённых и путь архива;
//   warning бэка показан рядом с числом, warning null — предупреждения нет;
// — N=0: «Удалять нечего», кнопка подтверждения выключена;
// — отказ предпросмотра: Notice danger в полосе, модалки нет; отказ чистки: плашка в модалке,
//   она остаётся в состоянии предпросмотра;
// — итог принадлежит полосе, не модалке (QA J-F4): закрыть окно во время чистки — запрос
//   доходит, «Удалено N… Архив: …» выходит строкой над рядом; пока запрос не ответил —
//   подтверждение выключено и над рядом «Чистка выполняется…»; «Скрыть» (×) снимает итог;
// — полоса закреплена у нижнего края (М2/П4): в источнике позиции sticky/bottom;
// — узкая раскладка полосы решается чистой функцией purgeStripCompact по ширине контейнера.
// Стор мокан на уровне операций (тот же приём, что WikiEmpty.test): настоящая чистка в
// тестах не запускается — ни одного запроса на провод.
//
// Мутации этого прогона (вывод — в отчёт задачи):
// (1) в onClick кнопки вызывать runWikiPurge сразу (без предпросмотра) → «run 0 раз»
//     краснеет;
// (2) не ставить confirmDisabled при N=0 → «N=0» краснеет (кнопка активна);
// (3) не показывать warning в итоге → «warning» краснеет;
// (4) показывать строку архива и при archivePath null → «архива нет» краснеет;
// (5) держать итог только в модалке (не писать lastResult) → «закрыть окно во время
//     чистки» и «Скрыть» краснеют (в полосе пусто);
// (6) не строить строку статуса при runBusy → «Чистка выполняется…» краснеет в обоих
//     новых тестах;
// (7) в purgeStripCompact поднять порог 300→301 → «300→false» краснеет;
// (8) не снимать итог по «Скрыть» (setLastResult(null) → noop) → «Скрыть снимает» краснеет.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiPurgeBlock, purgeStripCompact } from '../WikiPurgeBlock';

const { preview, run } = vi.hoisted(() => ({ preview: vi.fn(), run: vi.fn() }));
vi.mock('../../../../lib/wiki', async (orig) => ({
  ...await orig<typeof import('../../../../lib/wiki')>(),
  previewWikiPurge: preview,
  runWikiPurge: run,
}));

import { WikiError } from '../../../../lib/wiki';

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

// Составы — формы ответа POST /api/wiki/purge (WikiController.cs:690-697): предпросмотр —
// countsBySection + paths, deleted 0; запуск — deleted + archivePath, paths пуст.
const PREVIEW = (): Record<string, unknown> => ({
  dryRun: true,
  countsBySection: {
    'Решения': { ai: 4, together: 2 },
    'Хроники': { chronicle: 1 },
  },
  pages: [],
  deleted: 0,
  archivePath: null,
  staledPackages: 0,
  warning: null,
});

const RESULT = (over: Record<string, unknown> = {}): Record<string, unknown> => ({
  dryRun: false,
  countsBySection: {},
  pages: [],
  deleted: 7,
  archivePath: 'keeper/purge-2026-11-12.zip',
  staledPackages: 3,
  warning: null,
  ...over,
});

const roots: Root[] = [];

function render(): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiPurgeBlock));
  roots.push(root);
  return container;
}

async function settle(times = 20) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(node: AnyNode): string[] {
  return textNodes(node).map(t => t.trim()).filter(Boolean);
}
// Пробелы нормализуем: DOM может дать строку несколькими узлами («Архив: » + путь в mono)
function has(node: AnyNode, needle: string): boolean {
  return texts(node).join(' ').replace(/\s+/g, ' ').includes(needle.replace(/\s+/g, ' '));
}

function click(node: AnyNode, what: string): void {
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick(undefined); });
}

const openBtn = (c: Cont) => findNodeByAttr(c, 'data-testid', 'wiki-purge-open')!;

// Диалог — портал в document.body (тот же приём, что «Ночной разбор»): его узлы от body
function docBody(): AnyNode {
  return (globalThis as { document: { body: AnyNode } }).document.body;
}
function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}
function buttonByExactText(root: AnyNode, text: string): AnyNode | null {
  return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === text) ?? null;
}
const isDisabled = (n: AnyNode): boolean => (reactProps(n) as { disabled?: boolean }).disabled === true;

beforeEach(() => {
  preview.mockReset();
  run.mockReset();
  preview.mockResolvedValue(PREVIEW());
});

afterEach(() => {
  for (const r of roots.splice(0)) void r.unmount();
  uninstallFakeDom();
});

describe('блок «Начать вики заново» (J-F4)', () => {
  it('клик кнопки: ровно один предпросмотр, чистки нет; модалка с числами по разделам и классам', async () => {
    const c = render();
    await settle();
    expect(preview.mock.calls.length).toBe(0);
    expect(isDisabled(openBtn(c))).toBe(false);

    click(openBtn(c), 'Начать вики заново');
    await settle();
    // мутация (1): вызов run сразу по кнопке здесь краснеет
    expect(preview.mock.calls.length).toBe(1);
    expect(run.mock.calls.length).toBe(0);

    expect(has(docBody(), 'Начать вики заново?')).toBe(true);
    // А2: длинное пояснение переехало в модалку первой строкой (его в полосе больше нет)
    expect(has(docBody(), 'Удалятся все страницы, написанные ИИ: хроники, карточки источников и страницы, написанные вместе с вами.')).toBe(true);
    expect(has(docBody(), 'Будет удалено 7 страниц:')).toBe(true);
    expect(has(docBody(), 'Решения — 6')).toBe(true);
    expect(has(docBody(), 'написанные ИИ — 4, написанные вместе с вами — 2')).toBe(true);
    expect(has(docBody(), 'Хроники — 1')).toBe(true);
    expect(has(docBody(), 'хроники — 1')).toBe(true);
    expect(has(docBody(), 'Страницы, написанные вами, и приёмыши остаются. Всё удалённое сохранится в zip-архиве.')).toBe(true);
    const confirm = buttonByExactText(docBody(), 'Удалить 7 страниц');
    expect(confirm).not.toBeNull();
    expect(isDisabled(confirm!)).toBe(false);
  });

  it('«Отмена»: чистки нет, модалка закрыта', async () => {
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Отмена')!, 'отмена');
    expect(run.mock.calls.length).toBe(0);
    expect(has(docBody(), 'Начать вики заново?')).toBe(false);
  });

  it('«Удалить 7 страниц»: ровно одна чистка; итог — число удалённых и путь архива, без warning', async () => {
    run.mockResolvedValue(RESULT());
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle();
    expect(run.mock.calls.length).toBe(1);
    expect(has(docBody(), 'Удалено 7 страниц.')).toBe(true);
    expect(has(docBody(), 'Архив: keeper/purge-2026-11-12.zip')).toBe(true);
    expect(has(docBody(), 'перезапуск разбора')).toBe(false);
    // «Закрыть» — итог остаётся записанным в истории, окно закрывается без нового вызова
    click(buttonByExactText(docBody(), 'Закрыть')!, 'закрыть');
    expect(has(docBody(), 'Удалено 7 страниц.')).toBe(false);
    expect(run.mock.calls.length).toBe(1);
  });

  it('warning в итоге: предупреждение рядом с числом удалённых; без warning — предупреждения нет', async () => {
    run.mockResolvedValue(RESULT({ warning: 'чистка выполнена, но перезапуск разбора не доведён — повтори позже' }));
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle();
    // мутация (3): не показывать warning — эта строка краснеет
    expect(has(docBody(), 'Удалено 7 страниц.')).toBe(true);
    expect(has(docBody(), 'чистка выполнена, но перезапуск разбора не доведён — повтори позже')).toBe(true);
    // закрываем первую модалку: иначе вторая проверка читала бы её текст, висящий в body
    click(buttonByExactText(docBody(), 'Закрыть')!, 'закрыть');

    run.mockResolvedValue(RESULT());
    const c2 = render();
    await settle();
    click(openBtn(c2), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle();
    expect(has(docBody(), 'перезапуск разбора')).toBe(false);
  });

  it('пустой состав: «Удалять нечего», подтверждение выключено, чистки нет', async () => {
    preview.mockResolvedValue({ ...PREVIEW(), countsBySection: {} });
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    expect(has(docBody(), 'Удалять нечего.')).toBe(true);
    const confirm = buttonByExactText(docBody(), 'Удалить 0 страниц');
    expect(confirm).not.toBeNull();
    // мутация (2): снять confirmDisabled при N=0 — здесь краснеет
    expect(isDisabled(confirm!)).toBe(true);
    // выключенную кнопку человек нажать не может: ищем не клик, а отсутствие самого вызова
    expect(run.mock.calls.length).toBe(0);
  });

  it('архива нет (archivePath null): строки архива в итоге нет', async () => {
    run.mockResolvedValue(RESULT({ archivePath: null }));
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle();
    expect(has(docBody(), 'Удалено 7 страниц.')).toBe(true);
    // мутация (4): рисовать «Архив:» и при null — краснеет
    expect(has(docBody(), 'Архив:')).toBe(false);
  });

  it('отказ предпросмотра: плашка у кнопки с причиной бэка, модалки нет, чистки нет', async () => {
    preview.mockRejectedValue(new WikiError(503, 'wiki_purge_io', 'не удалось прочитать страницы — попробуйте ещё раз'));
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    expect(has(c, 'не удалось прочитать страницы — попробуйте ещё раз')).toBe(true);
    expect(has(docBody(), 'Начать вики заново?')).toBe(false);
    expect(run.mock.calls.length).toBe(0);
    // кнопка остаётся рабочей: повтор — то же действие
    expect(isDisabled(openBtn(c))).toBe(false);
  });

  it('отказ чистки: плашка в модалке, она остаётся в состоянии предпросмотра', async () => {
    run.mockRejectedValue(new WikiError(409, 'wiki_purge_manifest', 'манифест раздела не читается — почините его и повторите'));
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle();
    expect(run.mock.calls.length).toBe(1);
    expect(has(docBody(), 'манифест раздела не читается — почините его и повторите')).toBe(true);
    // не итог: предпросмотр на месте, повторить можно тем же подтверждением
    expect(has(docBody(), 'Будет удалено 7 страниц:')).toBe(true);
    expect(has(docBody(), 'Удалено 7 страниц.')).toBe(false);
    expect(isDisabled(buttonByExactText(docBody(), 'Удалить 7 страниц')!)).toBe(false);
  });

  it('пока чистка не ответила: подтверждение выключено, под кнопкой «Чистка выполняется…»', async () => {
    // Ответ приходит по нашему сигналу: тест ловит состояние ДО settle-а ответа
    let resolveRun: (v: Record<string, unknown>) => void = () => {};
    run.mockImplementation(() => new Promise<Record<string, unknown>>((res) => { resolveRun = res; }));
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle(1);
    const confirm = buttonByExactText(docBody(), 'Удалить 7 страниц');
    expect(confirm).not.toBeNull();
    // loading у Button гасит подтверждение: повторить разрушающее действие нельзя
    expect(isDisabled(confirm!)).toBe(true);
    // мутация (6): не строить строку статуса при runBusy — здесь краснеет
    expect(has(c, 'Чистка выполняется…')).toBe(true);
    // итога ещё нет: ответ не пришёл
    expect(has(c, 'Удалено 7 страниц.')).toBe(false);
    await act(async () => { resolveRun(RESULT()); });
    await settle();
    expect(has(c, 'Удалено 7 страниц.')).toBe(true);
  });

  it('закрыть окно во время чистки: запрос доходит, итог — строкой под кнопкой (QA J-F4)', async () => {
    let resolveRun: (v: Record<string, unknown>) => void = () => {};
    run.mockImplementation(() => new Promise<Record<string, unknown>>((res) => { resolveRun = res; }));
    const c = render();
    await settle();
    click(openBtn(c), 'Начать вики заново');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle(1);
    expect(has(c, 'Чистка выполняется…')).toBe(true);
    // сценарий QA: крестик в шапке (Esc в fakeDom нет; крестик — тот же onClose)
    click(findNodeByAttr(docBody(), 'aria-label', 'Закрыть')!, 'крестик');
    expect(has(docBody(), 'Начать вики заново?')).toBe(false);
    // закрытие окна не отменило запрос: он доходит после закрытия
    await act(async () => { resolveRun(RESULT()); });
    await settle();
    expect(run.mock.calls.length).toBe(1);
    // мутация (5): держать итог только в модалке — эти две строки краснеют
    expect(has(c, 'Удалено 7 страниц.')).toBe(true);
    expect(has(c, 'Архив: keeper/purge-2026-11-12.zip')).toBe(true);
  });

  it('«Скрыть» (×) снимает итог из полосы: строки «Удалено…/Архив:» больше нет', async () => {
    run.mockResolvedValue(RESULT());
    const c = render();
    await settle();
    click(openBtn(c), 'Начать заново…');
    await settle();
    click(buttonByExactText(docBody(), 'Удалить 7 страниц')!, 'удалить');
    await settle();
    // закрыть окно «Закрыть» — итог остаётся записанным в полосе
    click(buttonByExactText(docBody(), 'Закрыть')!, 'закрыть');
    expect(has(c, 'Удалено 7 страниц.')).toBe(true);
    expect(has(c, 'Архив: keeper/purge-2026-11-12.zip')).toBe(true);
    // «Скрыть» (×) — полоса возвращается к одному ряду
    const hide = findNodeByAttr(c, 'aria-label', 'Скрыть');
    expect(hide).not.toBeNull();
    // мутация (8): не снимать lastResult — эти две строки краснеют
    click(hide!, 'скрыть');
    await settle();
    expect(has(c, 'Удалено 7 страниц.')).toBe(false);
    expect(has(c, 'Архив:')).toBe(false);
  });

  it('узкая раскладка полосы — по ширине контейнера (А2/П1): порог 300, без замера — широкая', () => {
    // мутация (7): поднять порог 300→301 — «(300)→false» краснеет
    expect(purgeStripCompact(299)).toBe(true);
    expect(purgeStripCompact(300)).toBe(false);
    expect(purgeStripCompact(301)).toBe(false);
    expect(purgeStripCompact(1000)).toBe(false);
    // до первого замера (null) — широкая раскладка: она же и на широком экране
    expect(purgeStripCompact(null)).toBe(false);
  });

  it('полоса закреплена у нижнего края тела панели (М2/П4): sticky/bottom в источнике', () => {
    const src = readFileSync(fileURLToPath(new URL('../WikiPurgeBlock.tsx', import.meta.url)), 'utf8');
    expect(src).toContain("position: 'sticky'");
    expect(src).toContain('bottom: 0');
  });
});
