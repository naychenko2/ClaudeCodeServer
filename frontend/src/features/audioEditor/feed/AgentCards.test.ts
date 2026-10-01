// Карточки вызовов агента audio_* в ленте: каждый инструмент — свой вид, отказы сторожей
// хода — человеческим текстом, без флага audio-editor вкладов нет. Рендер статикой через
// react-dom/server, как у карточек картинок.
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
import { getSlotItem, registerSubsystem, type ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { setAllSubsystems } from '../../../lib/subsystems';
import { setAllFlags } from '../../../lib/featureFlags';
import type { AudioCatalog, AudioJob, AudioThread } from '../api';
import { soundMode } from '../composer/soundMode';
import { manifest } from '../manifest';
import { __applyThreads, __resetAudioStore, __setScopeData, suggestPrompt } from '../thread/threadStore';
import { AudioConcatCard, AudioFocusLine, AudioLaunchCard, AudioPromptCard, AudioServiceLine, failText } from './AgentCards';
import { AUDIO_TOOL } from './parse';

const P = 'p1';
const S = 's1';

const CATALOG: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4,
  providers: [{
    key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
    models: [{
      id: 'qwen3-tts', label: 'Qwen3-TTS 1.7B',
      caps: { ops: ['speak'], languages: ['ru'], voiceKinds: ['preset'], producesFiles: ['main'], license: { label: 'Apache-2.0', kind: 'permissive' }, priceUnit: 'free' },
    }],
  }],
};

const thread = (launchStatus: AudioThread['launches'][number]['status'] | null): AudioThread => ({
  id: 't1', file: 'audio/intro.mp3', lineage: [], draftFolder: null, createdAt: '2026-10-01T10:00:00Z',
  versions: [
    { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, files: [{ role: 'main', path: 'audio/intro.mp3' }], license: null, createdAt: '' },
    ...(launchStatus === 'done' ? [
      { id: 'v1', number: 1, jobId: 'j1', variant: 1, baseVersionId: 'origin', files: [], license: null, createdAt: '' },
      { id: 'v2', number: 2, jobId: 'j1', variant: 2, baseVersionId: 'origin', files: [], license: null, createdAt: '' },
    ] : []),
  ],
  currentVersionId: 'origin',
  launches: launchStatus ? [{ jobId: 'j1', baseVersionId: 'origin', at: '', status: launchStatus, initiator: 'agent', prompt: null, license: null }] : [],
  settings: { mode: 'voice', operation: 'speak', provider: 'local', model: 'qwen3-tts', fields: null },
});

function withThreads(...threads: AudioThread[]) {
  __applyThreads(S, P, { focus: threads[0]?.id ?? null, revision: 1, threads });
}

const tool = (name: string, input: Record<string, unknown>, result?: string, isError = false): ChatItem => ({
  kind: 'tool_use', id: `tu-${name}`, name: AUDIO_TOOL(name), input, result, isError,
} as unknown as ChatItem);

const ctx = (item: ChatItem): ChatItemToolCtx => ({ item, online: true, projectId: P, sessionId: S, persona: null });

const html = (C: (p: { ctx: ChatItemToolCtx }) => unknown, item: ChatItem) =>
  renderToStaticMarkup(createElement(C as never, { ctx: ctx(item) }));

const LAUNCH = JSON.stringify({
  jobId: 'j1', threadId: 't1', baseVersion: { versionId: 'origin', label: 'исходник' },
  quote: { provider: 'local', model: 'qwen3-tts', op: 'speak', count: 2, price: { amount: 0, unit: 'free', approx: false } },
});

beforeEach(() => {
  __resetAudioStore();
  __setScopeData(P, CATALOG, { voice: null, music: null, process: null });
});

