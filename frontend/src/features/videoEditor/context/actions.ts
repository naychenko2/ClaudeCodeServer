// Каталог действий «Видео» (ADR-023 §Д2.2, Р1): чистые функции состояния сцены и фильма → чипы поля ввода.
// Сцена: «Снять» («Переснять», когда клип уже есть), «Кадр A ▾», «Кадр B ▾» (меню источника кадра, выбор не
// меняют). Фильм: «Собрать» («Пересобрать» при готовом файле) и «Монтаж» (редактор, выбором не бывает).
// Порядок и есть умолчание: первое `run` без серости. Текст сцены, «Развернуть», сценарий и музыка живут
// в редакторах «Сцена» и «Монтаж», а не в чипах.

import type { ContextAction, ContextMenuItem } from 'aihome_shell/kit';
import type { FrameSlot } from '../store/frameRefs';

// Операции действий вида: их принимает `launch` (run.ts)
export const VIDEO_OPS = ['shoot', 'build'] as const;
export type VideoOp = typeof VIDEO_OPS[number];

export interface SceneActionInput {
  // У сцены есть клип: «Снять» становится «Переснять»
  shot: boolean;
  hasFrameA: boolean;
  hasFrameB: boolean;
  // Текст сцены пуст: описание съёмки обязано прийти из поля ввода
  sceneTextEmpty: boolean;
  // Пункты меню кадра: функция, потому что хост зовёт их по клику, а не на каждый рендер
  frameMenu: (slot: FrameSlot) => () => readonly ContextMenuItem[];
  // Адрес миниатюры кадра слота; нет кадра — null (пустой квадрат на чипе)
  frameThumb: (slot: FrameSlot) => string | null;
}

export interface FilmActionInput {
  // Фильм собирался: «Собрать» становится «Пересобрать»
  built: boolean;
  // Известно, что в фильме нет ни одной сцены; пока фильм не прочитан — false
  empty: boolean;
  openMontage: () => void;
}

const frameReason = (a: boolean, b: boolean): string | undefined => {
  if (a && b) return undefined;
  if (!a && !b) return 'Нужны оба кадра: выберите их через «Кадр A ▾» и «Кадр B ▾»';
  return a ? 'Нужен кадр B: выберите его через «Кадр B ▾»' : 'Нужен кадр A: выберите его через «Кадр A ▾»';
};

export function buildSceneActions(i: SceneActionInput): readonly ContextAction[] {
  const frame = (slot: FrameSlot): ContextAction => ({
    id: slot === 'A' ? 'frameA' : 'frameB', kind: 'menu', label: `Кадр ${slot}`,
    hint: `Кадр ${slot}: откуда взять`, items: i.frameMenu(slot), thumb: i.frameThumb(slot),
  });
  const reason = frameReason(i.hasFrameA, i.hasFrameB);
  return [
    {
      id: 'shoot', kind: 'run', op: 'shoot', verb: 'Снимаем', label: i.shot ? 'Переснять' : 'Снять',
      text: i.sceneTextEmpty ? 'required' : 'optional',
      hint: 'Снять клип от кадра A к кадру B: текст поля добавится к тексту сцены',
      placeholder: i.sceneTextEmpty
        ? 'Что происходит от кадра A к кадру B…'
        : i.shot ? 'Что изменить в сцене… (не обязательно)' : 'Добавить к тексту сцены… (не обязательно)',
      ...(reason ? { disabledReason: reason } : {}),
    },
    frame('A'),
    frame('B'),
  ];
}

export function buildFilmActions(i: FilmActionInput): readonly ContextAction[] {
  return [
    {
      id: 'build', kind: 'run', op: 'build', verb: 'Собираем', label: i.built ? 'Пересобрать' : 'Собрать', text: 'none',
      hint: 'Собрать фильм в один файл без ИИ: текст не нужен',
      ...(i.empty ? { disabledReason: 'Добавьте в фильм хотя бы одну сцену' } : {}),
    },
    {
      id: 'montage', kind: 'editor', label: 'Монтаж',
      hint: 'Открыть монтаж фильма: порядок сцен, склейки, подрезка, музыка', open: i.openMontage,
    },
  ];
}
