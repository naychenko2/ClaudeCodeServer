import { describe, expect, it } from 'vitest';
import { chainOf, currentIndex, currentStack, focusLabel, interruptedOf, launchEndNote, lastLaunchPrompt, launchedPrompt, saveFolder, versionLabel } from './model';
import type { ImageThread, ImageThreadLaunch } from './threadsApi';

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

import {
  currentVersion, isLegacyThread, versionHasImage, versionStep, versionsOf, launchVersions, versionMeta,
} from './model';

const v = (id: string, patch: Partial<Parameters<typeof makeV>[0]> = {}): ImageThreadVersion => makeV(id, patch);

function makeV(id: string, patch: Partial<{ jobId: string | null; variant: number | null; baseVersionId: string | null; baseStepId: string | null; steps: string[]; currentStepId: string | null; number: number; createdAt: string }>): ImageThreadVersion {
  return {
    id, jobId: null, variant: null, baseVersionId: null, baseStepId: null,
    steps: [], currentStepId: null, number: 0, createdAt: '2026-09-27T00:00:00Z', ...patch,
  };
}

import type { ImageThreadVersion } from './threadsApi';

describe('версии картинки (изменение 27.09 к ADR-019)', () => {
  it('у нити без versions — пустой список, но не падает', () => {
    expect(versionsOf(thread({}))).toEqual([]);
    expect(currentVersion(thread({}))).toBeNull();
  });

  it('isLegacyThread срабатывает только по старым признакам: стопки с шагами или pendingJobId', () => {
    expect(isLegacyThread(thread({ stacks: [{ stackId: 's', steps: ['a'], forkedFromStepId: null, old: false }] }))).toBe(true);
    expect(isLegacyThread(thread({ pendingJobId: 'j1' }))).toBe(true);
    expect(isLegacyThread(thread({
      versions: [v('origin'), v('v1', { jobId: 'j1', variant: 0, number: 1, steps: ['step1'], currentStepId: 'step1' })],
      currentVersionId: 'v1',
    }))).toBe(false);
  });

  it('currentVersion — текущая; версия без неё — первая в списке', () => {
    const t = thread({
      versions: [v('origin'), v('v1', { number: 1 })],
      currentVersionId: 'v1',
    });
    expect(currentVersion(t)?.id).toBe('v1');
    expect(currentVersion(thread({ versions: [v('a'), v('b')] }))?.id).toBe('a');
  });

  it('versionHasImage: исходник — файл нити или baseStepId; версия ИИ — currentStepId', () => {
    const t = thread({
      file: 'img/hero.png', stacks: [], currentStackId: null, currentStepId: null,
      versions: [
        v('origin'),
        v('v1', { jobId: 'j1', variant: 0, number: 1, steps: ['st1'], currentStepId: 'st1' }),
      ],
    });
    expect(versionHasImage(t, t.versions![0])).toBe(true);
    expect(versionHasImage(t, t.versions![1])).toBe(true);
  });

  it('launchVersions выбирает только варианты одного jobId по индексу', () => {
    const t = thread({
      versions: [
        v('origin'),
        v('a', { jobId: 'j', variant: 0, number: 1 }),
        v('b', { jobId: 'j', variant: 1, number: 2 }),
        v('c', { jobId: 'other', variant: 0, number: 3 }),
      ],
    });
    expect(launchVersions(t, 'j').map(x => x.id)).toEqual(['a', 'b']);
  });

  it('versionMeta собирает подпись из индекса в запуске и базовой версии', () => {
    const t = thread({
      versions: [
        v('origin'),
        v('a', { jobId: 'j', variant: 0, number: 1, baseVersionId: 'origin' }),
        v('b', { jobId: 'j', variant: 1, number: 2, baseVersionId: 'origin' }),
      ],
    });
    expect(versionMeta(t, t.versions![1])).toContain('вариант 1 из 2');
    expect(versionMeta(t, t.versions![1])).toContain('от исходника');
    expect(versionMeta(t, t.versions![2])).toContain('вариант 2 из 2');
  });

  it('focusLabel для нити с версиями говорит имя · версия N', () => {
    const t = thread({
      versions: [v('origin'), v('v1', { number: 1 })],
      currentVersionId: 'v1',
    });
    expect(focusLabel(t)).toBe('hero.png · версия 1');
    expect(focusLabel(t, true)).toBe('hero.png · в1');
  });

  it('versionStep у исходника без правок — текущий шаг нити, у версии ИИ — её baseStepId', () => {
    const t = thread({
      file: 'img/hero.png', currentStepId: 'st0',
      versions: [
        v('origin'),
        v('v1', { number: 1, baseStepId: 'st0', currentStepId: 'st1' }),
      ],
    });
    expect(versionStep(t, t.versions![0])).toBe('st0');
    expect(versionStep(t, t.versions![1])).toBe('st1');
  });
});

describe('launchEndNote', () => {
  it('отмена и перезапуск с частью готовых вариантов называют, сколько готово', () => {
    expect(launchEndNote('cancelled', 1, 3)).toBe('Отменено — готово 1 из 3.');
    expect(launchEndNote('interrupted', 2, 3)).toBe('Прервано перезапуском сервера — готово 2 из 3.');
  });

  it('без готовых вариантов — прежние тексты', () => {
    expect(launchEndNote('cancelled', 0, 3)).toBe('Генерация отменена.');
    expect(launchEndNote('interrupted', 0, 3)).toBe('Генерация прервана перезапуском сервера.');
    expect(launchEndNote('failed', 0, 1)).toBe('Сервис рисования отказал — версий нет.');
  });

  it('идущий и доделанный запуск надписи не несут', () => {
    expect(launchEndNote('running', 1, 3)).toBeNull();
    expect(launchEndNote('done', 3, 3)).toBeNull();
  });
});

describe('промпт последнего запуска (затравка режима «Картинка»)', () => {
  const launch = (jobId: string, at: string, prompt: string | null, initiator: 'human' | 'agent' = 'agent') =>
    ({ jobId, baseVersionId: null, baseStepId: null, at, status: 'done', initiator, prompt }) as ImageThreadLaunch;
  const withLaunches = (launches?: ImageThreadLaunch[]) => thread({ launches });

  it('берёт последний по времени, а не по порядку в массиве', () => {
    const t = withLaunches([
      launch('j2', '2026-09-29T10:05:00Z', 'новый'),
      launch('j1', '2026-09-29T10:00:00Z', 'старый'),
    ]);
    expect(lastLaunchPrompt(t)).toBe('новый');
  });

  it('пропускает запуски без промпта (фон, апскейл)', () => {
    const t = withLaunches([
      launch('j1', '2026-09-29T10:00:00Z', 'кот на окне'),
      launch('j2', '2026-09-29T10:05:00Z', null),
      launch('j3', '2026-09-29T10:06:00Z', '   '),
    ]);
    expect(lastLaunchPrompt(t)).toBe('кот на окне');
  });

  it('не зависит от инициатора', () => {
    const t = withLaunches([
      launch('j1', '2026-09-29T10:00:00Z', 'агента', 'agent'),
      launch('j2', '2026-09-29T10:05:00Z', 'человека', 'human'),
    ]);
    expect(lastLaunchPrompt(t)).toBe('человека');
  });

  it('без запусков — null', () => {
    expect(lastLaunchPrompt(withLaunches(undefined))).toBeNull();
    expect(lastLaunchPrompt(withLaunches([]))).toBeNull();
  });
});
