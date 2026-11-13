// Лента личного чата вне проекта (projectId = null): записи video_scene / video_launch_versions рисуют карточки
// по сцене из стора области personal. Данные — слепок реального чата (озвученная версия без cost, provider «sound»).
import { beforeEach, describe, expect, it } from 'vitest';

const memoryStorage = () => {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
  } as Storage;
};
(globalThis as unknown as { localStorage: Storage }).localStorage = memoryStorage();
(globalThis as unknown as { sessionStorage: Storage }).sessionStorage = memoryStorage();

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { VideoThreadsState } from '../api';
import { CATALOG } from '../mocks';
import { PERSONAL_SCOPE } from '../scope';
import { __applyThreads, __resetVideoStore, __setScopeData } from '../store/videoStore';
import { manifest } from '../manifest';
import { LaunchAnchor, SceneAnchor } from './SceneCard';

const CHAT = 'd1e53758-5b59-4f2a-892b-86f6129248c1';
const SCENE = '0cf1072f367b423cb7ca99ca03990f95';

// Нить чата как её отдаёт GET …/state (поле threads), без служебных events/turnCursor
const REAL = {
 "focus": {
  "sceneId": "0cf1072f367b423cb7ca99ca03990f95"
 },
 "revision": 10,
 "scenes": [
  {
   "sceneId": "0cf1072f367b423cb7ca99ca03990f95",
   "name": "Сцена 1",
   "folder": "",
   "settings": {
    "text": "Рыжая лиса медленно идёт по заснеженному лесу, утренний свет пробивается сквозь ели, слышен хруст снега под лапами.",
    "provider": "local",
    "model": "kandinsky-6-lite",
    "durationSec": 5,
    "aspect": "16:9",
    "sound": false,
    "count": 1
   },
   "versions": [
    {
     "versionId": "108762d1004b49d1a85e00e16f870640",
     "number": 1,
     "jobId": "a234a26aaaf7494d9923d9ad485dfb40",
     "variant": 1,
     "provider": "local",
     "model": "kandinsky-6-lite",
     "durationSec": 5,
     "sizeBytes": 1301553,
     "hasSound": true,
     "license": "не указана",
     "cost": {
      "currency": "local",
      "amount": 0
     },
     "initiator": "agent",
     "inputs": {
      "text": "Рыжая лиса медленно идёт по заснеженному лесу, утренний свет пробивается сквозь ели, слышен хруст снега под лапами."
     },
     "createdAt": "2026-11-13T13:25:13.7006764Z",
     "width": 864,
     "height": 480
    },
    {
     "versionId": "f9c25f40e6794afea7388f04923f274d",
     "number": 2,
     "jobId": "9f46dc458b5646188c7de6457d371df3",
     "variant": 1,
     "provider": "local",
     "model": "relayvsr-x2",
     "durationSec": 5,
     "sizeBytes": 7269521,
     "hasSound": true,
     "license": "не указана",
     "cost": {
      "currency": "local",
      "amount": 0
     },
     "initiator": "agent",
     "inputs": {
      "text": "Рыжая лиса медленно идёт по заснеженному лесу, утренний свет пробивается сквозь ели, слышен хруст снега под лапами."
     },
     "createdAt": "2026-11-13T18:53:39.0411175Z",
     "width": 1728,
     "height": 960,
     "enhanceScale": 2
    },
    {
     "versionId": "c15c1abbcd6440448b862526421117cc",
     "number": 3,
     "jobId": "vs_a9c360053b744444b50a5268956621f1",
     "variant": 1,
     "provider": "sound",
     "model": "hunyuanvideo-foley-xl",
     "durationSec": 5,
     "sizeBytes": 1341879,
     "hasSound": true,
     "license": "не указана",
     "initiator": "human",
     "inputs": {
      "text": "Рыжая лиса медленно идёт по заснеженному лесу, утренний свет пробивается сквозь ели, слышен хруст снега под лапами."
     },
     "createdAt": "2026-11-13T18:54:09.9320972Z",
     "width": 864,
     "height": 480
    }
   ],
   "currentVersionId": "c15c1abbcd6440448b862526421117cc",
   "launches": [
    {
     "jobId": "a234a26aaaf7494d9923d9ad485dfb40",
     "at": "2026-11-13T13:21:06.9954777Z",
     "status": "done",
     "interrupted": false,
     "initiator": "agent",
     "provider": "local",
     "model": "kandinsky-6-lite",
     "count": 1,
     "prompt": "Рыжая лиса медленно идёт по заснеженному лесу, утренний свет пробивается сквозь ели, слышен хруст снега под лапами.",
     "license": "не указана"
    },
    {
     "jobId": "9f46dc458b5646188c7de6457d371df3",
     "at": "2026-11-13T18:53:23.2108716Z",
     "status": "done",
     "interrupted": false,
     "initiator": "agent",
     "provider": "local",
     "model": "relayvsr-x2",
     "count": 1,
     "prompt": "Увеличение ×2",
     "license": "не указана"
    },
    {
     "jobId": "vs_a9c360053b744444b50a5268956621f1",
     "at": "2026-11-13T18:54:09.9313862Z",
     "status": "done",
     "interrupted": false,
     "initiator": "human",
     "provider": "sound",
     "model": "hunyuanvideo-foley-xl",
     "count": 1,
     "license": "не указана"
    }
   ],
   "savedFiles": [],
   "createdAt": "2026-11-13T10:18:56.9129087Z"
  },
  {
   "sceneId": "a94446a65e404a818680695f7f93681c",
   "name": "Сцена 2",
   "folder": "",
   "settings": {
    "text": "",
    "durationSec": 5,
    "aspect": "16:9",
    "sound": false,
    "count": 1
   },
   "versions": [],
   "launches": [],
   "savedFiles": [],
   "createdAt": "2026-11-13T18:20:15.6196806Z"
  }
 ]
} as unknown as VideoThreadsState;

