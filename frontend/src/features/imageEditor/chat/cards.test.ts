// Лента запуска агентом (image_generate): один запуск — один блок. У нити с версиями
// (v3) строку запуска, прогресс, «Отменить» и версии рисует якорь image_launch_versions,
// карточка вызова инструмента молчит. Старые записи (v2, нить без версий) — прежняя
// карточка, без ошибок. Рендер статикой через react-dom/server, как соседние тесты ленты.
import { beforeEach, describe, expect, it } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { manifest } from '../manifest';
import { __applyThreads, __resetThreadStore } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';

const P = 'p1';
const S = 's1';

const toolUse = (threadId: string, jobId: string): ChatItem => ({
  kind: 'tool_use', id: 'tu1', name: 'mcp__image-editor__image_generate',
  input: { threadId, prompt: 'Андрей на пляже' },
  result: JSON.stringify({ jobId, threadId, quote: { provider: 'local', model: 'qwen-image-2.1', estimate: { amount: 0, unit: 'free', approx: false }, expectedSeconds: 60 } }),
} as ChatItem);

const launchRecord = (threadId: string, jobId: string): ChatItem => ({
  kind: 'module_record', module: 'imageeditor', recordType: 'image_launch_versions',
  data: { threadId, jobId, prompt: 'Андрей на пляже', model: 'qwen-image-2.1', count: 1, initiator: 'agent', baseVersionId: 'v2' },
  fallback: 'Claude: правка от версии 2',
} as unknown as ChatItem);

const v3 = (): ImageThread => ({
  id: 't1', file: null, lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-09-28T00:00:00Z',
  versions: [
    { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: '2026-09-28T00:00:00Z' },
    { id: 'v2', number: 2, jobId: 'j0', variant: 0, baseVersionId: 'origin', baseStepId: null, steps: ['s2'], currentStepId: 's2', createdAt: '2026-09-28T00:00:00Z' },
  ],
  currentVersionId: 'v2',
  launches: [{ jobId: 'j1', status: 'running', prompt: 'Андрей на пляже', initiator: 'agent', baseVersionId: 'v2' }],
} as unknown as ImageThread);

// Нить картинки v2: стопка шагов, версий и запусков нет
const legacy = (): ImageThread => ({
  id: 't1', file: 'img/hero.png', lineage: [], draftFolder: null,
  stacks: [{ stackId: 'k1', steps: ['a', 'b'], forkedFromStepId: null, old: false }],
  currentStackId: 'k1', currentStepId: 'b', settings: null, pendingJobId: null, createdAt: '2026-09-20T00:00:00Z',
});

// Лента как в ChatItemView: запись идёт во вклад chat-item-tool по имени инструмента или ключу записи
function renderFeed(items: ChatItem[]): string {
  const slot = manifest.slots['chat-item-tool'] as { name: string; render: (ctx: ChatItemToolCtx) => unknown }[];
  return items.map(item => {
    const key = item.kind === 'tool_use' ? item.name
      : item.kind === 'module_record' ? `${(item as { module: string }).module}:${(item as { recordType: string }).recordType}` : item.kind;
    const view = slot.find(c => c.name === key);
    if (!view) throw new Error(`нет вклада для ${key}`);
    const Wrap = () => view.render({ item, online: true, projectId: P, sessionId: S, persona: null }) as ReturnType<typeof createElement>;
    return renderToStaticMarkup(createElement(Wrap));
  }).join('\n');
}

const count = (html: string, needle: string) => html.split(needle).length - 1;

beforeEach(() => {
  store.clear();
  __resetThreadStore();
});

describe('лента: запуск агентом в нить с версиями', () => {
  it('один блок на запуск: строка запуска с одним прогрессом и одной «Отменить»', () => {
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [v3()] });
    const html = renderFeed([toolUse('t1', 'j1'), launchRecord('t1', 'j1')]);
    expect(count(html, 'data-image-launch=')).toBe(1);
    expect(count(html, 'data-image-card=')).toBe(0);
    expect(count(html, 'Отменить')).toBe(1);
    expect(html).not.toContain('Открыть в редакторе');
    expect(html).not.toContain('Запущена генерация');
  });

  it('ошибка запуска остаётся видна карточкой: якоря у неё нет', () => {
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [v3()] });
    const err = { ...toolUse('t1', 'j9'), result: JSON.stringify({ error: 'Нет такой модели' }) } as ChatItem;
    const html = renderFeed([err]);
    expect(html).toContain('Генерация не запущена');
  });
});

describe('лента: старые записи v2', () => {
  it('вызов image_generate по нити без версий рисует прежнюю карточку', () => {
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [legacy()] });
    const html = renderFeed([toolUse('t1', 'j1')]);
    expect(count(html, 'data-image-card=')).toBe(1);
    expect(html).toContain('Запущена генерация');
  });

  it('тихая строка image_launch v2 рендерится', () => {
    const row = { kind: 'image_launch', by: 'user', prompt: 'вечер', provider: 'fal', model: null, count: 2, estimate: null } as unknown as ChatItem;
    const html = renderFeed([row]);
    expect(html).toContain('Вы запустили');
    expect(html).toContain('«вечер»');
  });
});
