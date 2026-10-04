// Лента звука: каждая версия нити — одна полноценная карточка на своём месте. Запуск на N вариантов —
// N карточек, каждая на своей версии и со своими действиями; пока запуск идёт — одна карточка хода.
// Рендер статикой через react-dom/server, как у карточек агента.
import { beforeEach, describe, expect, it } from 'vitest';

const store = new Map<string, string>();
const memoryStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null,
  setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); },
  clear: () => m.clear(),
  key: () => null,
  length: 0,
} as Storage);
// Ссылка на файл версии берёт токен из хранилищ — в node их нет
(globalThis as unknown as { localStorage: Storage }).localStorage = memoryStorage(store);
(globalThis as unknown as { sessionStorage: Storage }).sessionStorage = memoryStorage(new Map());

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { setAllFlags } from '../../../lib/featureFlags';
import { __applyChatContext, __resetChatContextStore } from '../../../lib/chatContext/store';
import type { AudioThread, AudioThreadLaunch, AudioThreadVersion } from '../api';
import { LaunchAnchor, ThreadAnchor } from './ThreadCard';
import { __applyThreads, __resetAudioStore, setSelection } from './threadStore';

const P = 'p1';
const S = 's1';

const ver = (id: string, number: number, jobId: string | null, variant: number | null, baseVersionId: string | null): AudioThreadVersion => ({
  id, number, jobId, variant, baseVersionId, files: [{ role: 'main', path: `${id}.mp3` }], license: null, createdAt: '',
});
const launch = (jobId: string, status: AudioThreadLaunch['status'], baseVersionId: string | null): AudioThreadLaunch => ({
  jobId, baseVersionId, at: '', status, initiator: 'human', prompt: `песенка ${jobId}`, license: null,
});

// Песня из черновика: два запуска по два варианта, второй — от второго варианта первого
const song = (extra: Partial<AudioThread> = {}): AudioThread => ({
  id: 't1', file: null, name: 'Детская песенка', lineage: [], draftFolder: '', createdAt: '',
  versions: [ver('v1', 1, 'j1', 1, null), ver('v2', 2, 'j1', 2, null), ver('v3', 3, 'j2', 1, 'v2'), ver('v4', 4, 'j2', 2, 'v2')],
  currentVersionId: 'v4',
  launches: [launch('j1', 'done', null), launch('j2', 'done', 'v2')],
  settings: { mode: 'music', operation: 'song', provider: 'local', model: 'ace-step', fields: null },
  ...extra,
});

const record = (recordType: string, data: Record<string, unknown>): ChatItem =>
  ({ kind: 'module_record', module: 'audioeditor', recordType, data, fallback: 'след' } as unknown as ChatItem);
const threadAnchor = (versionId: string | null) => record('audio_thread', { threadId: 't1', versionId });
const launchAnchor = (jobId: string, count = 2) => record('audio_launch_versions', { threadId: 't1', jobId, op: 'song', model: 'ace-step', count, initiator: 'human' });

function renderFeed(items: ChatItem[]): string {
  return items.map(item => {
    const r = item as unknown as { recordType: string };
    const C = r.recordType === 'audio_thread' ? ThreadAnchor : LaunchAnchor;
    const ctx: ChatItemToolCtx = { item, online: true, projectId: P, sessionId: S, persona: null };
    return renderToStaticMarkup(createElement(C, { ctx }));
  }).join('\n');
}

// Карточки в порядке ленты: id версии и признак «в работе»
const cards = (html: string) =>
  [...html.matchAll(/data-audio-card="([^"]+)" data-current="(true|false)"/g)].map(m => `${m[1]}${m[2] === 'true' ? '*' : ''}`);
// Разметка одной карточки — от её рамки до следующей
const cardHtml = (html: string, id: string) => {
  const from = html.indexOf(`data-audio-card="${id}"`);
  const next = html.indexOf('data-audio-card=', from + 1);
  return html.slice(from, next < 0 ? undefined : next);
};
const count = (html: string, needle: string) => html.split(needle).length - 1;

