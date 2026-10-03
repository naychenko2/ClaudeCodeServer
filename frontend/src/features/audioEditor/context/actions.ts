// Каталог действий звука (ADR-023 §Д2.2, Р6): чистая функция состояния нити → чипы поля ввода.
// Четыре состояния: черновик, речь, песня и версия со стемами. Порядок действий и есть умолчание
// (Р1: первое `run` без `disabledReason`; у песни без выделения это «Стемы»). Остальные операции
// каталога OPS — только агенту; «Обучить голос» — кнопка библиотеки «Голоса», а не чип.

import type { ContextAction } from 'aihome_shell/kit';
import type { AudioOp, AudioStemSet } from '../api';
import { STEM_SETS } from '../panel/stems';

export type AudioState = 'draft' | 'speech' | 'song' | 'stems';

// Роли референсов основного объекта «audio» (AudioContextRoles на бэкенде)
export const ROLE_VOICE = 'voice';
export const ROLE_REFERENCE = 'reference';
export const ROLE_PIECE = 'piece';

export interface AudioRefLike { kind: string; role: string | null }

export interface AudioActionInput {
  state: AudioState;
  refs: readonly AudioRefLike[];
  // Выделен кусок на волне текущей версии (поле «Кусок»)
  hasSelection: boolean;
  // Какие наборы стемов умеет хоть один поставщик каталога; null — каталог ещё не пришёл
  stemSets: readonly AudioStemSet[] | null;
}

// Состояние по версии основного объекта: стемы — у версии есть роли `stem:*`; речь — `mode = voice`;
// всё остальное, включая версию без `mode` (загруженный файл), считается песней
export function audioState(v: { hasSound: boolean; hasStems: boolean; mode: string | null | undefined }): AudioState {
  if (v.hasStems) return 'stems';
  if (!v.hasSound) return 'draft';
  return v.mode === 'voice' ? 'speech' : 'song';
}

// Операция «Озвучить» по референсам: голос из библиотеки — `speak` с голосом, образец — `cloneVoice`,
// иначе `speak` диктором по умолчанию. Голос сильнее образца: `speak` образец не читает
export function resolveSpeakOp(refs: readonly AudioRefLike[]): AudioOp {
  if (refs.some(r => r.role === ROLE_VOICE)) return 'speak';
  if (refs.some(r => r.role === ROLE_REFERENCE)) return 'cloneVoice';
  return 'speak';
}

export const pieceCount = (refs: readonly AudioRefLike[]) => refs.filter(r => r.role === ROLE_PIECE).length;

const noStems = 'Ни один поставщик не умеет разделять на стемы';

const speak = (id: string, label: string, op: AudioOp): ContextAction => ({
  id, kind: 'run', label, op, text: 'required',
  hint: op === 'cloneVoice' ? 'Озвучить текст голосом из образца' : 'Озвучить текст: голос — из контекста или диктор по умолчанию',
  placeholder: 'Текст для озвучки…',
});

export function buildAudioActions(i: AudioActionInput): readonly ContextAction[] {
  const speakOp = resolveSpeakOp(i.refs);
  const stems = (): ContextAction => {
    const sets = STEM_SETS.filter(s => !i.stemSets || i.stemSets.includes(s.value));
    return {
      id: 'stems', kind: 'run', label: 'Стемы', op: 'separate', text: 'none',
      hint: 'Разделить на дорожки: текст не нужен',
      question: { param: 'stemSet', title: 'Набор', options: sets.map(s => ({ value: s.value, label: s.label })) },
      ...(sets.length ? {} : { disabledReason: noStems }),
    };
  };
  const concat = (): ContextAction => ({
    id: 'concat', kind: 'run', label: 'Склеить', op: 'concat', text: 'none',
    hint: 'Склеить куски из контекста в новый звук без ИИ',
    ...(pieceCount(i.refs) >= 2 ? {} : {
      disabledReason: pieceCount(i.refs) === 0 ? 'Добавьте куски через «В контекст»' : 'Нужен ещё один кусок: добавьте его через «В контекст»',
    }),
  });

  switch (i.state) {
    case 'draft':
      return [
        speak('speak', 'Озвучить', speakOp),
        {
          id: 'song', kind: 'run', label: 'Песня', op: 'song', text: 'required',
          hint: 'Сочинить песню по описанию стиля', placeholder: 'Стиль: жанр, настроение, инструменты, голос…',
        },
        {
          id: 'sfx', kind: 'run', label: 'Эффект', op: 'sfx', text: 'required',
          hint: 'Сгенерировать звуковой эффект по описанию', placeholder: 'Опишите звук: «скрип двери в пустом подъезде»',
        },
      ];
    case 'speech':
      return [
        speak('speakMore', 'Озвучить ещё', speakOp),
        {
          id: 'convert', kind: 'run', label: 'Сменить голос', op: 'convertVoice', text: 'none',
          hint: 'Заменить голос в записи: слова и интонация сохранятся',
          ...(i.refs.some(r => r.role === ROLE_VOICE || r.role === ROLE_REFERENCE)
            ? {} : { disabledReason: 'Добавьте голос или образец через «В контекст»' }),
        },
        { id: 'denoise', kind: 'run', label: 'Убрать шум', op: 'denoise', text: 'none', hint: 'Очистить речь от шума: текст не нужен' },
        stems(),
      ];
    case 'song':
      return [
        {
          id: 'repaint', kind: 'run', label: 'Перегенерировать кусок', op: 'repaint', text: 'required',
          hint: 'Перегенерировать выделенный кусок', placeholder: 'Что должно звучать в выделенном куске…',
          ...(i.hasSelection ? {} : { disabledReason: 'Выделите кусок на волне в редакторе' }),
        },
        stems(),
        { id: 'denoise', kind: 'run', label: 'Убрать шум', op: 'denoise', text: 'none', hint: 'Очистить звук от шума: текст не нужен' },
        concat(),
      ];
    case 'stems':
      return [
        {
          id: 'mix', kind: 'run', label: 'Свести', op: 'mixStems', text: 'none',
          hint: 'Свести стемы в одну дорожку без ИИ',
        },
        concat(),
      ];
  }
}