describe('audio_generate', () => {
  it('идёт: операция, поставщик · модель, цена и «Отменить»', () => {
    withThreads(thread('running'));
    const out = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' }, LAUNCH));
    expect(out).toContain('Claude — запуск: Озвучить');
    expect(out).toContain('Локально · Qwen3-TTS 1.7B · 2 вар. · бесплатно');
    expect(out).toContain('От: исходник');
    expect(out).toContain('Отменить');
  });

  it('готово: ссылки на версии запуска', () => {
    withThreads(thread('done'));
    const out = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' }, LAUNCH));
    expect(out).toContain('Готово: Озвучить');
    expect(out).toContain('Версия 1');
    expect(out).toContain('Версия 2');
    expect(out).not.toContain('Отменить');
  });

  it('ошибка: «Не получилось» с причиной', () => {
    withThreads(thread('failed'));
    const out = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' }, LAUNCH));
    expect(out).toContain('Не получилось: Озвучить');
    expect(out).toContain('Поставщик не справился.');
  });

  it('причина сбоя — текст задачи и деньги', () => {
    expect(failText({ outcome: 'failed', error: 'таймаут', charged: false } as AudioJob))
      .toBe('Поставщик не справился. таймаут. Деньги не списаны.');
    expect(failText({ outcome: 'insufficientCredits', error: null, charged: null } as AudioJob))
      .toBe('У поставщика закончились средства.');
  });

  it('прервалось и звук удалён', () => {
    withThreads(thread('interrupted'));
    expect(html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' }, LAUNCH))).toContain('Сервер перезапускался');
    withThreads();
    expect(html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' }, LAUNCH))).toContain('Этого звука в чате уже нет');
  });

  it('лимит хода — понятным текстом', () => {
    const out = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' },
      'За один ход можно запустить не больше 2 операций со звуком. Покажи человеку, что уже получилось, и дождись его ответа.', true));
    expect(out).toContain('Лимит запусков за ход');
    expect(out).toContain('не больше 2 операций со звуком. Ответьте в чате');
    expect(out).not.toContain('Покажи человеку');
  });

  it('делегированный ход и ход-ответ на доклад — понятным текстом', () => {
    const delegated = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' },
      'Запуск операции со звуком недоступно на делегированном ходу: этот ход инициирован другим чатом. Верни результат тому, кто тебя позвал.', true));
    expect(delegated).toContain('Запуск недоступен на чужом ходу');
    expect(delegated).not.toContain('Верни результат');
    const report = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' },
      'Запуск операции со звуком недоступно на этом ходу: ты отвечаешь на доклад исполнителя.', true));
    expect(report).toContain('ответ на доклад исполнителя');
  });

  it('отказ исполнителя JSON — его текст', () => {
    const out = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' },
      JSON.stringify({ error: 'Поставщик «fal» больше недоступен', code: 'provider_unavailable' }), true));
    expect(out).toContain('Операция не запущена');
    expect(out).toContain('Поставщик «fal» больше недоступен');
    expect(out).not.toContain('provider_unavailable');
  });

  it('монтаж без ИИ — готовая версия сразу', () => {
    withThreads(thread(null));
    const out = html(AudioLaunchCard, tool('audio_generate', { threadId: 't1', op: 'trim' },
      JSON.stringify({ threadId: 't1', versionId: 'v9', note: '…' })));
    expect(out).toContain('Готово: Обрезка и громкость');
    expect(out).toContain('монтаж без ИИ · бесплатно');
    expect(out).toContain('Показать версию');
  });

  it('вызов ещё идёт', () => {
    expect(html(AudioLaunchCard, tool('audio_generate', { threadId: 't1' }))).toContain('Запускаю операцию со звуком');
  });
});

describe('audio_concat', () => {
  it('склеено: имя, число кусков, переход к звуку', () => {
    withThreads({ ...thread(null), id: 't2', file: null, name: 'склейка.wav' });
    const out = html(AudioConcatCard, tool('audio_concat', { pieces: [{ threadId: 'a' }, { threadId: 'b' }, { file: 'c.wav' }] },
      JSON.stringify({ threadId: 't2', versionId: 'v1', name: 'склейка.wav' })));
    expect(out).toContain('Склеено: склейка.wav');
    expect(out).toContain('кусков: 3');
    expect(out).toContain('Показать звук');
  });

  it('отказ', () => {
    const out = html(AudioConcatCard, tool('audio_concat', { pieces: [] }, 'pieces — список кусков: у каждого threadId (и versionId) или file.', true));
    expect(out).toContain('Склейка не выполнена');
  });
});

