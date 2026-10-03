// Пункты «Файлов» для контекста хода (ADR-023, 2к-2): «Работать с этой» (слот context-opener) и «В контекст»
// с ролью от вида основного объекта. Окружение node — пункты рисуем в строку.
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.mock('../../lib/offline', () => ({ request: vi.fn() }));
vi.mock('../../lib/signalr', () => ({ onMessage: () => () => {}, onReconnected: () => () => {} }));

const { fileContextItems } = await import('./useFileContextMenu');
const { registerSubsystem } = await import('../../lib/subsystems/registryCore');
type State = import('../../lib/chatContext/types').ChatContextDto;

const S = 's1';
const base = { addedAt: '', version: null, thumb: null, missing: false, by: 'human' as const };
const primary = { ...base, id: 'p1', kind: 'image', ref: { threadId: 't1' }, label: 'hero.png', role: null };
const inCtx = { ...base, id: 'r1', kind: 'project-file', ref: { path: 'a.png' }, label: 'a.png', role: 'object', usedBy: [] as string[] };

const install = (roles: { role: string; label: string }[]) => registerSubsystem({
  key: 'file-menu-test', title: 't', order: 1, noPill: true, core: true,
  slots: { 'context-kind': [{ name: 'image', action: { kinds: ['image'], refRoles: () => roles } as never }] },
});

const state = (refs: (typeof inCtx)[] = [], p: typeof primary | null = primary): State => ({ revision: 1, primary: p, refs });
const opener = { isOpenable: (p: string) => p.endsWith('.png'), toRef: async () => null };
const html = (path: string, st: State = state(), withOpener = true) => renderToStaticMarkup(createElement('div', null,
  ...fileContextItems({ projectId: 'p', sessionId: S, path, state: st, opener: withOpener ? opener : undefined, close: () => {} })));

beforeEach(() => install([{ role: 'style', label: 'Как образец стиля' }, { role: 'object', label: 'Как объект' }]));

describe('пункты «Файлов» для контекста хода', () => {
  it('картинка: «Работать с этой» и «В контекст» по роли на каждую (несколько ролей — выбор в самом меню)', () => {
    const h = html('a.png');
    expect(h).toContain('Работать с этой');
    expect(h).toContain('В контекст · как образец стиля');
    expect(h).toContain('В контекст · как объект');
  });

  it('не картинка: только «В контекст»; одна роль — без уточнения', () => {
    install([{ role: 'object', label: 'Как объект' }]);
    const h = html('notes.txt');
    expect(h).not.toContain('Работать с этой');
    expect(h).toContain('>В контекст<');
    expect(h).not.toContain('как объект');
  });

  it('звуковой файл при картинке в работе: образцы стиля не предлагаются, кнопка серая с причиной', () => {
    const h = html('speech.wav');
    expect(h).toContain('такой референс не берёт');
    expect(h).not.toContain('образец стиля');
  });

  it('картинка при звуке в работе — тоже серая; звуковой файл звук берёт ролями звука', () => {
    const audioPrimary = { ...primary, kind: 'audio', label: 'song.mp3' };
    registerSubsystem({
      key: 'file-menu-test-audio', title: 'a', order: 2, noPill: true, core: true,
      slots: { 'context-kind': [{ name: 'audio', action: { kinds: ['audio'], refRoles: () => [{ role: 'piece', label: 'Как кусок склейки' }] } as never }] },
    });
    expect(html('a.png', state([], audioPrimary))).toContain('такой референс не берёт');
    expect(html('speech.wav', state([], audioPrimary))).toContain('>В контекст<');
  });

  it('нет вкладчика context-opener — «Работать с этой» нет даже у картинки', () => {
    expect(html('a.png', state(), false)).not.toContain('Работать с этой');
  });

  it('без основного объекта «В контекст» серый с причиной', () => {
    const h = html('a.png', state([], null));
    expect(h).toContain('Сначала выберите, с чем работать');
    expect(h).toContain('disabled');
  });

  it('основной объект такой референс не берёт — серый с другой причиной', () => {
    install([]);
    expect(html('a.png')).toContain('такой референс не берёт');
  });

  it('файл уже в контексте: пункт «В контексте · роль» со снятием', () => {
    const h = html('a.png', state([inCtx]));
    expect(h).toContain('В контексте · объект');
    expect(h).not.toContain('В контекст ·');
  });
});
