// Каталог действий картинки (ADR-023 §Д2.2): чистая функция состояния нити → чипы поля ввода.
// Три состояния: черновик (у нити нет картинки), с файлом и с отметками редактора. Порядок действий
// и есть умолчание (Р1: первое `run` без `disabledReason`). `enhanceFaces` и режим подбора
// (EditMode) — не чипы: ими пользуется агент.

import type { ContextAction } from 'aihome_shell/kit';
import type { ImageEditOp } from '../api';
import { pickOp } from '../format';

export type ImageState = 'draft' | 'file' | 'marks';

// Пропорции, которые принимает сервер у дорисовки и новой картинки (ImageEditLaunchAssembler.AspectRatios)
export const ASPECT_OPTIONS = ['16:9', '9:16', '1:1'] as const;
// Пропорции новой картинки «как решит модель»: значение params.aspect, на сервер уходит как null
export const ASPECT_AUTO = 'авто';

export interface ImageActionInput {
  // У нити есть что править: версия с картинкой, шаг или файл
  hasFile: boolean;
  // Сколько отметок на холсте редактора (кисть, стрелки, рамки, подписи)
  marks: number;
  // Отметки включают закрашенное кистью, и его не отменили выбором «Вся картинка»
  hasMask: boolean;
  // Какие быстрые операции умеет хоть один поставщик каталога; не умеет никто — действие серое
  offered: { removeBackground: boolean; upscale: boolean; outpaint: boolean };
  openBrush: () => void;
}

export const imageState = (i: Pick<ImageActionInput, 'hasFile' | 'marks'>): ImageState =>
  !i.hasFile ? 'draft' : i.marks > 0 ? 'marks' : 'file';

// Операции действий: по ним хост строит «Чем» и цену
export const EDIT_OP = (hasMask: boolean): ImageEditOp => pickOp(true, hasMask);

const notOffered = (what: string) => `Ни один поставщик не умеет ${what}`;

// Глагол хода и значок чипа по id действия (макет composer-actions-v1)
const LOOK: Readonly<Record<string, Pick<ContextAction, 'verb' | 'icon'>>> = {
  draw: { verb: 'Рисуем', icon: 'image' },
  edit: { verb: 'Изменяем', icon: 'spark' },
  removeBg: { verb: 'Убираем фон у', icon: 'scissors' },
  upscale: { verb: 'Увеличиваем', icon: 'maximize' },
  outpaint: { verb: 'Дорисовываем', icon: 'expand' },
  mark: { icon: 'brush' },
};

export const buildImageActions = (i: ImageActionInput): readonly ContextAction[] =>
  rawImageActions(i).map(a => ({ ...a, ...LOOK[a.id] }));

function rawImageActions(i: ImageActionInput): readonly ContextAction[] {
  if (imageState(i) === 'draft') {
    return [{
      id: 'draw', kind: 'run', label: 'Нарисовать', op: 'generate', text: 'required',
      hint: 'Нарисовать новую картинку по описанию',
      placeholder: 'Опишите новую картинку — например, «Аня в кафе у окна»',
    }];
  }
  const marked = imageState(i) === 'marks';
  const off = (ok: boolean, what: string) => (ok ? {} : { disabledReason: notOffered(what) });
  return [
    {
      id: 'edit', kind: 'run', op: EDIT_OP(i.hasMask), text: 'required',
      label: marked ? 'Изменить отмеченное' : 'Изменить',
      hint: marked ? 'Изменить отмеченное место: отметки уйдут вместе с описанием' : 'Изменить картинку по описанию',
      placeholder: marked ? 'Что сделать с отмеченным…' : 'Что изменить на картинке…',
    },
    {
      id: 'removeBg', kind: 'run', label: 'Убрать фон', op: 'removeBackground', text: 'none',
      hint: 'Убрать фон: текст не нужен', ...off(i.offered.removeBackground, 'убирать фон'),
    },
    {
      id: 'upscale', kind: 'run', label: 'Увеличить', op: 'upscale', text: 'none',
      hint: 'Улучшить качество и увеличить: текст не нужен', ...off(i.offered.upscale, 'улучшать качество'),
    },
    {
      id: 'outpaint', kind: 'run', label: 'Дорисовать', op: 'outpaint', text: 'none',
      hint: 'Дорисовать картинку за края до выбранных пропорций',
      question: {
        param: 'aspect', title: 'Пропорции',
        options: ASPECT_OPTIONS.map(r => ({ value: r, label: r })),
      },
      ...off(i.offered.outpaint, 'дорисовывать за края'),
    },
    {
      id: 'mark', kind: 'editor', label: 'Отметить',
      hint: 'Отметить место кистью в редакторе', open: i.openBrush,
    },
  ];
}
