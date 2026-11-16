// Блок «Артефакты» (F15): файлы с версиями, дифф с режимами, гонка ответов по ключу path+from+to.
// D1: привязанные задачей файлы и итог видны и без коммитов; F-2.11: привязанные файлы видны и при
// непустых files, пустое состояние различает отсутствие дерева.
//
// Мутации (в отчёте): (1) читать состояние диффа не по ключу, а по последнему ответу → тест
// «запоздавший ответ» красный; (2) убрать `if (signal.aborted) return` при отказе → тест «отказ под
// файлом» остаётся зелёным, поэтому отдельный тест «размонтирование» ловит применение после отмены;
// (3) не рисовать «ещё N файлов» → тест «сворачивает» красный; (4) не рисовать «Файлы задачи» при
// пустых files → тест «без файлов коммитов» красный; (5) оставить пустое состояние при результате →
// тест «без пустого состояния» красный; (6) не рисовать «Файлы задачи» при непустых files → тест
// «при файлах коммитов» красный; (7) не различать commitsAvailable в пустом состоянии → тест «без
// дерева задачи» красный; (8) вернуть сырой текст итога вместо MarkdownContent → тесты
// «markdown итога» красные: нет h2/li/strong/code, в тексте остаются сырые «##» и «**».

import { act, createElement } from 'react';
import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest';
import { FeatureStageArtifacts, diffModes } from './FeatureStageArtifacts';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { StageArtifacts, StageCommit, StageFile, StageFileDiff } from '../../types/featureStage';

const { diffMock } = vi.hoisted(() => ({ diffMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, tasks: { ...orig.api.tasks, stageFileDiff: diffMock } } };
});

type FNode = Parameters<typeof findNodeByAttr>[0];
const flush = () => new Promise(r => setTimeout(r, 0));
const roots: ReturnType<typeof mount>['root'][] = [];

const ver = (n: number, afterReturn = false) => ({ n, commitSha: `s${n}`, subject: `к${n}`, at: null, afterReturn });
const file = (path: string, n: number, lastAfterReturn = false): StageFile => ({
  path, versions: Array.from({ length: n }, (_, i) => ver(i + 1, lastAfterReturn && i === n - 1)),
});
const arts = (files: StageFile[], over: Partial<StageArtifacts> = {}): StageArtifacts => ({
  commits: [], commitsAvailable: true, linkedFiles: [], resultMarkdown: null, files, totals: { files: files.length, commits: 0 }, ...over,
});
const fdiff = (path: string, body: string): StageFileDiff => ({ path, from: 'all', to: 'all', diff: `@@ -1 +1 @@\n-x\n+${body}`, truncated: false, maxChars: 1000 });

