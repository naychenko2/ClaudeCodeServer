// Режим «Командная реализация» (флаг team-implement-mode): подписи стадий и тоны
// бейджа/маркера. Тексты — дословно из docs/features/team-implement-mode.md («Тексты»)
// и макета docs/mockups/team-implement-mode.html (короткие формы маркера).

import type { TeamImplementStage } from '../types';

// Тон по тому, кто должен действовать: work — команда работает (accent),
// wait — практика стоит и ждёт человека (warning), idle — ждёт задачу (muted)
export type TeamImplementTone = 'work' | 'wait' | 'idle';

export function teamImplementTone(stage: TeamImplementStage): TeamImplementTone {
  switch (stage) {
    case 'confirming':
    case 'awaitingDecision':
      return 'wait';
    case 'idle':
      return 'idle';
    default:
      return 'work';
  }
}

// «Волна N из M»: M — потолок волн из бюджета (maxWaves). Планового числа волн на
// проводе пока нет (появится с карточкой плана в Э2), потолок — честная граница.
function waveText(waveNumber: number, maxWaves?: number): string {
  return maxWaves && maxWaves > 0 ? `волна ${waveNumber} из ${maxWaves}` : `волна ${waveNumber}`;
}

// Полная подпись стадии для бейджа в композере (и тултипа маркера)
export function teamImplementStageLabel(stage: TeamImplementStage, waveNumber: number, maxWaves?: number): string {
  switch (stage) {
    case 'planning': return 'планирование';
    case 'confirming': return 'ждёт подтверждения';
    case 'wave': return waveText(waveNumber, maxWaves);
    case 'awaitingDecision': return 'нужно решение';
    case 'checking': return 'проверка';
    case 'idle': return 'ждёт задачу';
  }
}

// Короткая форма для маркера в узкой строке списка чатов
export function teamImplementStageShort(stage: TeamImplementStage, waveNumber: number, maxWaves?: number): string {
  switch (stage) {
    case 'planning': return 'планирует';
    case 'confirming': return 'согласование';
    case 'wave': return maxWaves && maxWaves > 0 ? `волна ${waveNumber}/${maxWaves}` : `волна ${waveNumber}`;
    case 'awaitingDecision': return 'решение';
    case 'checking': return 'проверка';
    case 'idle': return 'ожидает';
  }
}

// Полная строка бейджа: «Командная реализация · <стадия>»
export function teamImplementBadgeText(stage: TeamImplementStage, waveNumber: number, maxWaves?: number): string {
  return `Командная реализация · ${teamImplementStageLabel(stage, waveNumber, maxWaves)}`;
}

// Описание режима — тултип бейджа и текст поповера
export const TEAM_IMPLEMENT_DESCRIPTION =
  'Чат работает как штаб: задачи ставятся исполнителям, их чаты видны под этим в списке. ' +
  'Напишите, что ещё нужно сделать — команда возьмёт в работу';

// Тултип чипа «Авто» (текст тумблера + подпись из плана)
export const TEAM_IMPLEMENT_AUTO_TITLE =
  'Авто-волны — не спрашивать после каждой волны. ' +
  'План согласуете один раз, дальше команда работает сама, пока хватает бюджета';

// Подтверждение выключения режима
export const TEAM_IMPLEMENT_DISABLE_TITLE = 'Выключить командную реализацию?';
export const TEAM_IMPLEMENT_DISABLE_TEXT =
  'Текущие исполнители доработают свои задачи, новые волны не стартуют. ' +
  'Чат станет обычным разговором — включить режим обратно можно в любой момент.';
