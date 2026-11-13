// Вход и транскрипт (F17): разбор постановки на три секции, «×N» у файлов, таблица инструментов
// «Раз / Последний», кнопка перехода в чат исполнителя (без executorSessionId её нет), легаси-текст.
//
// Мутации (в отчёте): (1) убрать 'stage' из ветки ЭТАП в splitPromptSections → тест «три секции» красный;
// (2) не передавать executorSessionId условием в кнопке → тест «без сессии кнопки нет» красный;
// (3) считать count без сортировки/агрегации (по одному на вызов) → тест toolRows красный.

import { act, createElement } from 'react';
import { describe, it, expect, afterEach } from 'vitest';
import { FeatureStageInput, splitPromptSections, toolRows, fileChips, formatChars } from './FeatureStageInput';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { StageInput, StageToolCall } from '../../types/featureStage';

type FNode = Parameters<typeof findNodeByAttr>[0];

const PROMPT = '## ЗАДАЧА\nсделай\n\n## ПРАВИЛА\nправило\n## ДЕЛЕГИРОВАНИЕ\nделегируй\n## ЭТАП\nшаги по плану\n## КОНТЕКСТ\nконтекст';
const at = (h: number, m: number) => new Date(2026, 0, 1, h, m).toISOString();
const call = (name: string, summary: string | null, when: string | null): StageToolCall => ({ name, summary, at: when, isError: false });

function input(over: Partial<StageInput['transcript'] & object> = {}, avail = true): StageInput {
  return {
    stage: { templateId: 'full', templateVersion: 4, role: 'step', stageId: 's', after: [] },
    sessionId: 'c1', transcriptAvailable: avail,
    transcript: avail ? {
      prompt: PROMPT, filesRead: ['/x/A.cs', '/x/B.cs'],
      toolCalls: [call('Read', '/x/A.cs', at(16, 58)), call('Read', '/x/A.cs', at(17, 1)), call('Edit', null, at(17, 2))],
      toolCallsTotal: 3, truncated: false, ...over,
    } : null,
  };
}

const roots: ReturnType<typeof mount>['root'][] = [];
afterEach(() => { for (const r of roots) act(() => { r.unmount(); }); roots.length = 0; uninstallFakeDom(); });
function render(props: Partial<Parameters<typeof FeatureStageInput>[0]> = {}) {
  installFakeDom();
  const r = mount(createElement(FeatureStageInput, { input: input(), isMobile: false, ...props }));
  roots.push(r.root);
  return r.container as FNode;
}
const text = (c: FNode) => textNodes(c).join('|');
const get = (c: FNode, id: string) => findNodeByAttr(c, 'data-testid', id);
function findAll(c: FNode, id: string): FNode[] {
  const out: FNode[] = [];
  const walk = (x: FNode) => { if (x.nodeType === 1 && x.getAttribute?.('data-testid') === id) out.push(x); for (const ch of x.childNodes) walk(ch); };
  walk(c);
  return out;
}

describe('splitPromptSections', () => {
  it('три секции: правила+делегирование, этап, задача+контекст', () => {
    const s = splitPromptSections(PROMPT);
    expect(s.map(x => x.key)).toEqual(['rules', 'stage', 'task']);
    expect(s[0].text).toContain('правило');
    expect(s[0].text).toContain('делегируй');
    expect(s[1].text).toBe('шаги по плану');
    expect(s[2].text).toContain('сделай');
    expect(s[2].text).toContain('контекст');
  });
  it('без заголовков — одна секция задачи', () => {
    expect(splitPromptSections('просто текст').map(x => x.key)).toEqual(['task']);
  });
  it('размер в знаках с неразрывным пробелом', () => {
    expect(formatChars(3120)).toBe('3 120 знаков');
  });
});

describe('агрегаты', () => {
  it('toolRows: счёт, сортировка по убыванию, последнее время', () => {
    const rows = toolRows([call('Edit', null, at(17, 2)), call('Read', null, at(16, 58)), call('Read', null, at(17, 1))]);
    expect(rows).toEqual([{ name: 'Read', count: 2, last: '17:01' }, { name: 'Edit', count: 1, last: '17:02' }]);
  });
  it('fileChips: «×N» по чтениям, базовое имя', () => {
    const c = fileChips(['/x/A.cs', '/x/B.cs'], [call('Read', '/x/A.cs', null), call('Read', '/x/A.cs', null)]);
    expect(c).toEqual([{ path: '/x/A.cs', name: 'A.cs', count: 2 }, { path: '/x/B.cs', name: 'B.cs', count: 1 }]);
  });
});

describe('FeatureStageInput', () => {
  it('три секции, плашка шаблона у этапа, размер у задачи, чипы и таблица', () => {
    const c = render({ templateName: 'Полный цикл' });
    expect(get(c, 'stage-prompt-rules')).not.toBeNull();
    expect(get(c, 'stage-prompt-stage')).not.toBeNull();
    expect(get(c, 'stage-prompt-task')).not.toBeNull();
    expect(text(get(c, 'stage-prompt-stage-badge')!)).toContain('из шаблона «Полный цикл» v4');
    expect(text(get(c, 'stage-prompt-task')!)).toMatch(/знаков/);
    const t = text(c);
    expect(t).toContain('A.cs ×2');
    expect(textNodes(get(c, 'stage-files-read')!).join('')).toContain('Прочитанные файлы · 2');
    const rows = textNodes(get(c, 'stage-tool-calls')!).join('|');
    expect(rows).toContain('Read|2|17:01');
  });

  it('21 файл: при лимите 3 — три чипа и «ещё 18»; сумма «Раз» равна числу вызовов', () => {
    const files = Array.from({ length: 21 }, (_, i) => `/x/F${i}.cs`);
    const calls = [...files.map(f => call('Read', f, at(16, 0))), call('Edit', null, at(17, 0)), call('Edit', null, at(17, 5))];
    const c = render({ input: input({ filesRead: files, toolCalls: calls, toolCallsTotal: calls.length }) });
    expect(textNodes(get(c, 'stage-files-read')!).join('')).toContain('Прочитанные файлы · 21');
    expect(findAll(c, 'stage-file-chip').length).toBe(3);
    expect(text(get(c, 'stage-files-more')!)).toContain('ещё 18');
    const t = textNodes(get(c, 'stage-tool-calls')!).join('|');
    expect(t).toContain('Инструмент');
    expect(t).toContain('Раз');
    expect(t).toContain('Последний');
    const sum = findAll(c, 'stage-tool-row').reduce((a, r) => a + Number(textNodes(r)[1]), 0);
    expect(sum).toBe(calls.length);
  });

  it('кнопка перехода в чат: есть при executorSessionId и зовёт onOpenChat, без него нет', () => {
    let opened: string | null = null;
    const c = render({ executorSessionId: 's9', onOpenChat: id => { opened = id; } });
    const btn = get(c, 'stage-input-open-chat');
    expect(btn).not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(opened).toBe('s9');
    const c2 = render({ executorSessionId: null, onOpenChat: () => {} });
    expect(get(c2, 'stage-input-open-chat')).toBeNull();
  });

  it('легаси: транскрипт не разобран — текст с отсылкой к чату', () => {
    const c = render({ input: input({}, false) });
    expect(text(get(c, 'stage-transcript-empty')!)).toContain('не разобран — откройте чат исполнителя');
  });
});