// Основной объект контекста: «в работе» карточки берётся из него, а не из фокуса нитей
const work = (versionId: string | null) => __applyChatContext(S, {
  revision: 1, refs: [],
  primary: {
    id: 'p', kind: 'audio', ref: versionId ? { threadId: 't1', versionId } : { threadId: 't1' }, by: 'human', addedAt: '',
    label: 'песенка', version: versionId, thumb: null, missing: false, role: null,
  },
} as never);

beforeEach(() => {
  store.clear();
  __resetAudioStore();
  __resetChatContextStore();
  setAllFlags({ 'audio-editor': true });
});

describe('лента звука: карточка на каждый вариант', () => {
  it('два запуска по два варианта — четыре полноценные карточки, каждая на своей версии, без повторов', () => {
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [song()] });
    work('v4');
    const html = renderFeed([threadAnchor(null), launchAnchor('j1'), launchAnchor('j2')]);

    // Черновик после запуска молчит; версия — ровно одна карточка, в работе — только текущая
    expect(cards(html)).toEqual(['v1', 'v2', 'v3', 'v4*']);
    expect(html).not.toContain('Ещё не создан');
    // Полноценная карточка у каждой: подпись своей версии, «В контекст», сохранение
    for (const [id, tag] of [['v1', 'версия 1 · вариант 1 из 2'], ['v2', 'версия 2 · вариант 2 из 2'], ['v3', 'версия 3 · вариант 1 из 2'], ['v4', 'версия 4 · вариант 2 из 2']]) {
      const c = cardHtml(html, id);
      expect(c, id).toContain(tag);
      expect(c, id).not.toContain('data-audio-process');
      expect(c, id).toContain('Сохранить в проект');
    }
    // «Работать с этой» — у всех, кроме карточки текущей версии
    expect(count(html, 'Работать с этой')).toBe(3);
    expect(cardHtml(html, 'v4')).not.toContain('Работать с этой');
    // A/B — с основой своей версии: у варианта второго запуска A — версия 2
    expect(cardHtml(html, 'v3')).toContain('A · в2');
    expect(cardHtml(html, 'v3')).toContain('B · в3');
    // Урезанных вариантов больше нет
    expect(html).not.toContain('data-audio-variant');
    expect(html).not.toContain('>Взять<');
  });

  it('выделение куска — у карточки той версии, на которой выделяли', () => {
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [song()] });
    setSelection(S, 't1', { start: 1, end: 2, versionId: 'v2' });
    const html = renderFeed([launchAnchor('j1'), launchAnchor('j2')]);
    expect(cardHtml(html, 'v2')).toContain('Открыть в редакторе');
    for (const id of ['v1', 'v3', 'v4']) expect(cardHtml(html, id), id).not.toContain('Открыть в редакторе');
  });

  it('запуск идёт — одна карточка хода с «Отменить», без карточек вариантов', () => {
    const t = song({ launches: [launch('j1', 'done', null), launch('j2', 'done', 'v2'), launch('j3', 'running', 'v4')] });
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [t] });
    const html = renderFeed([launchAnchor('j3')]);
    expect(cards(html)).toEqual(['running']);
    expect(count(html, 'Отменить')).toBe(1);
    expect(html).toContain('«песенка j3»');
  });

  it('якорь нити с версией — карточка этой версии: исходник, правка без ИИ', () => {
    const t = song({
      file: 'music/song.mp3',
      versions: [ver('origin', 0, null, null, null), ...song().versions, ver('v5', 5, 'dsp', null, 'v4')],
      currentVersionId: 'v5',
    });
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [t] });
    work('v5');
    const html = renderFeed([threadAnchor('origin'), launchAnchor('j1'), launchAnchor('j2'), threadAnchor('v5')]);
    expect(cards(html)).toEqual(['origin', 'v1', 'v2', 'v3', 'v4', 'v5*']);
    expect(cardHtml(html, 'origin')).toContain('исходник');
    expect(cardHtml(html, 'v5')).toContain('версия 5');
  });

  it('черновик до первого запуска — пунктир', () => {
    __applyThreads(S, P, { focus: 't1', revision: 1, threads: [song({ versions: [], launches: [], currentVersionId: null })] });
    work(null);
    expect(cards(renderFeed([threadAnchor(null)]))).toEqual(['draft*']);
  });
});
