import { getPersonaById } from '../../../lib/personas';
import { STAGE_NAME, MERGE_PENDING_STATUS, queueLabelParts } from '../../../lib/chatGroups';
import type { FeatureQueue, FeatureRun, FeatureState } from '../../../types/featureRun';

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

// Уникальные имена персон по id («Кира, Денис»); '' — никто/не резолвятся.
// Единый источник для пилюли (идущие/упавший/мерж) и плашки фичи в чате.
// id — string | null | undefined (опциональные поля FeatureNow.personaId и т.п.).
export function uniquePersonaNames(ids: (string | null | undefined)[]): string {
  const names: string[] = [];
  for (const id of ids) {
    if (!id) continue;
    const name = getPersonaById(id)?.name ?? null;
    if (name && !names.includes(name)) names.push(name);
  }
  return names.join(', ');
}

// Имена идущих (из now[], уникальные, «Кира, Денис»); '' — никто.
function runningNames(run: FeatureRun): string {
  return uniquePersonaNames((run.now ?? []).map(n => n.personaId));
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

// Фраза состояния по state: { bold, post } — ОДИН ИСТОЧНИК слов для пилюли
// (buildPillModel) и плашки фичи в чате (FeatureBanner, «где мы»). Входы те же:
// state, активная стадия и имена (идущие/упавший/мерж).
//   running → «{этап} · {идущие}»; waiting → «{этап} · ждёт вас»;
//   stopped → «стоит · {имя}»; merged → «смержена» (без даты, как в пилюле).
// Удержание для фразы (те же данные, что FeatureHoldInfo в FeatureStatusTag и поля
// holdReason/queue строки индекса): причина и место в очереди. Позицию считает бэк.
export interface StatusPhraseHold {
  holdReason?: string | null;
  queue?: FeatureQueue | null;
}

export function statusPhrase(
  state: FeatureState,
  stage: string | null,
  names: { running?: string; stopped?: string; merging?: string },
  hold?: StatusPhraseHold,
): { bold: string; post: string } {
  switch (state) {
    case 'not_started': return { bold: 'Не начата', post: '' };
    case 'idle': return { bold: 'Простаивает', post: '' };
    // Пауза/черновик (W4-HOLD-B1): не тревога — нейтральная подпись по состоянию.
    // Очередь фич (волна 6, feature-queue-1): пауза с причиной queue — тот же тон,
    // тревоги нет (макет, таблица «Подпись»). ТЕКСТ — из общего хелпера queueLabelParts
    // (chatGroups, R-Q5C-A/R-Q5D-D/R-Q5D-F): берём ГОТОВЫЕ части (жирное слово и суффикс),
    // как в плашке (FeatureBanner, R-Q5D-E): в потребителе нет своей копии слова и формата
    // «N из M» и нет разбора строки по разделителю (ревью RV-K-Q5D-B, минор 3).
    // Позицию считает бэк; queue не пришёл — без цифр (нулей не показываем).
    case 'paused': {
      if (hold?.holdReason === 'queue') {
        const p = queueLabelParts(hold.queue);
        return { bold: p.bold, post: p.post };
      }
      return { bold: 'На паузе', post: '' };
    }
    case 'draft': return { bold: 'Черновик', post: '' };
    case 'running': return { bold: stage ?? 'Разрез', post: names.running ? ` · ${names.running}` : '' };
    case 'waiting': return { bold: stage ?? 'Разрез', post: ' · ждёт вас' };
    case 'stopped': return { bold: 'стоит', post: names.stopped ? ` · ${names.stopped}` : '' };
    case 'merge_pending': return { bold: 'Мерж', post: ' · ждёт согласия' };
    case 'merging': return { bold: 'Мерж', post: names.merging ? ` · идёт · ${names.merging}` : ' · идёт' };
    case 'merged': return { bold: 'смержена', post: '' };
    case 'closed': return { bold: 'закрыта', post: '' };
  }
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

  // Слова — из statusPhrase (общий источник с плашкой фичи в чате); тон/aria — по макету.
  switch (run.state) {
    case 'not_started': {
      const p = statusPhrase('not_started', null, {});
      return { state: 'not_started', tone: 'neutral', pre: '', bold: p.bold, post: p.post, ariaExtra: null };
    }
    case 'idle': {
      const p = statusPhrase('idle', null, {});
      return { state: 'idle', tone: 'neutral', pre: '', bold: p.bold, post: p.post, ariaExtra: null };
    }
    case 'paused': {
      // Очередь фич (волна 6, feature-queue-1): пауза с причиной queue — те же слова,
      // что у рельсы в чате (statusPhrase, общий источник). manual — как прежде
      // (запасной путь ниже): задача про очередь подпись ручной паузы не меняет.
      if (run.hold?.reason === 'queue') {
        const p = statusPhrase('paused', null, {}, { holdReason: 'queue', queue: run.queue });
        return { state: 'paused', tone: 'neutral', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: null };
      }
      break;
    }
    case 'running': {
      const p = statusPhrase('running', stage, { running: runningNames(run) });
      return { state: 'running', tone: 'neutral', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: null };
    }
    case 'waiting': {
      const p = statusPhrase('waiting', stage, {});
      return { state: 'waiting', tone: 'wait', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: 'фича ждёт вас' };
    }
    case 'stopped': {
      const p = statusPhrase('stopped', null, { stopped: stoppedName(run) });
      return { state: 'stopped', tone: 'stop', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: 'исполнитель остановлен' };
    }
    case 'merge_pending': {
      const p = statusPhrase('merge_pending', null, {});
      return { state: 'merge_pending', tone: 'wait', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: MERGE_PENDING_STATUS };
    }
    case 'merging': {
      const p = statusPhrase('merging', null, { merging: mergingName(run) });
      return { state: 'merging', tone: 'neutral', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: null };
    }
    case 'merged': {
      const p = statusPhrase('merged', null, {});
      return { state: 'merged', tone: 'ok', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: null };
    }
    case 'closed': {
      const p = statusPhrase('closed', null, {});
      return { state: 'closed', tone: 'neutral', pre: 'фича · ', bold: p.bold, post: p.post, ariaExtra: null };
    }
  }
  // Запасной путь: state не пришёл (старые данные/кеш) — нейтральная «Не начата».
  return { state: 'not_started', tone: 'neutral', pre: '', bold: 'Не начата', post: '', ariaExtra: null };
}
