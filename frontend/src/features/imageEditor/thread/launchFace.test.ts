import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { imageEditorApi } from '../api';
import { LaunchAnchor, VersionCard } from './VersionCards';
import { putCharacter } from '../characters/useCharacters';
import { __applyThreads, __resetThreadStore } from './threadStore';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ImageThread, ImageThreadLaunch, ImageThreadVersion, ImageThreadsState } from './threadsApi';

// «Подставить персонажа» (этап 2 · шаг 10c): подпись «{Имя} → лицо {n}» на карточке запуска
// в ленте — в строке идущего запуска и в meta карточки версии. SSR-рендер: DOM-свободно,
// как в originAnchor.test.ts.

const ver = (id: string, patch: Partial<ImageThreadVersion> = {}): ImageThreadVersion => ({
  id, number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null,
  steps: [], currentStepId: null, createdAt: '2026-09-29T00:00:00Z', ...patch,
});

const launch = (patch: Partial<ImageThreadLaunch> = {}): ImageThreadLaunch => ({
  jobId: 'j1', baseVersionId: 'origin', baseStepId: null, at: '2026-09-29T00:00:00Z',
  status: 'done', initiator: 'human', prompt: 'кот', ...patch,
});

const thread = (launches: ImageThreadLaunch[], extra: ImageThreadVersion[] = []): ImageThread => ({
  id: 't1', file: 'img/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-09-29T00:00:00Z',
  versions: [ver('origin'), ...extra], currentVersionId: extra.length ? extra[0].id : 'origin',
  launches,
});

const state = (t: ImageThread, revision = 3): ImageThreadsState => ({ focus: t.id, revision, threads: [t] });

// Персонаж в списке проекта: имя по slug
const marina = { slug: 'marina', name: 'Марина', path: 'characters/marina', photos: [], createdAt: '2026-09-29T00:00:00Z' };

const faceTarget = { n: 2, bBox: [10, 10, 50, 50], characterSlug: 'marina' };

beforeEach(() => {
  __resetThreadStore();
  vi.restoreAllMocks();
});

describe('VersionCard: подпись подстановки', () => {
  const version = () =>
    ver('v1', { number: 1, jobId: 'j1', variant: 0, baseVersionId: 'origin', steps: ['s1'], currentStepId: 's1', similarity: 0.81 });

  it('faceTarget: meta — «{Имя} → лицо {n} · сходство · …», имя по slug из списка персонажей', () => {
    putCharacter('p1', marina);
    const t = thread([launch({ faceTarget })], [version()]);
    const html = renderToStaticMarkup(createElement(VersionCard, {
      projectId: 'p1', sessionId: 's1', thread: t, version: t.versions![1], focused: false,
    }));
    expect(html).toContain('Марина → лицо 2');
    expect(html).toContain('сходство 0,81');
  });

  it('faceTarget, персонажа нет в списке: fallback на slug', () => {
    putCharacter('p2', { ...marina, slug: 'other', name: 'Другой' });
    const t = thread([launch({ faceTarget })], [version()]);
    const html = renderToStaticMarkup(createElement(VersionCard, {
      projectId: 'p2', sessionId: 's1', thread: t, version: t.versions![1], focused: false,
    }));
    expect(html).toContain('marina → лицо 2');
    expect(html).not.toContain('Другой → лицо');
  });

  it('без faceTarget: meta прежняя — подписи нет (регресс)', () => {
    putCharacter('p3', marina);
    const t = thread([launch()], [version()]);
    const html = renderToStaticMarkup(createElement(VersionCard, {
      projectId: 'p3', sessionId: 's1', thread: t, version: t.versions![1], focused: false,
    }));
    expect(html).not.toContain('→ лицо');
    // Прежний состав meta: «от исходника» на месте
    expect(html).toContain('от исходника');
  });
});

describe('LaunchAnchor: строка идущего запуска', () => {
  const ctx = (projectId: string, data: Record<string, unknown>): ChatItemToolCtx => ({
    item: { kind: 'module_record', module: 'imageeditor', recordType: 'image_launch_versions', data, fallback: null } as never,
    online: true, projectId, sessionId: 's1', persona: null,
  });

  it('faceTarget: строка «{имя нити} · {Имя} → лицо {n} · {info}»', () => {
    putCharacter('p4', marina);
    const t = thread([launch({ status: 'running', faceTarget })]);
    __applyThreads('s1', 'p4', state(t));
    const html = renderToStaticMarkup(createElement(LaunchAnchor, {
      ctx: ctx('p4', { threadId: 't1', jobId: 'j1', model: 'Qwen-Image', count: 1, estimate: { amount: 0, unit: 'free' } }),
    }));
    expect(html).toContain('hero.png · Марина → лицо 2 · Qwen-Image · 1 вариант · бесплатно');
  });

  it('без faceTarget: строка прежняя — подписи нет (регресс)', () => {
    putCharacter('p5', marina);
    const t = thread([launch({ status: 'running' })]);
    __applyThreads('s1', 'p5', state(t));
    const html = renderToStaticMarkup(createElement(LaunchAnchor, {
      ctx: ctx('p5', { threadId: 't1', jobId: 'j1', model: 'Qwen-Image', count: 1 }),
    }));
    expect(html).not.toContain('→ лицо');
    expect(html).toContain('hero.png · Qwen-Image · 1 вариант');
  });
});

// Личный чат вне проекта (регрессия naychenko.me): ctx.projectId = null → область 'personal'.
// useCharacters('personal') не должен дёргать проектную ручку персонажей (projectBase бросает
// синхронно) — рендер якоря/карточки в личной области идёт без броска и без /characters.
describe('LaunchAnchor / VersionCard: личный чат (ctx.projectId = null)', () => {
  it('LaunchAnchor: рендер без броска, в /characters не ходит', async () => {
    const listChars = vi.spyOn(imageEditorApi(), 'listCharacters');
    const t = thread([launch({ status: 'running' })]);
    __applyThreads('s1', 'personal', state(t));
    const html = renderToStaticMarkup(createElement(LaunchAnchor, {
      ctx: {
        item: { kind: 'module_record', module: 'imageeditor', recordType: 'image_launch_versions',
          data: { threadId: 't1', jobId: 'j1', model: 'Qwen-Image', count: 1 }, fallback: null } as never,
        online: true, projectId: null, sessionId: 's1', persona: null,
      },
    }));
    expect(html).toContain('hero.png');
    await new Promise<void>(r => setTimeout(r, 0));
    expect(listChars).not.toHaveBeenCalled();
  });

  it('VersionCard: projectId = personal рендерится без броска', () => {
    putCharacter('personal', marina);
    const t = thread([launch()]);
    const html = renderToStaticMarkup(createElement(VersionCard, {
      projectId: 'personal', sessionId: 's1', thread: t, version: t.versions[0], focused: false,
    }));
    expect(html).toContain('hero.png');
  });
});