describe('audio_suggest_prompt', () => {
  it('с выбранным звуком — «Вставить в промпт» и «Сгенерировать» с ценой', () => {
    withThreads(thread(null));
    const out = html(AudioPromptCard, tool('audio_suggest_prompt', { prompt: 'Добрый вечер!', mode: 'voice' }, '{}'));
    expect(out).toContain('Добрый вечер!');
    expect(out).toContain('Голос');
    expect(out).toContain('Вставить в промпт');
    expect(out).toContain('Сгенерировать · бесплатно');
  });

  it('без выбранного звука — подсказка вместо кнопок', () => {
    const out = html(AudioPromptCard, tool('audio_suggest_prompt', { prompt: 'Добрый вечер!' }, '{}'));
    expect(out).toContain('Выберите звук в полосе «Звук»');
    expect(out).not.toContain('Сгенерировать');
  });

  it('«Вставить в промпт» включает режим «Звук» и кладёт текст в поле', () => {
    withThreads(thread(null));
    const mctx = { projectId: P, sessionId: S };
    expect(soundMode.prefill!(mctx)).toBeNull();
    suggestPrompt(S, 'Добрый вечер!');
    expect(soundMode.prefill!(mctx)).toEqual({ key: 'suggest:1', text: 'Добрый вечер!' });
    expect(soundMode.autoSelect!(mctx)).toBe('request:1');
  });
});

describe('audio_focus и audio_new', () => {
  const focused = JSON.stringify({
    focus: 't1',
    thread: { threadId: 't1', file: 'audio/intro.mp3', currentVersionId: 'v3', versions: [{ versionId: 'v3', label: 'версия 3' }] },
  });

  it('«Claude взял в работу: файл · версия» — кнопкой', () => {
    const out = html(AudioFocusLine, tool('audio_focus', { threadId: 't1' }, focused));
    expect(out).toContain('Claude взял в работу');
    expect(out).toMatch(/<button[^>]*>intro\.mp3 · версия 3<\/button>/);
  });

  it('«Claude завёл новый звук»', () => {
    const out = html(AudioFocusLine, tool('audio_new', { mode: 'music' },
      JSON.stringify({ focus: 't5', thread: { threadId: 't5', name: 'Новый звук', versions: [] } })));
    expect(out).toContain('Claude завёл новый звук (музыка)');
    expect(out).toContain('Новый звук');
  });

  it('снятый выбор и отказ', () => {
    expect(html(AudioFocusLine, tool('audio_focus', {}, JSON.stringify({ note: '…' })))).toContain('снял выбор звука');
    expect(html(AudioFocusLine, tool('audio_focus', { file: 'x.wav' }, 'Звуковой файл не найден в проекте: x.wav', true)))
      .toContain('Звуковой файл не найден в проекте: x.wav');
  });
});

describe('служебные: audio_state, audio_voices, audio_cancel', () => {
  it('строки без сырого JSON', () => {
    const state = html(AudioServiceLine, tool('audio_state', {}, JSON.stringify({ threads: [{}, {}], catalog: { providers: [] } })));
    expect(state).toContain('Звуки чата: 2 звука');
    expect(state).not.toContain('catalog');
    const voices = html(AudioServiceLine, tool('audio_voices', { language: 'ru' },
      JSON.stringify({ providers: [{ provider: 'local', voices: [{}, {}, {}, {}, {}] }], library: [{}] })));
    expect(voices).toContain('Дикторы (ru): 5 дикторов, голосов проекта: 1');
    const cancel = html(AudioServiceLine, tool('audio_cancel', { jobId: 'j1' },
      JSON.stringify({ jobId: 'j1', status: 'cancelled', charged: false, variants: 1 })));
    expect(cancel).toContain('Операция со звуком отменена · готовых вариантов: 1 · деньги не списаны');
  });

  it('отказ служебного вызова', () => {
    expect(html(AudioServiceLine, tool('audio_cancel', { jobId: 'x' }, 'Задача не найдена.', true)))
      .toContain('Не удалось отменить операцию: Задача не найдена.');
  });
});

describe('флаг audio-editor', () => {
  it('без флага вкладов в ленте нет, с флагом — на каждый инструмент', () => {
    registerSubsystem(manifest);
    setAllSubsystems(['audioeditor']);
    setAllFlags({ 'audio-editor': false });
    expect(getSlotItem('chat-item-tool', AUDIO_TOOL('audio_generate'))).toBeUndefined();
    setAllFlags({ 'audio-editor': true });
    for (const t of ['audio_state', 'audio_focus', 'audio_new', 'audio_voices', 'audio_generate', 'audio_concat', 'audio_suggest_prompt', 'audio_cancel']) {
      expect(getSlotItem('chat-item-tool', AUDIO_TOOL(t))?.render, t).toBeTypeOf('function');
    }
  });
});
