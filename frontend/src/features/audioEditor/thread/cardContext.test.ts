// Карточка версии звука при флаге composer-context-row (ADR-023, 2з-3): «Работать с этой» и «В контекст ▾»
// вместо «Обработать ▾», бейдж «в работе» — из контекста чата, а не из фокуса нитей. Рендер статикой.
import { beforeEach, describe, expect, it } from 'vitest';

const memoryStorage = () => {
  const m = new Map<string, string>();
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); }, removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0 } as Storage;
};
(globalThis as unknown as { localStorage: Storage }).localStorage = memoryStorage();
(globalThis as unknown as { sessionStorage: Storage }).sessionStorage = memoryStorage();

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { setAllFlags } from '../../../lib/featureFlags';
import { __applyChatContext, __resetChatContextStore } from '../../../lib/chatContext/store';
import type { AudioThread } from '../api';
import { ThreadAnchor } from './ThreadCard';
import { __applyThreads, __resetAudioStore } from './threadStore';

const P = 'p1';
const S = 's1';
const song: AudioThread = {
  id: 't1', file: 'music/intro.mp3', lineage: [], draftFolder: null, createdAt: '',
  versions: [
    { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, files: [{ role: 'main', path: 'music/intro.mp3' }], license: null, createdAt: '' },
    { id: 'v1', number: 1, jobId: 'j1', variant: 1, baseVersionId: 'origin', files: [{ role: 'main', path: 'v1.mp3' }], license: null, createdAt: '' },
  ],
  currentVersionId: 'v1', launches: [], settings: { mode: 'music', operation: null, provider: null, model: null, fields: null },
};
const primary = (by: 'human' | 'agent', versionId = 'v1') => ({
  id: 'p', kind: 'audio', ref: { threadId: 't1', versionId }, by, addedAt: '', label: 'intro.mp3', version: versionId, thumb: null, missing: false, role: null,
});

function card(versionId: string): string {
  const item = { kind: 'module_record', module: 'audioeditor', recordType: 'audio_thread', data: { threadId: 't1', versionId }, fallback: '' } as unknown as ChatItem;
  const ctx: ChatItemToolCtx = { item, online: true, projectId: P, sessionId: S, persona: null };
  return renderToStaticMarkup(createElement(ThreadAnchor, { ctx }));
}

beforeEach(() => {
  __resetAudioStore();
  __resetChatContextStore();
  __applyThreads(S, P, { focus: 't1', revision: 1, threads: [song] });
});

describe('карточка звука по флагу composer-context-row', () => {
  it('без флага: «Обработать ▾» и «Работать с этой», «В контекст» нет', () => {
    setAllFlags({ 'audio-editor': true });
    const html = card('origin');
    expect(html).toContain('data-audio-process');
    expect(html).toContain('Работать с этой');
    expect(html).not.toContain('В контекст');
  });

  it('с флагом: «Обработать ▾» не рисуется, вместо него «Работать с этой» и «В контекст»', () => {
    setAllFlags({ 'audio-editor': true, 'composer-context-row': true });
    __applyChatContext(S, { revision: 1, primary: primary('human'), refs: [] });
    const html = card('origin');
    expect(html).not.toContain('data-audio-process');
    expect(html).toContain('Работать с этой');
    expect(html).toContain('В контекст');
  });

  it('с флагом «в работе» — карточка основного объекта: рамка и бейдж, кнопок наполнения нет', () => {
    setAllFlags({ 'audio-editor': true, 'composer-context-row': true });
    __applyChatContext(S, { revision: 1, primary: primary('human'), refs: [] });
    const html = card('v1');
    expect(html).toContain('data-current="true"');
    expect(html).toContain('в работе');
    expect(html).not.toContain('Работать с этой');
    expect(html).not.toContain('data-audio-process');
  });

  it('основной объект выбрал агент — «в работе ✦»; другая версия той же нити не подсвечена', () => {
    setAllFlags({ 'audio-editor': true, 'composer-context-row': true });
    __applyChatContext(S, { revision: 1, primary: primary('agent'), refs: [] });
    expect(card('v1')).toContain('в работе ✦');
    const other = card('origin');
    expect(other).toContain('data-current="false"');
    expect(other).not.toContain('в работе');
  });
});
