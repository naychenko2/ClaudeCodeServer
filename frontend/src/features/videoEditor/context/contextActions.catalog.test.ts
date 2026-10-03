import { describe, expect, it } from 'vitest';
import { actionChips, chipSelectable, CHAT_CHIP_ID, MAX_VIEW_ACTIONS } from '../../../lib/chatContext/actionChips';
import { pickDefaultAction } from '../../../lib/chatContext/actionMemory';
import {
  buildFilmActions, buildSceneActions, VIDEO_OPS, type FilmActionInput, type SceneActionInput,
} from './actions';

// Каталог действий «Видео» (ADR-023 §Д2.2, Р1): сцена не снята / снята и фильм

const noMenu = () => [];
const scene: SceneActionInput = { shot: false, hasFrameA: true, hasFrameB: true, sceneTextEmpty: false, frameMenu: () => noMenu };
const film: FilmActionInput = { built: false, empty: false, openMontage: () => {} };

type Fixture = { kind: 'scene'; input: SceneActionInput } | { kind: 'film'; input: FilmActionInput };
const FIXTURES: Record<string, Fixture> = {
  'сцена не снята': { kind: 'scene', input: scene },
  'сцена не снята, кадров нет': { kind: 'scene', input: { ...scene, hasFrameA: false, hasFrameB: false } },
  'сцена снята': { kind: 'scene', input: { ...scene, shot: true } },
  'сцена без текста': { kind: 'scene', input: { ...scene, sceneTextEmpty: true } },
  'фильм не собран': { kind: 'film', input: film },
  'фильм собран': { kind: 'film', input: { ...film, built: true } },
  'фильм пуст': { kind: 'film', input: { ...film, empty: true } },
};
const actionsOf = (f: Fixture) => (f.kind === 'scene' ? buildSceneActions(f.input) : buildFilmActions(f.input));

describe.each(Object.entries(FIXTURES))('каталог видео · %s', (_name, fixture) => {
  const actions = actionsOf(fixture);

  it('вид отдаёт не больше пяти действий: с «Чатом» хоста чипов не больше шести, «Чат» первым', () => {
    expect(actions.length).toBeLessThanOrEqual(MAX_VIEW_ACTIONS);
    const chips = actionChips(actions);
    expect(chips.length).toBeLessThanOrEqual(6);
    expect(chips[0].id).toBe(CHAT_CHIP_ID);
  });

  it('id уникальны', () => {
    expect(new Set(actions.map(a => a.id)).size).toBe(actions.length);
  });

  it('op каждого run-действия — операция вида', () => {
    for (const a of actions.filter(x => x.kind === 'run')) expect(VIDEO_OPS).toContain(a.op);
  });

  it('у объекта человека выбран первое run без серости, у агента — «Чат»', () => {
    const picked = pickDefaultAction(actions, 'human');
    const firstLive = actions.find(a => a.kind === 'run' && !a.disabledReason);
    expect(picked).toBe(firstLive?.id ?? null);
    if (picked) expect(actionChips(actions).filter(chipSelectable).map(c => c.id)).toContain(picked);
    expect(pickDefaultAction(actions, 'agent')).toBeNull();
  });
});

describe('каталог видео: состав по состояниям', () => {
  it('сцена не снята: «Снять», «Кадр A», «Кадр B»; умолчание «Снять»', () => {
    const a = buildSceneActions(scene);
    expect(a.map(x => x.id)).toEqual(['shoot', 'frameA', 'frameB']);
    expect(a.map(x => x.kind)).toEqual(['run', 'menu', 'menu']);
    expect(a[0]).toMatchObject({ label: 'Снять', op: 'shoot', text: 'optional' });
    expect(pickDefaultAction(a, 'human')).toBe('shoot');
  });

  it('сцена снята: «Снять» становится «Переснять», остальное то же', () => {
    const a = buildSceneActions({ ...scene, shot: true });
    expect(a.map(x => x.id)).toEqual(['shoot', 'frameA', 'frameB']);
    expect(a[0].label).toBe('Переснять');
    expect(pickDefaultAction(a, 'human')).toBe('shoot');
  });

  it('«Снять» серая без кадров, с причиной под недостающий кадр; с обоими — живая', () => {
    const shoot = (a: boolean, b: boolean) => buildSceneActions({ ...scene, hasFrameA: a, hasFrameB: b })[0];
    expect(shoot(false, false).disabledReason).toMatch(/Нужны оба кадра/);
    expect(shoot(false, true).disabledReason).toMatch(/кадр A/);
    expect(shoot(true, false).disabledReason).toMatch(/кадр B/);
    expect(shoot(true, true).disabledReason).toBeUndefined();
  });

  it('серая «Снять» отдаёт умолчание «Чату»: кадры ещё выбирают', () => {
    expect(pickDefaultAction(buildSceneActions({ ...scene, hasFrameA: false }), 'human')).toBeNull();
  });

  it('текст поля у «Снять»: просьба поверх текста сцены не обязательна; у сцены без текста — обязательна', () => {
    expect(buildSceneActions(scene)[0].text).toBe('optional');
    expect(buildSceneActions({ ...scene, sceneTextEmpty: true })[0].text).toBe('required');
  });

  it('чипы кадров — меню без выбора; пункты отдаёт фабрика слота', () => {
    const calls: string[] = [];
    const a = buildSceneActions({ ...scene, frameMenu: slot => () => { calls.push(slot); return []; } });
    a.filter(x => x.kind === 'menu').forEach(x => x.items?.());
    expect(calls).toEqual(['A', 'B']);
    expect(actionChips(a).filter(chipSelectable).map(c => c.id)).toEqual([CHAT_CHIP_ID, 'shoot']);
  });

  it('фильм: «Собрать» и «Монтаж» (editor, выбором не бывает); умолчание «Собрать»', () => {
    const a = buildFilmActions(film);
    expect(a.map(x => x.id)).toEqual(['build', 'montage']);
    expect(a.map(x => x.kind)).toEqual(['run', 'editor']);
    expect(a[0]).toMatchObject({ label: 'Собрать', op: 'build', text: 'none' });
    expect(pickDefaultAction(a, 'human')).toBe('build');
    expect(actionChips(a).filter(chipSelectable).map(c => c.id)).toEqual([CHAT_CHIP_ID, 'build']);
  });

  it('фильм собран — «Пересобрать»; пустой фильм — «Собрать» серая с причиной', () => {
    expect(buildFilmActions({ ...film, built: true })[0].label).toBe('Пересобрать');
    const empty = buildFilmActions({ ...film, empty: true });
    expect(empty[0].disabledReason).toMatch(/хотя бы одну сцену/);
    expect(pickDefaultAction(empty, 'human')).toBeNull();
  });

  it('«Монтаж» открывает редактор вызовом open', () => {
    let opened = 0;
    buildFilmActions({ ...film, openMontage: () => { opened++; } })[1].open?.();
    expect(opened).toBe(1);
  });

  it('вне чипов остаются текст сцены, «Развернуть», сценарий, музыка и «Сохранить сцену»: они в редакторах', () => {
    const labels = Object.values(FIXTURES).flatMap(f => actionsOf(f).map(a => a.label));
    for (const gone of ['Развернуть', 'Сценарий', 'Музыка', 'Сохранить']) expect(labels.join('|')).not.toContain(gone);
  });
});
