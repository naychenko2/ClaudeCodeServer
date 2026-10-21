import { C } from '../../../lib/design';
import { getPersonaById } from '../../../lib/personas';
import type { FeatureRun } from '../../../types/featureRun';

// Чистая функция: из агрегатов фич чата (`FeatureRun[]`, порядок отсортировал бэк —
// runs[0] — главная) строит МОДЕЛЬ пилюли. Компонент (TurnPlanPill) «глупый»: только
// рисует модель. Вся логика 5 состояний макета v1/v2 — здесь.
//
//   run       — идёт: `фича · 8/9 ↺3 · Производство · Денис`; merging → `мерж · идёт`;
//               not_started/idle → `Не начата`
//   wait      — ждёт вас: ромб, `ждёт вас · {reason}` (C.info, 600); merge_pending → `ждёт согласия`
//   stopped   — исполнитель упал: квадрат, `стоит · {имя}` (C.dangerText, 600)
//   closed    — merged → `смержена`, closed (без мержа) → `закрыта`
//   allClosed — все закрыты (N>1): `фичи · N · все закрыты` (без счётчика шагов, без +N)
//
// Приоритет: stopped → wait → closed → run. «Стоит» — ТОЛЬКО для упавшего исполнителя.

export type PillState = 'run' | 'wait' | 'stopped' | 'closed' | 'allClosed';

export interface PillModel {
  state: PillState;
  // 'фича' | 'фичи' (слово-метка режима; компонент скрывает его на мобиле).
  word: string;
  // '8/9' | null (нет счётчика: все закрыты, N>1).
  counter: string | null;
  // Возвраты (↺); 0 — нет.
  loops: number;
  // +N: число остальных фич; 0 — нет.
  more: number;
  // Текст состояния (последняя часть): 'Производство · Денис' / 'ждёт вас · мерж' /
  // 'стоит · Денис' / 'Фича смержена' / '6 · все закрыты'.
  text: string;
  // Значок: 'route' (lucide Route) | 'diamond' (ромб) | 'square' (квадрат).
  icon: 'route' | 'diamond' | 'square';
  // Цвет значка (токен).
  iconColor: string;
  // Цвет текста состояния (токен); null — обычный (textMuted).
  textColor: string | null;
  // Вес текста состояния (wait/stopped → 600, иначе 400).
  textWeight: number;
  // Жаркий ↺ (≥ 3): 700 + C.warningText.
  loopsHot: boolean;
  // aria-дополнение (точка-сигнал, когда пилюля в режиме «План хода»):
  // 'фича ждёт вас' / 'исполнитель остановлен' / null.
  ariaExtra: string | null;
}

// Русское имя стадии конвейера (key бэка → подпись).
const STAGE_NAMES: Record<string, string> = {
  cut: 'Разрез', plan: 'План', prod: 'Производство',
  review: 'Ревью', qa: 'Проверка', final: 'Финальное ревью', merge: 'Мерж',
};
function stageName(key: string | null | undefined): string {
  if (!key) return 'Разрез';
  return STAGE_NAMES[key] ?? 'Разрез';
}

// Имя персоны по id (из стора); null — не найдена.
function personName(id: string | null | undefined): string | null {
  if (!id) return null;
  return getPersonaById(id)?.name ?? null;
}

// Стадия для подписи: now[0].stage (бэк уже отсортировал now по убыванию старта),
// иначе активная стадия из stages, иначе «Разрез».
function stageFor(run: FeatureRun): string {
  const now = run.now;
  if (now && now.length > 0) return stageName(now[0].stage);
  const active = run.stages?.find(s => s.state === 'active');
  if (active) return stageName(active.key);
  return 'Разрез';
}

// Исполнители «сейчас»: уникальные имена из now (параллельная работа реальна —
// «Производство · Кира, Денис»). Никто не идёт (now пуст) → ''.
function namesFor(run: FeatureRun): string {
  const names: string[] = [];
  for (const n of run.now ?? []) {
    const name = personName(n.personaId);
    if (name && !names.includes(name)) names.push(name);
  }
  return names.join(', ');
}

// Счётчик шагов: '8/9'; при нуле шагов — '0/—'.
function counterFor(run: FeatureRun): string {
  const total = run.stepsTotal ?? 0;
  const accepted = run.stepsAccepted ?? 0;
  return total > 0 ? `${accepted}/${total}` : '0/—';
}

