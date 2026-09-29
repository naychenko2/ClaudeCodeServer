import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { OriginAnchor } from './VersionCards';
import type { ImageThread, ImageThreadLaunch, ImageThreadVersion } from './threadsApi';

// Якорь «origin» нити: черновик «Новая картинка» виден только до первого запуска —
// дальше результат рисует якорь запуска, а пустая карточка лишь дублирует его

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
  it('черновик без запусков рисуется', () => {
    const html = render(draft());
    expect(html).toContain('data-image-draft');
    expect(html).toContain('Новая картинка');
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
