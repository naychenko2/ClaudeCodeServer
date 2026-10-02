import { describe, expect, it } from 'vitest';
import { IMAGE_PICK_FOOTER, imagePickMenu } from './pickMenu';
import type { ImageThread, ImageThreadLaunch } from './threadsApi';

// Состав меню «Что править?» (макет image-panel-v5, вариант 1)
const thread = (patch: Partial<ImageThread>): ImageThread => ({
  id: 't', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z', ...patch,
});
const launch = (at: string, initiator: 'human' | 'agent' = 'human'): ImageThreadLaunch =>
  ({ jobId: `j-${at}`, baseVersionId: null, baseStepId: null, at, status: 'done', initiator, prompt: 'x' });
const ago = (iso: string) => `когда ${iso.slice(11, 16)}`;

const HERO = thread({ id: 'hero', file: 'images/hero.png', launches: [launch('2026-10-01T10:00:00Z')] });
const CAT = thread({ id: 'cat', file: null, draftFolder: '', currentStepId: 'st1', launches: [launch('2026-10-01T11:00:00Z', 'agent')] });
const EMPTY = thread({ id: 'draft', file: null, draftFolder: '', createdAt: '2026-10-01T12:00:00Z' });

describe('меню «Что править?»', () => {
  it('картинки чата свежими сверху, черновик без картинки и выбранная — не в списке', () => {
    const m = imagePickMenu([HERO, CAT, EMPTY], { ago, focusId: null, lastEditedId: null, personal: false });
    expect(m.rows.map(r => r.id)).toEqual(['cat', 'hero']);
    expect(m.rows[0].sub).toBe('агент · когда 11:00');
    expect(m.rows[1].sub).toBe('вы · когда 10:00');
    const focused = imagePickMenu([HERO, CAT], { ago, focusId: 'cat', lastEditedId: null, personal: false });
    expect(focused.rows.map(r => r.id)).toEqual(['hero']);
  });

  it('последняя правленая отмечена', () => {
    const m = imagePickMenu([HERO, CAT], { ago, focusId: null, lastEditedId: 'hero', personal: false });
    expect(m.rows.map(r => [r.id, r.last])).toEqual([['cat', false], ['hero', true]]);
  });

  it('в проекте — «С компьютера…» и «Из файлов проекта…», в личном чате их нет; подвал есть всегда', () => {
    expect(imagePickMenu([HERO], { ago, focusId: null, lastEditedId: null, personal: false }).extras).toEqual(['upload', 'project']);
    const personal = imagePickMenu([HERO], { ago, focusId: null, lastEditedId: null, personal: true });
    expect(personal.extras).toEqual([]);
    expect(personal.footer).toBe(IMAGE_PICK_FOOTER);
  });

  it('пустой чат — строк нет, загрузка остаётся', () => {
    const m = imagePickMenu([EMPTY], { ago, focusId: null, lastEditedId: null, personal: false });
    expect(m.rows).toEqual([]);
    expect(m.extras).toEqual(['upload', 'project']);
  });
});