const record = (recordType: string, data: Record<string, unknown>): ChatItem =>
  ({ kind: 'module_record', module: 'videoeditor', recordType, data, fallback: 'след' } as unknown as ChatItem);
const ctxOf = (item: ChatItem, sessionId: string | null = CHAT): ChatItemToolCtx =>
  ({ item, online: true, projectId: null, sessionId, persona: null });

describe('лента видео в личном чате', () => {
  beforeEach(() => {
    __resetVideoStore();
    __setScopeData(PERSONAL_SCOPE, CATALOG, { } as never);
    __applyThreads(CHAT, PERSONAL_SCOPE, REAL);
  });

  it('манифест регистрирует якоря под ключами module:recordType из истории', () => {
    const names = (manifest.slots?.['chat-item-tool'] ?? []).map(s => s.name);
    expect(names).toContain('videoeditor:video_scene');
    expect(names).toContain('videoeditor:video_launch_versions');
  });

  it('video_scene рисует карточку сцены с плеером и версией 3', () => {
    const html = renderToStaticMarkup(createElement(SceneAnchor, { ctx: ctxOf(record('video_scene', { sceneId: SCENE })) }));
    expect(html).not.toBe('');
    expect(html).toContain('Сцена 1');
    expect(html).toContain('data-video-enhance');
    expect(html).not.toContain('Сохранить сцену');
  });

  it('video_launch_versions рисует строку запуска', () => {
    const rec = record('video_launch_versions', { sceneId: SCENE, jobId: 'vs_3', provider: 'sound', model: 'hunyuanvideo-foley-xl', count: 1, initiator: 'human' });
    expect(renderToStaticMarkup(createElement(LaunchAnchor, { ctx: ctxOf(rec) }))).not.toBe('');
  });

  it('пустая сцена без версий тоже видна', () => {
    const html = renderToStaticMarkup(createElement(SceneAnchor, { ctx: ctxOf(record('video_scene', { sceneId: 'a94446a65e404a818680695f7f93681c' })) }));
    expect(html).toContain('Сцена 2');
  });
});
