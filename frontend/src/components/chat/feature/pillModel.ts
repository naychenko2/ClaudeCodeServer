import { getPersonaById } from '../../../lib/personas';
import { MERGE_PENDING_STATUS, queueLabelParts } from '../../../lib/chatGroups';
import { trackerPercent } from '../../../lib/featureProgress';
import { alarmView, findChatChain, stepAlarmBy, stepStatusParts } from './featureTruthModel';
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

// Имя упавшего исполнителя (из тревоги); '' — не известно.
function stoppedName(run: FeatureRun): string {
  const id = run.attention?.personaId;
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

// Словарь коротких слов пилюли (словарь Майи, дизайн-ревью F2, п. 8): ключ — chain.outcome
// (бэковый перечислимый), а НЕ текст chain.status — сокращение переживает смену формулировки
// на бэке. Сокращение ТОЛЬКО в пилюле: карточка и цепочка печатают полное слово
// (stepStatusParts). Неизвестный outcome (более новый бэк) — слово как есть, строка не теряется.
const PILL_SHORT: Record<string, string> = {
  accepted: 'принят',
  returned: 'закрыт без ревью',
  withdrawn: 'снят',
  running: 'идёт',
  pending: 'ждёт ревью',
};

// Модель пилюли (F2, макет v4 §П2 «Мой шаг»). null — фич нет и загрузка/ошибка не
// идёт (пилюля скрыта). Три ветки:
//   • чат ШАГА (цепочка найдена по задаче-владельцу чата) — «{Ш8} · {коротко} · {давность}»:
//     stepRef обычный, статус — короткое слово по outcome (PILL_SHORT; тревога на этом
//     шаге — «стоит»/«ждёт вас» из бэкового attention), время — суффиксом;
//   • тревога фичи (waiting/merge_pending/stopped) — заменяет текст шага: «Ждёт вас ·
//     {Имя}»/«Стоит · {Имя}» тоном (сигнал на пилюле не теряется; действие — в строке шапки);
//     если тревога на ЭТОМ шаге — формат шага «{stepRef} · стоит/ждёт вас · {время}»
//     (тот же тон, слова и время, что у карточки П2 — словарь п. 8);
//   • чат БЕЗ шага (оркестратор, ревью) — «фича · {pct} %» (процент по трекеру, общий
//     trackerPercent): П2 к такому чату неприменим, попап тогда сводка (П1).
// 'loading'/'error' → «фича · …».
export function buildPillModel(runs: FeatureRun[], chatTaskId: string | null | undefined, loading: boolean, error: string | null): PillModel | null {
  if (runs.length === 0) {
    if (!loading && error == null) return null;
    return { state: loading ? 'loading' : 'error', tone: 'neutral', pre: 'фича · …', bold: '', post: '', ariaExtra: null };
  }
  const run = runs[0];
  const chain = findChatChain(run, chatTaskId);

  if (run.state === 'waiting' || run.state === 'merge_pending' || run.state === 'stopped') {
    const who = stoppedName(run);
    const a = alarmView(run.state, run.attention?.reason ?? null, who || null, true);
    const aria = run.state === 'stopped' ? 'исполнитель остановлен' : run.state === 'merge_pending' ? MERGE_PENDING_STATUS : 'фича ждёт вас';
    const tone = a.tone === 'warn' ? 'wait' : 'stop';
    // Тревога на ЭТОМ шаге: слово и время те же, что в карточке («стоит»/«ждёт вас» с
    // attention.since), формат пилюли «{stepRef} · {коротко} · {время}» (словарь, п. 8).
    const alarmBy = chain ? stepAlarmBy(run, chain) : null;
    if (chain && alarmBy) {
      const p = stepStatusParts(chain, Date.now(), alarmBy);
      return { state: run.state, tone, pre: `${chain.stepRef} · `, bold: p.word, post: p.time ? `${p.sep}${p.time}` : '', ariaExtra: aria };
    }
    // Тревога не этого шага (или чат без шага): текст тревоги фичи — сигнал на пилюле не теряется.
    return { state: run.state, tone, pre: a.text, bold: '', post: '', ariaExtra: aria };
  }

  if (chain) {
    // Статус шага — короткое слово по outcome (PILL_SHORT); неизвестный outcome —
    // бэковое слово как есть. Время — общее с карточкой (stepStatusParts).
    const p = stepStatusParts(chain, Date.now());
    return {
      state: run.state,
      tone: 'neutral',
      pre: `${chain.stepRef} · `,
      bold: PILL_SHORT[chain.outcome] ?? p.word,
      post: p.time ? `${p.sep}${p.time}` : '',
      ariaExtra: null,
    };
  }

  // Чат без шага: «фича · {pct} %». Нет stepProgress (старые данные) — без числа.
  const sp = run.stepProgress;
  const pct = sp ? trackerPercent(sp.accepted, sp.total) : null;
  return { state: run.state, tone: 'neutral', pre: 'фича · ', bold: pct != null ? `${pct} %` : '', post: '', ariaExtra: null };
}