// Причина ожидания: reason attention (новое) → awaitingHumanReason (старый сигнал) → 'мерж'.
function waitReason(run: FeatureRun): string {
  return run.attention?.reason || run.awaitingHumanReason || 'мерж';
}

// Имя упавшего исполнителя (из тревоги); '' — не известно.
function stoppedName(run: FeatureRun): string {
  return personName(run.attention?.personaId) ?? '';
}

// Состояние: stopped → wait → closed → run (макет v1/v2, «Стоит» только для упавшего).
// Источник правды — run.state (бэк Б1a/Б1b): merge_pending → wait («ждёт вас»),
// merging → run (нейтрально, «мерж идёт»), waiting → wait, merged/closed → closed.
// Без state (старые данные/кеш/моки) — запасной путь по прежнему признаку
// (attention.kind / awaitingHumanSince), чтобы существующие тесты не упали.
function stateFor(run: FeatureRun): PillState {
  if (run.state) {
    switch (run.state) {
      case 'stopped': return 'stopped';
      case 'waiting':
      case 'merge_pending': return 'wait';
      case 'merged':
      case 'closed': return 'closed';
      // not_started, running, merging, idle → run.
      default: return 'run';
    }
  }
  // Запасной путь: state в данных нет (кеш/моки) — прежний признак.
  const att = run.attention;
  if (att && att.kind === 'stopped') return 'stopped';
  if ((att && att.kind === 'wait') || run.awaitingHumanSince) return 'wait';
  if (run.closed) return 'closed';
  return 'run';
}

// Модель пилюли. null — фич нет (runs пуст). Пилюля всегда показывает runs[0];
// выбор фичи в меню/карточке её НЕ меняет (макет v2, «Правило пилюли»).
export function buildPillModel(runs: FeatureRun[]): PillModel | null {
  if (runs.length === 0) return null;
  const run = runs[0];
  const allClosed = runs.length > 1 && runs.every(r => r.closed);
  const state: PillState = allClosed ? 'allClosed' : stateFor(run);

  const loops = run.verdicts?.loops ?? 0;
  const more = allClosed ? 0 : runs.length - 1;
  const word = allClosed ? 'фичи' : 'фича';

  // Текст состояния (F-pop-a: тексты по утверждённому макету).
  let text: string;
  if (allClosed) text = `${runs.length} · все закрыты`;
  else if (state === 'wait') {
    // merge_pending → «ждёт согласия»; прочий wait → «ждёт вас · {reason}».
    text = run.state === 'merge_pending' ? 'ждёт согласия' : `ждёт вас · ${waitReason(run)}`;
  } else if (state === 'stopped') {
    const name = stoppedName(run);
    text = name ? `стоит · ${name}` : 'стоит';
  } else if (state === 'closed') {
    // merged → «смержена», closed (без мержа) → «закрыта».
    text = run.state === 'merged' ? 'смержена' : 'закрыта';
  } else {
    // run: merging → «мерж · идёт»; not_started/idle → «Не начата»; иначе стадия · исполнители.
    if (run.state === 'merging') text = 'мерж · идёт';
    else if (run.state === 'not_started' || run.state === 'idle') text = 'Не начата';
    else {
      const names = namesFor(run);
      text = names ? `${stageFor(run)} · ${names}` : stageFor(run);
    }
  }

  // Значок и его цвет.
  let icon: PillModel['icon'];
  let iconColor: string;
  if (state === 'wait') { icon = 'diamond'; iconColor = C.info; }
  else if (state === 'stopped') { icon = 'square'; iconColor = C.danger; }
  else if (state === 'closed' || state === 'allClosed') { icon = 'route'; iconColor = C.textMuted; }
  else { icon = 'route'; iconColor = C.accent; } // run — идёт

  // Цвет/вес текста: wait → info, stopped → dangerText (оба 600); иначе обычный.
  const textColor: string | null = state === 'wait' ? C.info : state === 'stopped' ? C.dangerText : null;
  const textWeight = state === 'wait' || state === 'stopped' ? 600 : 400;

  // aria-дополнение: только когда фича ждёт/упала (точка-сигнал в режиме «План хода»).
  const ariaExtra: string | null = state === 'wait' ? 'фича ждёт вас'
    : state === 'stopped' ? 'исполнитель остановлен' : null;

  return {
    state,
    word,
    counter: allClosed ? null : counterFor(run),
    loops,
    more,
    text,
    icon,
    iconColor,
    textColor,
    textWeight,
    loopsHot: loops >= 3,
    ariaExtra,
  };
}
