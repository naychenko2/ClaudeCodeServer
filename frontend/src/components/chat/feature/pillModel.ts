import { getPersonaById } from '../../../lib/personas';
import { STAGE_NAME } from '../../../lib/chatGroups';
import type { FeatureRun, FeatureState } from '../../../types/featureRun';

// Тон пилюли (макет turn-plan-feature-popup-1, S[state].pill[0]):
//   wait    — ждёт вас / ждёт согласия (текст C.info);
//   stop    — стоит (текст C.dangerText);
//   ok      — смержена (текст C.successText);
//   neutral — остальное (базовый цвет кнопки, без перекраски).
// Жирная часть текста — всегда C.textHeading 600 (макет .tpp .x b).
export type PillTone = 'wait' | 'stop' | 'ok' | 'neutral';

// Модель пилюли (режим фичи): компонент (TurnPlanPill) «глупый» — только рисует модель.
// Текст — по run.state (9 состояний, макет S[state].pill[1]): префикс + жирная часть + суффикс.
// 'loading'/'error' — run ещё не пришёл (загрузка/ошибка фич): «фича · …».
export interface PillModel {
  // Состояние фичи (run.state, 9 значений) или 'loading'/'error' (run ещё нет).
  state: FeatureState | 'loading' | 'error';
  // Тон (wait/stop/ok/neutral) — цвет текста.
  tone: PillTone;
  // Текст: префикс ("" или "фича · "), жирная часть, суффикс (" · ждёт вас" и т.п.).
  pre: string;
  bold: string;
  post: string;
  // aria-дополнение (точка-сигнал в режиме «План хода», когда фича ждёт/упала).
  ariaExtra: string | null;
}

// Имя активной стадии (для «{этап} · …»); null — активной нет.
function activeStageName(run: FeatureRun): string | null {
  const stage = (run.stages ?? []).find(s => s.state === 'active');
  return stage ? STAGE_NAME[stage.key] : null;
}

// Имена идущих (из now[], уникальные, «Кира, Денис»); '' — никто.
function runningNames(run: FeatureRun): string {
  const names: string[] = [];
  for (const n of run.now ?? []) {
    if (!n.personaId) continue;
    const name = getPersonaById(n.personaId)?.name ?? null;
    if (name && !names.includes(name)) names.push(name);
  }
  return names.join(', ');
}

// Имя упавшего исполнителя (из тревоги); '' — не известно.
function stoppedName(run: FeatureRun): string {
  const id = run.attention?.personaId;
  return (id ? getPersonaById(id)?.name : null) ?? '';
}

// Имя мержа (из merge-задачи); '' — не известно.
function mergingName(run: FeatureRun): string {
  const id = run.merge?.personaId;
  return (id ? getPersonaById(id)?.name : null) ?? '';
}

// Модель пилюли. null — фич нет и загрузка/ошибка не идёт (пилюля скрыта).
// Текст и тон — строго по макету S[state].pill; 'loading'/'error' → «фича · …».
export function buildPillModel(runs: FeatureRun[], loading: boolean, error: string | null): PillModel | null {
  // Run ещё нет: загрузка/ошибка (режим фичи) — «фича · …».
  if (runs.length === 0) {
    if (!loading && error == null) return null;
    return { state: loading ? 'loading' : 'error', tone: 'neutral', pre: 'фича · …', bold: '', post: '', ariaExtra: null };
  }
  const run = runs[0];
  const stage = activeStageName(run);

  switch (run.state) {
    case 'not_started':
      return { state: 'not_started', tone: 'neutral', pre: '', bold: 'Не начата', post: '', ariaExtra: null };
    case 'idle':
      return { state: 'idle', tone: 'neutral', pre: '', bold: 'Простаивает', post: '', ariaExtra: null };
    case 'running': {
      const names = runningNames(run);
      return {
        state: 'running', tone: 'neutral',
        pre: 'фича · ', bold: stage ?? 'Разрез', post: names ? ` · ${names}` : '',
        ariaExtra: null,
      };
    }
    case 'waiting':
      return {
        state: 'waiting', tone: 'wait',
        pre: 'фича · ', bold: stage ?? 'Разрез', post: ' · ждёт вас',
        ariaExtra: 'фича ждёт вас',
      };
    case 'stopped': {
      const name = stoppedName(run);
      return {
        state: 'stopped', tone: 'stop',
        pre: 'фича · ', bold: 'стоит', post: name ? ` · ${name}` : '',
        ariaExtra: 'исполнитель остановлен',
      };
    }
    case 'merge_pending':
      return {
        state: 'merge_pending', tone: 'wait',
        pre: 'фича · ', bold: 'Мерж', post: ' · ждёт согласия',
        ariaExtra: 'фича ждёт вас',
      };
    case 'merging': {
      const name = mergingName(run);
      return {
        state: 'merging', tone: 'neutral',
        pre: 'фича · ', bold: 'Мерж', post: name ? ` · идёт · ${name}` : ' · идёт',
        ariaExtra: null,
      };
    }
    case 'merged':
      return { state: 'merged', tone: 'ok', pre: 'фича · ', bold: 'смержена', post: '', ariaExtra: null };
    case 'closed':
      return { state: 'closed', tone: 'neutral', pre: 'фича · ', bold: 'закрыта', post: '', ariaExtra: null };
  }
  // Запасной путь: state не пришёл (старые данные/кеш) — нейтральная «Не начата».
  return { state: 'not_started', tone: 'neutral', pre: '', bold: 'Не начата', post: '', ariaExtra: null };
}
