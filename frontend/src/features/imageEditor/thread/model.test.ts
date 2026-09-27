import { describe, expect, it } from 'vitest';
import { chainOf, currentIndex, currentStack, focusLabel, interruptedOf, launchedPrompt, saveFolder, versionLabel } from './model';
import type { ImageThread } from './threadsApi';

const thread = (patch: Partial<ImageThread>): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-09-27T00:00:00Z', ...patch,
});

describe('нить картинки', () => {
  it('у файла первая позиция — исходник, у черновика исходника нет', () => {
    const t = thread({ stacks: [{ stackId: 's1', steps: ['a', 'b'], forkedFromStepId: null, old: false }], currentStackId: 's1', currentStepId: 'b' });
    expect(chainOf(t, currentStack(t)).map(p => p.stepId)).toEqual([null, 'a', 'b']);
    const d = thread({ file: null, draftFolder: 'images/blog', stacks: [{ stackId: 's1', steps: ['a'], forkedFromStepId: null, old: false }], currentStackId: 's1' });
    expect(chainOf(d, currentStack(d)).map(p => p.stepId)).toEqual(['a']);
  });

  it('стопка после отката рисуется как есть: шаги в ней уже с начала', () => {
    const t = thread({
      stacks: [
        { stackId: 's1', steps: ['a', 'b', 'c'], forkedFromStepId: null, old: true },
        { stackId: 's2', steps: ['a', 'd'], forkedFromStepId: 'a', old: false },
      ],
      currentStackId: 's2', currentStepId: 'd',
    });
    expect(chainOf(t, currentStack(t)).map(p => p.stepId)).toEqual([null, 'a', 'd']);
    expect(chainOf(t, t.stacks[0]).map(p => p.stepId)).toEqual([null, 'a', 'b', 'c']);
  });

  it('текущий шаг после отката — не последний; у старой стопки — последний', () => {
    const t = thread({
      stacks: [
        { stackId: 's1', steps: ['a', 'b', 'c'], forkedFromStepId: null, old: true },
        { stackId: 's2', steps: ['a', 'b'], forkedFromStepId: 'b', old: false },
      ],
      currentStackId: 's2', currentStepId: 'a',
    });
    const cur = currentStack(t);
    expect(currentIndex(t, chainOf(t, cur), cur)).toBe(1);
    expect(currentIndex(t, chainOf(t, t.stacks[0]), t.stacks[0])).toBe(3);
  });

  it('версия: исходник и сохранённый шаг — «в проекте», новый шаг — «черновик»', () => {
    const t = thread({});
    expect(versionLabel(t, { stepId: null }, null)).toBe('в проекте');
    expect(versionLabel(t, { stepId: 'a' }, null)).toBe('черновик');
    expect(versionLabel(t, { stepId: 'a' }, 'a')).toBe('в проекте');
    expect(versionLabel(thread({ file: null }), { stepId: 'a' }, 'a')).toBe('черновик');
  });

  it('чип выбора и папка сохранения', () => {
    expect(focusLabel(thread({ file: null, draftFolder: 'images/blog' }))).toBe('Новая картинка · сохранять в images/blog/');
    expect(focusLabel(thread({ file: null, draftFolder: '' }))).toBe('Новая картинка · сохранять в корень проекта');
    const t = thread({ stacks: [{ stackId: 's1', steps: ['a'], forkedFromStepId: null, old: false }], currentStackId: 's1', currentStepId: 'a' });
    expect(focusLabel(t)).toBe('hero.png · шаг 2');
    expect(saveFolder(t)).toBe('images');
    expect(saveFolder(thread({ file: null, draftFolder: 'x' }))).toBe('x');
  });
});

describe('задача, оборванная перезапуском сервера', () => {
  const launched = (jobId: string, prompt: string) => ({
    at: '2026-09-27T10:00:00Z', kind: 'launched', threadId: 't1', jobId,
    text: `Человек запустил вручную: «${prompt}» · FLUX dev · 2 варианта · ≈ $0.06`,
  });
  const interrupted = { at: '2026-09-27T10:05:00Z', kind: 'interrupted', threadId: 't1', jobId: 'j1',
    text: 'Генерация прервана перезапуском сервера: картинка hero.png, вариантов не будет — запусти заново' };

  it('пометка без идущей задачи — «прервана», промпт берётся из записи о запуске', () => {
    const t = thread({ interruptedJobId: 'j1' });
    expect(interruptedOf(t, [launched('j0', 'старое'), launched('j1', 'кот в «шляпе»'), interrupted]))
      .toEqual({ jobId: 'j1', prompt: 'кот в «шляпе»' });
  });

  it('запись о запуске ушла из журнала — пометка есть, промпта нет', () => {
    expect(interruptedOf(thread({ interruptedJobId: 'j1' }), [interrupted])).toEqual({ jobId: 'j1', prompt: null });
  });

  it('идущая задача важнее пометки; без пометки блока нет', () => {
    expect(interruptedOf(thread({ interruptedJobId: 'j1', pendingJobId: 'j2' }), [launched('j1', 'кот')])).toBeNull();
    expect(interruptedOf(thread({ interruptedJobId: null }), [launched('j1', 'кот')])).toBeNull();
    expect(interruptedOf(thread({}))).toBeNull();
  });

  it('промпт из текста записи: агентский запуск и пустой промпт', () => {
    expect(launchedPrompt('Ты запустил: «убрать фон» · модель по котировке')).toBe('убрать фон');
    expect(launchedPrompt('Ты запустил: «  » · FLUX')).toBeNull();
    expect(launchedPrompt('что-то другое')).toBeNull();
  });
});
