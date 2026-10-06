import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { OriginAnchor, VersionCard } from './VersionCards';
import { isHiddenDraft } from './model';
import type { ImageThread, ImageThreadLaunch, ImageThreadVersion } from './threadsApi';

// Якорь «origin» нити: пустой черновик «Новая картинка» в ленте не рисуется вовсе —
// нить появляется с первой версией (якорь запуска)

const ver = (id: string, patch: Partial<ImageThreadVersion> = {}): ImageThreadVersion => ({
  id, number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null,
  steps: [], currentStepId: null, createdAt: '2026-09-29T00:00:00Z', ...patch,
});

const launch = (status: ImageThreadLaunch['status']): ImageThreadLaunch => ({
  jobId: 'j1', baseVersionId: 'origin', baseStepId: null, at: '2026-09-29T00:00:00Z',
  status, initiator: 'agent', prompt: 'кот',
});

const draft = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: null, lineage: [], draftFolder: '', stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-09-29T00:00:00Z',
  versions: [ver('origin')], currentVersionId: 'origin', launches: [],
  ...patch,
});

const render = (thread: ImageThread) =>
  renderToStaticMarkup(createElement(OriginAnchor, { projectId: 'p1', sessionId: 's1', thread, focused: true }));

describe('OriginAnchor', () => {
  it('пустой черновик без запусков карточкой в ленте не рисуется', () => {
    expect(render(draft())).toBe('');
    expect(render(draft({ draftFolder: 'images' }))).toBe('');
  });

  it('isHiddenDraft: прячет пустой черновик, но не нить с запуском, файлом или версией', () => {
    expect(isHiddenDraft(draft())).toBe(true);
    expect(isHiddenDraft(draft({ pendingJobId: 'j1' }))).toBe(false);
    expect(isHiddenDraft(draft({ file: 'img/a.png' }))).toBe(false);
    expect(isHiddenDraft(draft({ versions: [ver('origin'), ver('v1', { number: 1, steps: ['s1'], currentStepId: 's1' })] }))).toBe(false);
  });

  it('черновик с идущим запуском не рисуется', () => {
    expect(render(draft({ launches: [launch('running')] }))).toBe('');
  });

  it('черновик с упавшим запуском без версий не рисуется', () => {
    expect(render(draft({ launches: [launch('failed')] }))).toBe('');
  });

  it('черновик с версиями от ИИ не рисуется', () => {
    const t = draft({ versions: [ver('origin'), ver('v1', { number: 1, jobId: 'j1', steps: ['s1'], currentStepId: 's1' })] });
    expect(render(t)).toBe('');
  });

  it('нить с файлом-исходником рисует карточку версии, а не черновик', () => {
    const html = render(draft({ file: 'img/hero.png', draftFolder: null, launches: [launch('done')] }));
    expect(html).toContain('data-image-version');
    expect(html).not.toContain('data-image-draft');
  });
});

// Каждая карточка версии — на всю ленту: варианты запуска идут друг под другом, не в ряд
describe('VersionCard: ширина', () => {
  it('занимает всю ширину ленты', () => {
    const t = draft({ file: 'img/hero.png', draftFolder: null });
    const html = renderToStaticMarkup(createElement(VersionCard, {
      projectId: 'p1', sessionId: 's1', thread: t, version: t.versions[0], focused: true,
    }));
    expect(html).toContain('width:100%');
    expect(html).not.toContain('calc(');
  });
});