function render(a: StageArtifacts) {
  installFakeDom();
  const r = mount(createElement(FeatureStageArtifacts, { taskId: 't1', artifacts: a, isMobile: false }));
  roots.push(r.root);
  return r;
}
function click(c: FNode, testid: string) {
  const n = findNodeByAttr(c, 'data-testid', testid);
  expect(n, testid).not.toBeNull();
  act(() => { (reactProps(n!)!.onClick as (e: unknown) => void)({ stopPropagation() {}, preventDefault() {} }); });
}
const text = (c: FNode) => textNodes(c).join('|');
function findByLabel(c: FNode, label: string): FNode | null {
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && typeof reactProps(x)?.onClick === 'function' && textNodes(x).join('') === label) { hit = x; return; }
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c);
  return hit;
}
function deferred<T>() {
  let resolve!: (v: T) => void; let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

beforeEach(() => { diffMock.mockReset(); });
afterEach(() => { for (const r of roots) act(() => { r.unmount(); }); roots.length = 0; uninstallFakeDom(); });

describe('diffModes', () => {
  it('режимы по числу версий', () => {
    expect(diffModes(file('a', 1)).map(m => m.label)).toEqual(['всё']);
    expect(diffModes(file('a', 2)).map(m => [m.label, m.from, m.to])).toEqual([['v1', 'all', '1'], ['v1 → v2', '1', '2'], ['всё', 'all', 'all']]);
    expect(diffModes(file('a', 3)).map(m => [m.label, m.from, m.to])).toEqual([['v1', 'all', '1'], ['v2 → v3', '2', '3'], ['всё', 'all', 'all']]);
  });
});

describe('FeatureStageArtifacts · файлы', () => {
  it('пусто всё: иконка и подсказка', () => {
    const { container } = render(arts([]));
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-artifacts-empty')!)).toContain('Артефактов пока нет');
    expect(findNodeByAttr(container, 'data-testid', 'stage-linked-files')).toBeNull();
  });

  it('итог без файлов: пустого состояния нет, итог исполнителя виден', () => {
    const { container } = render(arts([], { resultMarkdown: 'сделано' }));
    expect(findNodeByAttr(container, 'data-testid', 'stage-artifacts-empty')).toBeNull();
    expect(text(container)).toContain('Итог исполнителя');
    expect(text(container)).toContain('сделано');
  });

  it('D1: без файлов коммитов привязанные файлы задачи и итог видны, пустого состояния нет', () => {
    const { container } = render(arts([], { linkedFiles: ['qa/cut.md', 'docs/plan.md'], resultMarkdown: 'готово' }));
    expect(findNodeByAttr(container, 'data-testid', 'stage-artifacts-empty')).toBeNull();
    const linked = findNodeByAttr(container, 'data-testid', 'stage-linked-files');
    expect(linked, 'stage-linked-files').not.toBeNull();
    expect(text(linked!)).toContain('qa/cut.md');
    expect(text(linked!)).toContain('docs/plan.md');
    expect(text(container)).toContain('Итог исполнителя');
  });

  it('F-2.11: при файлах коммитов привязанные файлы задачи показаны рядом с ними', async () => {
    diffMock.mockResolvedValue(fdiff('A.cs', 'AAA'));
    const { container } = render(arts([file('A.cs', 1)], { linkedFiles: ['docs/plan.md', 'qa/cut.md'] }));
    await act(async () => { await flush(); });
    // вид с файлами жив: файл и его дифф на месте
    expect(findNodeByAttr(container, 'data-testid', 'stage-file-A.cs'), 'список файлов').not.toBeNull();
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-file-diff')!)).toContain('AAA');
    // и привязанный, но не закоммиченный файл не потерян
    const linked = findNodeByAttr(container, 'data-testid', 'stage-linked-files');
    expect(linked, 'stage-linked-files').not.toBeNull();
    expect(text(linked!)).toContain('docs/plan.md');
    expect(text(linked!)).toContain('qa/cut.md');
  });

  it('F-2.11: без дерева задачи пустое состояние говорит про недоступные коммиты, а не «появятся»', () => {
    const { container } = render(arts([], { commitsAvailable: false }));
    const t = text(findNodeByAttr(container, 'data-testid', 'stage-artifacts-empty')!);
    expect(t).toContain('Коммиты недоступны: дерево задачи не найдено');
    expect(t).not.toContain('Появятся с первым коммитом');
  });

  it('без файлов, но есть коммиты без файлов: список коммитов, пустого состояния нет', () => {
    const c: StageCommit = { sha: 's1', shortSha: 's1abcde', subject: 'коммит без файлов', at: null, files: [], taskId: 't1', role: null, version: 0 };
    const { container } = render(arts([], { commits: [c] }));
    expect(findNodeByAttr(container, 'data-testid', 'stage-artifacts-empty')).toBeNull();
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-commits')!)).toContain('коммит без файлов');
  });

  it('файл с тремя версиями: чипы v3·v2·v1, переключатель, клик «v2 → v3» — один запрос from=2,to=3', async () => {
    diffMock.mockResolvedValue(fdiff('A.cs', 'AAA'));
    const { container } = render(arts([file('A.cs', 3)]));
    await act(async () => { await flush(); });
    expect(text(container)).toMatch(/v\|3\|v\|2\|v\|1/);
    for (const l of ['v1', 'v2 → v3', 'всё']) expect(findByLabel(container, l), l).not.toBeNull();
    diffMock.mockClear();
    act(() => { (reactProps(findByLabel(container, 'v2 → v3')!)!.onClick as () => void)(); });
    await act(async () => { await flush(); });
    expect(diffMock).toHaveBeenCalledTimes(1);
    expect(diffMock.mock.calls[0].slice(0, 4)).toEqual(['t1', 'A.cs', '2', '3']);
  });

  it('первый файл выбран, дифф «всё» грузится по умолчанию', async () => {
    diffMock.mockResolvedValue(fdiff('A.cs', 'AAA'));
    const { container } = render(arts([file('A.cs', 2), file('B.cs', 1)]));
    await act(async () => { await flush(); });
    expect(diffMock.mock.calls[0].slice(0, 4)).toEqual(['t1', 'A.cs', 'all', 'all']);
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-file-diff')!)).toContain('AAA');
  });

  it('подпись про возврат — только если последняя версия после возврата', async () => {
    diffMock.mockResolvedValue(fdiff('A.cs', 'AAA'));
    const r1 = render(arts([file('A.cs', 3, true)]));
    expect(text(findNodeByAttr(r1.container, 'data-testid', 'stage-file-caption')!)).toContain('v3 — после возврата Глеба');
    act(() => { r1.root.unmount(); }); roots.length = 0;
    const r2 = render(arts([file('A.cs', 3, false)]));
    expect(text(findNodeByAttr(r2.container, 'data-testid', 'stage-file-caption')!)).not.toContain('после возврата');
  });

  it('запоздавший ответ прежнего файла не попадает в дифф выбранного', async () => {
    const a = deferred<StageFileDiff>(); const b = deferred<StageFileDiff>();
    diffMock.mockImplementation((_t: string, path: string) => (path === 'A.cs' ? a.promise : b.promise));
    const { container } = render(arts([file('A.cs', 1), file('B.cs', 1)]));
    await act(async () => { await flush(); });
    click(container, 'stage-file-B.cs');
    await act(async () => { await flush(); });
    await act(async () => { a.resolve(fdiff('A.cs', 'AAA')); await flush(); });
    expect(findNodeByAttr(container, 'data-testid', 'stage-file-diff')).toBeNull();
    await act(async () => { b.resolve(fdiff('B.cs', 'BBB')); await flush(); });
    const t = text(findNodeByAttr(container, 'data-testid', 'stage-file-diff')!);
    expect(t).toContain('BBB');
    expect(t).not.toContain('AAA');
  });

  it('отказ загрузки — под файлом, экран жив', async () => {
    diffMock.mockImplementation(() => Promise.reject(new Error('Файл или версия не найдены')));
    const { container } = render(arts([file('A.cs', 1)], { resultMarkdown: 'итог' }));
    await act(async () => { await flush(); });
    expect(text(container)).toContain('Файл или версия не найдены');
    expect(findNodeByAttr(container, 'data-testid', 'stage-file-diff')).toBeNull();
    expect(text(container)).toContain('итог');
  });

  it('отказ → повтор на том же ключе: «Повторить» перезапрашивает дифф', async () => {
    let n = 0;
    diffMock.mockImplementation(() => {
      n += 1;
      return n === 1
        ? Promise.reject(new Error('Файл или версия не найдены'))
        : Promise.resolve(fdiff('A.cs', 'AAA'));
    });
    const { container } = render(arts([file('A.cs', 1)]));
    await act(async () => { await flush(); });
    expect(text(container)).toContain('Файл или версия не найдены');
    expect(diffMock).toHaveBeenCalledTimes(1);
    const retry = findNodeByAttr(container, 'data-testid', 'stage-file-diff-retry');
    expect(retry).not.toBeNull();
    act(() => { (reactProps(retry!)!.onClick as (e: unknown) => void)({ stopPropagation() {}, preventDefault() {} }); });
    await act(async () => { await flush(); });
    expect(diffMock).toHaveBeenCalledTimes(2);
    expect(diffMock.mock.calls[1].slice(0, 4)).toEqual(['t1', 'A.cs', 'all', 'all']);
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-file-diff')!)).toContain('AAA');
  });

  it('сворачивает длинный список: «ещё N файлов», по клику раскрывает', () => {
    diffMock.mockReturnValue(new Promise(() => {}));
    const files = Array.from({ length: 13 }, (_, i) => file(`f${i}.cs`, 1));
    const { container } = render(arts(files));
    expect(findNodeByAttr(container, 'data-testid', 'stage-file-f12.cs')).toBeNull();
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-files-more')!)).toContain('ещё |3| |файла');
    click(container, 'stage-files-more');
    expect(findNodeByAttr(container, 'data-testid', 'stage-file-f12.cs')).not.toBeNull();
  });

  it('размонтирование отменяет запрос', async () => {
    diffMock.mockReturnValue(new Promise(() => {}));
    const { root } = render(arts([file('A.cs', 1)]));
    await act(async () => { await flush(); });
    const signal = diffMock.mock.calls[0][4] as AbortSignal;
    act(() => { root.unmount(); }); roots.length = 0;
    expect(signal.aborted).toBe(true);
  });
});

// F-2.46a: итог исполнителя — общий markdown-рендер ленты, а не сырой текст.
describe('FeatureStageArtifacts · markdown итога', () => {
  const MD = '## Заголовок\n- **пункт** `код`';
  function findByTag(c: FNode, tag: string): FNode | null {
    let hit: FNode | null = null;
    const walk = (x: FNode) => {
      if (hit) return;
      if (x.nodeType === 1 && x.nodeName === tag) { hit = x; return; }
      for (const ch of x.childNodes) walk(ch);
    };
    walk(c);
    return hit;
  }
  // Элементы разметки внутри карточки итога, а не их литералы в тексте
  function expectRendered(result: FNode) {
    const h2 = findByTag(result, 'H2');
    expect(h2, 'нет h2').not.toBeNull();
    expect(textNodes(h2!).join('')).toBe('Заголовок');
    const li = findByTag(result, 'LI');
    expect(li, 'нет li').not.toBeNull();
    const strong = findByTag(li!, 'STRONG');
    expect(strong, 'нет strong').not.toBeNull();
    expect(textNodes(strong!).join('')).toBe('пункт');
    const code = findByTag(li!, 'CODE');
    expect(code, 'нет code').not.toBeNull();
    expect(textNodes(code!).join('')).toBe('код');
  }

  it('новый вид: итог — заголовок/список/strong/code, сырых маркеров нет', () => {
    const { container } = render(arts([], { resultMarkdown: MD }));
    const result = findNodeByAttr(container, 'data-testid', 'stage-result');
    expect(result, 'stage-result').not.toBeNull();
    expectRendered(result!);
    const t = text(container);
    expect(t).not.toContain('##');
    expect(t).not.toContain('**');
  });

  it('старый вид (без files): итог тоже рендерится разметкой', () => {
    const { container } = render(arts([], { resultMarkdown: MD, files: undefined }));
    const result = findNodeByAttr(container, 'data-testid', 'stage-result');
    expect(result, 'stage-result').not.toBeNull();
    expectRendered(result!);
    const t = text(container);
    expect(t).not.toContain('##');
    expect(t).not.toContain('**');
  });
});
