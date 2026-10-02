// Операция панели «Звук» одним списком с группами (вариант А, docs/mockups/audio-panel-v3-proposal.md,
// «Рекомендация»): «Новый звук», «С выбранным звуком» (серая, пока звук с файлом не выбран),
// «Без ИИ · бесплатно» и «Из нескольких звуков» для склейки. Чистые функции — под тестом opGroups.test.ts.

import type { SelectOption } from 'aihome_shell/kit';
import type { AudioMode, AudioOp } from '../api';
import { isNoAi, opInfo } from '../ops';
import { needsSource, panelOps, pillOf } from './model';

export const OP_GROUP = {
  create: 'Новый звук',
  source: 'С выбранным звуком',
  noAi: 'Без ИИ · бесплатно',
  many: 'Из нескольких звуков',
} as const;

const NO_SOURCE_TAIL = ' · выберите звук в ленте';
const ON_SOURCE_TAIL = ' · над выбранным звуком';

// hasSource — в работе нить с файлом (есть текущая версия); current — выбранная операция
export function opOptions(mode: AudioMode, hasSource: boolean, current: AudioOp): SelectOption<AudioOp>[] {
  const picked = pillOf(current);
  return panelOps(mode).map(o => {
    const src = needsSource(o.op);
    const tail = src && hasSource && o.op === picked ? ON_SOURCE_TAIL : '';
    let group: SelectOption<AudioOp>['group'];
    if (o.op === 'concat') group = OP_GROUP.many;
    else if (isNoAi(o.op)) group = OP_GROUP.noAi;
    else if (src) group = hasSource ? OP_GROUP.source : { label: OP_GROUP.source + NO_SOURCE_TAIL, disabled: true };
    else group = OP_GROUP.create;
    // Правка без ИИ над звуком без выбранного звука тоже серая — поодиночке, группа у неё своя
    return { value: o.op, label: o.label + tail, group, ...(src && !hasSource ? { disabled: true } : null) };
  });
}

// Подсказка под полями: куда писать текст или стиль; у операций без текста — ничего
export function composerHintOf(op: AudioOp): string | null {
  const info = opInfo(op);
  if (info?.mode === 'voice' && info.field === 'text') return 'Текст для озвучки';
  if (info?.mode === 'music' && info.field === 'prompt') return 'Стиль музыки';
  return null;
}
