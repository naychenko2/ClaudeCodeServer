// Перевод ключей brief в слова (волна 6 «Сводка состояния фичи», шаг F2).
//
// ЧИСТЫЙ модуль: ни DOM, ни запросов, ни пересчёта — только текст из полей `brief`
// (разрез п. 7: «своих у фронта только перевод ключей стадий в слова и "N ч назад"
// от at»; kind/stale/next/estimate фронт НЕ выводит). Форматтеры времени — общие,
// из FeatureStatsBits.ts (тот же «N мин/ч», что в ленте); линия — laneWord из roleLabel.
//
// Строки: макет docs/mockups/feature-summary-1.md (состояния §2 и §«Токены поимённо»);
// для kind, которых в макете нет (error/draft/idle) — строки разреза п. 4.
// Оценки нет → «Оценки пока нет»; для queued — «Оценка — после старта»: для очереди
// оценки нет не от нехватки данных, а потому что фича не началась (решение Софьи,
// R-F2→F5). «≈ 0» не печатается нигде.
// Слова подстроки «Сейчас» и строки «Сделано» — тоже здесь (R-F5: единый источник
// формулировки; блок только вызывает модель и красит числа моно).

import { formatClock, formatDate, formatDuration, minutesSince, stageLabel } from './FeatureStatsBits';
import { trackerPercent } from '../../lib/featureProgress';
import { laneWord } from '../../lib/roleLabel';
import { knownStopVisual } from '../../lib/tasks';
import type { FeatureBrief, FeatureBriefActive, FeatureBriefActiveState, FeatureBriefKind, FeatureBriefNote } from '../../lib/featurePage';
import type { FeatureRailKey } from '../../types/featureIndex';

// Резолвер имени персоны по personaId (данные вызывают: usePersonas/getPersonaById).
// null — имя неизвестно: часть строки с именем опускается, строка остаётся валидной
// (тот же приём, что personaName в FeatureProdCards.ts).
export type PersonaNameResolver = (personaId: string) => string | null;

// Дополнительные данные строки «Сейчас» вне brief:
// mergedAt — момент мержа из run.summary.mergedAt (в brief его нет; для строк closed/merged).
// inMaster — сколько шагов уже в master (F1, бэк: run.stepProgress.inMaster; в brief его
// нет): ортогональный счётчик, процентом не считается, звучит только как пометка.
export interface BriefTextOptions {
  nameOf?: PersonaNameResolver;
  mergedAt?: string | null;
  inMaster?: number | null;
}

// Родительный падеж имени для «у {Имя}» (макет: «— у Глеба»). Эвристика для частых
// кириллических имён: «…ь»/«…й»→«…я» (Игорь→Игоря, Дмитрий→Дмитрия),
// «…я»/«…е»→«…и», «…а»→«…ы», твёрдая согласная→+«а»;
// некириллическое окончание — как есть.
export function genitiveName(name: string): string {
  const last = name.slice(-1).toLowerCase();
  if (last === 'ь' || last === 'й') return `${name.slice(0, -1)}я`;
  if (last === 'я' || last === 'е') return `${name.slice(0, -1)}и`;
  if (last === 'а') return `${name.slice(0, -1)}ы`;
  if (/[а-яё]/.test(last)) return `${name}а`;
  return name;
}

// Lowercase-название стадии для связных фраз («После ревью — …»): та же таблица
// stageLabel, что в ленте, но в середине предложения строчными.
function stageWord(key: string): string {
  return stageLabel(key).toLowerCase();
}

// Родительный падеж стадии для префикса «После …» (defect 16080f64): stageLabel даёт
// именительный («Производство»), а после «После» нужен родительный — «После производство»
// читалось сломанным падежом. Словарь — только родительные формы (не копия stageLabel),
// с теми же ключами: новая стадия в stageLabel требует родительной формы здесь.
// «ревью» — средний род, не склоняется. Типизирована Record<FeatureRailKey, string>
// (R-F17 п.2, прецедент STAGE_NAME в chatGroups.ts): новая стадия конвейера без родительной
// формы не соберётся. Рантаймовый фолбэк остаётся: стадия в контракте — string
// (featurePage.ts:51), незнакомый ключ более нового бэка не склоняется — формы невиданного
// слова не знать, остаётся ключ как есть (stageWord).
const STAGE_GENITIVE: Record<FeatureRailKey, string> = {
  cut: 'разреза', design: 'дизайна', plan: 'плана', prod: 'производства',
  review: 'ревью', qa: 'проверки', final: 'финального ревью', merge: 'мержа',
};

// ── «Сейчас» ────────────────────────────────────────────────────────────────────────
// Одна строка состояния по kind (макет §2, разрез п. 4). null — данных для строки нет
// (макет: «если какого-то поля нет, строку не рисуем»).
export function briefNowText(brief: FeatureBrief, opts: BriefTextOptions = {}): string | null {
  switch (brief.kind) {
    case 'running': {
      // «Ревью шага 3 (бэк) — у Глеба»: стадия + шаг + линия + исполнитель (разрез п. 4).
      const n = brief.now;
      if (!n) return null;
      // Пустая часть не входит в строку: пустой stage давал ведущий пробел (R-F8).
      const parts: string[] = [];
      if (n.stage) parts.push(stageLabel(n.stage));
      if (n.stepRef) parts.push(`шага ${n.stepRef}`);
      const lane = laneWord(n.lane);
      if (lane) parts.push(`(${lane})`);
      const name = n.personaId ? opts.nameOf?.(n.personaId) : null;
      if (name) parts.push(`— у ${genitiveName(name)}`);
      return parts.length ? parts.join(' ') : null;
    }
    case 'waiting_you': {
      // Тревога «мерж» (бэк кладёт код «мерж») — строка макета; прочая причина — как есть.
      const r = brief.reason;
      if (!r || r === 'мерж') return 'Ждёт вашего согласия на мерж в master';
      return `Ждёт вашего ответа: ${r}`;
    }
    case 'error': {
      // Причина — машинный код бэка (ExecutorStopClassifier → Attention.Reason,
      // FeatureProjector.cs:143): переводится общей таблицей knownStopVisual — как соседний
      // блок «Остановлена» и плашка карточки (defect c79050aa: код протекал в строку как есть —
      // «Исполнитель встал: interrupted_by_user»; R-F17 п.1: и «Стоп» человека — в той же
      // таблице). Нет причины и неизвестный код — одна фраза без двоеточия (R-F17 п.5):
      // одно состояние не может называться по-разному от того, знает ли фронт код.
      const r = brief.reason;
      if (!r) return 'Исполнитель встал';
      const stop = knownStopVisual(r);
      return stop ? `Исполнитель встал: ${stop.text}` : 'Исполнитель встал';
    }
    case 'paused': {
      // «На паузе — поставили вы в 13:40» (макет): код Hold.Reason 'manual' + время
      // тревоги (now.since). Своя причина тревоги (прерванная) — текстом.
      if (brief.reason && brief.reason !== 'manual' && brief.reason !== 'queue') {
        return `На паузе — ${brief.reason}`;
      }
      const t = brief.now?.since ? ` в ${formatClock(brief.now.since)}` : '';
      return `На паузе — поставили вы${t}`;
    }
    case 'queued': {
      // До готовности очереди (решение плана: brief.queue всегда null) — без номера.
      // С позицией — числа как есть: словесный порядок («вторая из трёх») — дело
      // эпика очереди, когда он принесёт позицию; в контракте — position/total.
      const q = brief.queue;
      if (!q) return 'Ждёт своей очереди';
      return `В очереди: ${q.position} из ${q.total}`;
    }
    case 'draft':
      // Разрез п. 4.
      return 'Готовится: разрез/план';
    case 'idle':
      // Разрез п. 4 («тихо N ч» — поля Quiet в контракте нет, строки нет).
      return 'Ничего не идёт';
    case 'closed':
    case 'merged': {
      // «Смержена в master 24.10 в 18:05» (макет) — дата мержа от вызывающего (run.summary);
      // без даты — «Закрыта» (разрез п. 4). 'merged' (Ф-1.1) — та же строка, что 'closed':
      // фича смержена, эпик ещё не закрыт (Б-1.1) — для свода ведётся так же, как 'closed'.
      const at = opts.mergedAt;
      if (at) return `Смержена в master ${formatDate(at)} в ${formatClock(at)}`;
      return 'Закрыта';
    }
  }
}

// ── «Сейчас»: подстрока ─────────────────────────────────────────────────────────────
// Макет: «с какого времени · сколько длится». Только данные контракта (now.since) и
// общие форматтеры (та же арифметика, что «N назад» ниже); статические части фраз
// макета, не утверждающие фактов вне контракта. Нет since — подстроки нет.
// Нулевая длительность — не число: «ждёт 0 мин» / «на паузе 0 мин» та же болезнь нуля,
// что «идёт 0 мин» / «в очереди 0 мин» (F10 минор; порог — тот же round, что у
// briefEstimateText). Нет числа — остаётся только статическая часть фразы.
function shownDuration(iso: string, nowMs: number): string | null {
  const m = minutesSince(iso, nowMs); // уже round; порог совпадает с гейтом «идёт»
  return m != null && m > 0 ? formatDuration(m) : null;
}

export function briefSubText(brief: FeatureBrief, nowMs: number): string | null {
  const since = brief.now?.since;
  switch (brief.kind) {
    case 'running': {
      if (!since) return null;
      const dur = shownDuration(since, nowMs);
      return dur ? `с ${formatClock(since)} · ${dur}` : `с ${formatClock(since)}`;
    }
    case 'waiting_you': {
      if (!since) return null;
      const dur = shownDuration(since, nowMs);
      return dur ? `ждёт ${dur} · без вас фича стоит` : 'без вас фича стоит';
    }
    case 'paused': {
      if (!since) return null;
      const dur = shownDuration(since, nowMs);
      return dur ? `на паузе ${dur} · исполнители не запускаются` : 'исполнители не запускаются';
    }
    case 'closed':
    case 'merged':
      return 'фича закрыта';
    default:
      return null;
  }
}

// ── «Сделано» ───────────────────────────────────────────────────────────────────────
// Счётчики done как есть (макет: «2 из 5 шагов приняты · 1 виток правок»). Для «в очереди»
// с нарезанным планом (planState cut/done) — строка макета queue «План готов · 0 из 4 шагов»:
// факт готовности плана несёт done.planState (R-F8). При незавершённом плане очереди
// («pending»/«partial») формулировки нет — обычный счётчик, не выдумываем.
function pluralRu(n: number, one: string, few: string, many: string): string {
  const d = n % 10, h = n % 100;
  if (d === 1 && h !== 11) return one;
  if (d >= 2 && d <= 4 && (h < 12 || h > 14)) return few;
  return many;
}

// Часть строки «Сделано»: mono — число (макет §«Токены поимённо»: числа mono 600
// textHeading), остальное — связный текст. Блок красит mono-части моно, слов не добавляет.
export interface BriefDonePart {
  text: string;
  mono: boolean;
}

export function briefDoneText(brief: FeatureBrief): BriefDonePart[] {
  const { stepsAccepted, stepsTotal, loops, planState } = brief.done;
  if (brief.kind === 'queued' && (planState === 'cut' || planState === 'done')) {
    // «План готов · <mono>0 из 4</mono> шагов» (макет queue): готовность плана — факт
    // planState, счётчик шагов — как есть, без «приняты»: фича ещё не шла.
    return [
      { text: 'План готов', mono: false },
      { text: ' · ', mono: false },
      { text: `${stepsAccepted} из ${stepsTotal}`, mono: true },
      { text: ' шагов', mono: false },
    ];
  }
  const parts: BriefDonePart[] = [
    { text: `${stepsAccepted} из ${stepsTotal}`, mono: true },
    { text: ' шагов приняты', mono: false },
  ];
  if (loops > 0) {
    parts.push({ text: ` · ${loops}`, mono: true });
    parts.push({ text: ` ${pluralRu(loops, 'виток', 'витка', 'витков')} правок`, mono: false });
  }
  return parts;
}

// ── «Дальше» ────────────────────────────────────────────────────────────────────────
// Строка из массива next (разрез п. 4: «После ревью — проверка (Вера), затем
// финальное ревью и мерж» собирается на фронте из next ≤3). Префикс — по kind:
// живые формулировки макета («снятия паузы», «вашего «да»», «старта»); для kinds
// вне макета — нейтральное «Дальше». null — идти некуда (next пуст, нет активной
// стадии для префикса running): строку не рисуем.
export function briefNextText(brief: FeatureBrief, nameOf?: PersonaNameResolver): string | null {
  if (brief.kind === 'closed' || brief.kind === 'merged') return 'Ничего — фича закрыта';

  // Строка про мерж — только когда мерж действительно ожидается: причина «мерж»
  // (бэк кладёт такой код) или следующий шаг — merge (макет wait). При другой
  // причине — общий префикс ниже; при пустом next строки нет (R-F2: не выдумывать мерж).
  if (brief.kind === 'waiting_you' && (brief.reason === 'мерж' || brief.next[0]?.stage === 'merge')) {
    return 'После вашего «да» — мерж в master';
  }

  let prefix: string;
  switch (brief.kind) {
    case 'running': {
      // «После ревью — …»: активная стадия из now (ее нет — префикса нет). Стадия после
      // «После» в родительном падеже (defect 16080f64): «После производство» — нет.
      if (!brief.now) return null;
      const genitive = Object.prototype.hasOwnProperty.call(STAGE_GENITIVE, brief.now.stage)
        ? STAGE_GENITIVE[brief.now.stage as FeatureRailKey]
        : stageWord(brief.now.stage);
      prefix = `После ${genitive}`;
      break;
    }
    case 'waiting_you': prefix = 'После вашего «да»'; break;
    case 'paused': prefix = 'После снятия паузы'; break;
    case 'queued': prefix = 'После старта'; break;
    case 'draft': prefix = 'После плана'; break;
    default: prefix = 'Дальше'; break; // error/idle — состояний в макете нет
  }

  const parts = brief.next.map(n => {
    const name = n.personaId ? nameOf?.(n.personaId) : null;
    return name ? `${stageWord(n.stage)} (${name})` : stageWord(n.stage);
  });
  if (!parts.length) return null;
  let s = `${prefix} — ${parts[0]}`;
  if (parts.length > 1) s += `, затем ${parts.slice(1).join(' и ')}`;
  return s;
}

// ── «Сейчас»: заголовок и список задач в работе ──────────────────────────────────────
// Макет feature-summary-2, «Итерация 2». Порядок строк задаёт бэк (ждёт вас → ошибка/стоит →
// по времени), фронт его не меняет: он собирает только слова. null — списка нет, блок рисует
// прежнюю строку briefNowText: поля active нет (старый бэк), эпик ждёт мержа (stage feature —
// там «Мерж…»), либо задача одна и никто не ждёт.
export const ACTIVE_ROWS_MAX = 4;

export interface BriefActiveRow {
  taskId: string;
  state: FeatureBriefActiveState;
  // «Шаг 4 (фронт)» / «Ревью шага 3 (бэк)».
  title: string;
  who: string | null;
  // Ошибка: «· стоит» после имени (красным); у остальных null.
  stopped: string | null;
  // «с 14:05 · 12 мин» / «ждёт 8 мин» / «остановлен в 13:40».
  time: string | null;
  ariaLabel: string;
}

export interface BriefActiveView {
  // Заголовок вместо briefNowText; null — оставить прежнюю строку (пауза).
  heading: string | null;
  sub: string | null;
  // Кнопка «Ответить Кире»: открывает чат задачи, которая ждёт человека.
  answer: { taskId: string; label: string } | null;
  rows: BriefActiveRow[];
  // «ещё N в работе — во вкладке «Задачи» ›»; null — все строки на виду.
  tail: string | null;
}

// Дательный падеж имени для «Ответить {Кире}» (эвристика, как genitiveName).
export function dativeName(name: string): string {
  const last = name.slice(-1).toLowerCase();
  if (last === 'ь' || last === 'й') return `${name.slice(0, -1)}ю`;
  if (last === 'я' || last === 'а') return `${name.slice(0, -1)}е`;
  if (/[а-яё]/.test(last) && !/[еиоуыэюё]/.test(last)) return `${name}у`;
  return name;
}

function activeTitle(a: FeatureBriefActive): string {
  const lane = laneWord(a.lane);
  const laneText = lane ? ` (${lane})` : '';
  if (a.stage === 'prod') return a.stepRef ? `Шаг ${a.stepRef}${laneText}` : `${stageLabel('prod')}${laneText}`;
  return `${stageLabel(a.stage)}${a.stepRef ? ` шага ${a.stepRef}` : ''}${laneText}`;
}

function activeTime(a: FeatureBriefActive, nowMs: number): string | null {
  const clock = formatClock(a.since);
  if (!clock) return null;
  if (a.state === 'held') return `остановлен в ${clock}`;
  const dur = shownDuration(a.since, nowMs);
  if (a.state === 'waiting_you') return dur ? `ждёт ${dur}` : `с ${clock}`;
  if (a.state === 'error') return `с ${clock}`;
  return dur ? `с ${clock} · ${dur}` : `с ${clock}`;
}

// Подстрока при нескольких задачах: «параллельно: бэк и фронт».
function lanesText(list: FeatureBriefActive[]): string | null {
  const lanes = [...new Set(list.map(a => laneWord(a.lane)).filter(Boolean))];
  if (!lanes.length) return null;
  if (lanes.length === 1) return `линия: ${lanes[0]}`;
  return `параллельно: ${lanes.slice(0, -1).join(', ')} и ${lanes[lanes.length - 1]}`;
}

export function briefActiveView(brief: FeatureBrief, opts: BriefTextOptions & { nowMs: number }): BriefActiveView | null {
  const list = brief.active ?? [];
  const n = list.length;
  if (!n || list.some(a => a.stage === 'feature')) return null;
  const paused = brief.kind === 'paused';
  const waiting = list.find(a => a.state === 'waiting_you') ?? null;
  if (n === 1 && !paused && !waiting) return null;

  const nameOf = (a: FeatureBriefActive) => (a.personaId ? opts.nameOf?.(a.personaId) ?? null : null);
  const rows: BriefActiveRow[] = n === 1 && !paused ? [] : list.slice(0, ACTIVE_ROWS_MAX).map(a => {
    const title = activeTitle(a);
    const who = nameOf(a);
    return {
      taskId: a.taskId, state: a.state, title, who, stopped: a.state === 'error' ? '· стоит' : null, time: activeTime(a, opts.nowMs),
      ariaLabel: `${title}${who ? ` — ${who}` : ''}, открыть задачу`,
    };
  });
  const tail = rows.length && n > ACTIVE_ROWS_MAX ? `ещё ${n - ACTIVE_ROWS_MAX} в работе — во вкладке «Задачи» ›` : null;

  if (waiting) {
    const name = nameOf(waiting);
    const step = waiting.stepRef ? ` по шагу ${waiting.stepRef}` : '';
    const others = n - 1;
    const dur = shownDuration(waiting.since, opts.nowMs);
    const stuck = waiting.stepRef ? `шаг ${waiting.stepRef} стоит` : 'задача стоит';
    const rest = others > 0 ? `, ${others} ${pluralRu(others, 'другая задача идёт', 'другие задачи идут', 'других задач идут')}` : '';
    return {
      heading: `${name ?? 'Задача'} ждёт вашего ответа${step}`,
      sub: [dur ? `ждёт ${dur}` : null, `${stuck}${rest}`].filter(Boolean).join(' · '),
      answer: { taskId: waiting.taskId, label: name ? `Ответить ${dativeName(name)}` : 'Ответить' },
      rows, tail,
    };
  }
  return {
    heading: paused ? null : `${n} ${pluralRu(n, 'задача', 'задачи', 'задач')} в работе`,
    sub: paused ? null : lanesText(list),
    answer: null, rows, tail,
  };
}

// Тег в шапке со счётом задач: «идёт · 3 в работе», «ждёт вас · 1 из 3». null — тег прежний
// (задача одна или поля active нет).
export function briefActiveTagLabel(brief: FeatureBrief): string | null {
  const list = brief.active ?? [];
  if (list.length < 2) return null;
  if (brief.kind === 'running') return `идёт · ${list.length} в работе`;
  if (brief.kind === 'waiting_you') return `ждёт вас · ${list.filter(a => a.state === 'waiting_you').length} из ${list.length}`;
  return null;
}

// ── «Готовность» ────────────────────────────────────────────────────────────────────
// Макет feature-summary-2: полоска по оценке оркестратора (шаги плюс стадии конвейера), без
// неё — по трекеру. Счёт и согласие оценок приходят с бэка; единственная арифметика здесь —
// доля штриховки «в работе» и процент по трекеру (done/total), когда оценки нет.
export interface BriefChip {
  text: string;
  // next — первый («следом»), you — «за вами», plain — остальные.
  tone: 'next' | 'you' | 'plain';
}

export interface BriefReadinessView {
  // Подпись: «Готовность» / «Готовность · по трекеру».
  label: string;
  // Подпись под полоской: «4 готово · 3 в работе · из 9 · 45 %» (mono — числа);
  // при шагах в master — пометка «· N в master» (F1).
  caption: BriefDonePart[];
  percent: number;
  // aria-label полоски: «Готовность 45 %», при шагах в master — «Готовность 45 %, 3 в master».
  ariaLabel: string;
  // F1: сколько шагов в master (0 — нет или данных нет; ортогональный счётчик бэка).
  inMaster: number;
  // «в master»-сегмент полоски, % ширины полоски — только когда полоска по шкале трекера
  // (оценки нет): на шкале оценки оркестратора счётчик шагов не стоит — тик был бы ложью.
  // 0 — сегмента нет. Никогда не больше percent.
  inMasterPercent: number;
  // Доля штриховки «в работе», 0 — штриховки нет.
  inProgressPercent: number;
  // Оценка оркестратора устарела: полоска тускнеет.
  stale: boolean;
  closed: boolean;
  // Метка устаревания у факта трекера («до смены этапа» / «до паузы»).
  staleLabel: string | null;
  // Факт трекера: «по трекеру: 2 из 5 шагов приняты · 1 виток правок».
  tracker: BriefDonePart[];
  // Подпись чипов: «Ещё не начато:» / «Осталось:».
  chipsLabel: string;
  chips: BriefChip[];
  // Вместо чипов (закрытая): «ничего — всё сделано».
  chipsEmpty: string | null;
}

const MERGE_CHIP = 'мерж · за вами';
const CHIP_CAP = 3;

function clampPercent(n: number): number {
  return Math.max(0, Math.min(100, Math.round(n)));
}

// Защита от битых чисел (тот же приём, что nonNeg в featureProgress): отрицательное/NaN — 0.
function nonNeg(n: number | null | undefined): number {
  return Number.isFinite(n) && (n as number) > 0 ? Math.round(n as number) : 0;
}

// Часть «· N в master» (F1): пометка ОДНА, на подписи полоски готовности — в факте трекера
// её нет (ревю 07eee660 минор 3: в одном блоке «N в master» печаталось дважды). Пусто при
// 0/отсутствии данных — поведение прежнее (отметки нет там, где шагов в master нет).
function masterParts(inMaster: number): BriefDonePart[] {
  if (inMaster <= 0) return [];
  return [{ text: ' · ', mono: false }, { text: String(inMaster), mono: true }, { text: ' в master', mono: false }];
}

export function briefReadinessText(brief: FeatureBrief, opts: BriefTextOptions = {}): BriefReadinessView {
  const closed = brief.kind === 'closed' || brief.kind === 'merged';
  const est = brief.note?.estimate ?? null;
  const stale = !!est && !!brief.note?.stale;
  const staleLabel = stale && brief.note ? voiceStaleLabel(brief.note, brief.kind) : null;
  const inMaster = nonNeg(opts.inMaster);

  const trackerParts = briefDoneText(brief);
  const planFact = brief.kind === 'queued' && (brief.done.planState === 'cut' || brief.done.planState === 'done');
  const tracker: BriefDonePart[] = planFact ? trackerParts : [{ text: 'по трекеру: ', mono: false }, ...trackerParts];
  // «в master» к строке трекера НЕ добавляется: это ровно тот же ортогональный счётчик,
  // что показан на подписи полоски ниже; второй раз в том же блоке он не звучит (минор 3).

  let caption: BriefDonePart[];
  let percent: number;
  let inProgressPercent = 0;
  // Сегмент «в master» на полоске — только по шкале трекера (ниже); на шкале оценки — 0.
  let inMasterPercent = 0;
  if (est && est.total > 0) {
    percent = clampPercent(est.percent ?? (est.done / est.total) * 100);
    const w = est.inProgress ?? 0;
    if (w > 0 && !closed) inProgressPercent = Math.min(clampPercent((w / est.total) * 100), 100 - percent);
    caption = w > 0 && !closed
      ? [
        { text: String(est.done), mono: true }, { text: ' готово · ', mono: false },
        { text: String(w), mono: true }, { text: ' в работе · из ', mono: false }, { text: String(est.total), mono: true },
      ]
      : [{ text: `${est.done} из ${est.total}`, mono: true }];
    caption.push({ text: ' · ', mono: false }, { text: `${percent} %`, mono: true });
  } else {
    const { stepsAccepted, stepsTotal } = brief.done;
    // Процент по трекеру — общий trackerPercent (та же функция, что полосам В2/С1/П2):
    // числитель и знаменатель бэковые, второй копии арифметики в проекте нет.
    percent = trackerPercent(stepsAccepted, stepsTotal);
    caption = [
      { text: `${stepsAccepted} из ${stepsTotal}`, mono: true }, { text: ' шагов · ', mono: false },
      { text: `${percent} %`, mono: true },
    ];
    // Та же шкала, что у знаменателя полоски: доля шагов в master от total, не выше
    // залитой части. Бэк НЕ гарантирует inMaster ≤ accepted: отсечка требует allDone
    // цепочки, а не принятия (FeatureStepAccounting.cs:321-328), поэтому шаг «в master»
    // без витка (корзина review) даёт inMaster > accepted. Это не битые числа, а правило
    // полоски: «в master» — часть принятой части шкалы готовности, за неё сегмент не
    // выходит; текст при этом честно называет факт (он ортогональный счётчик бэка),
    // а расхождение говорит себе в «Трекер расходится» (вид unreviewed).
    if (inMaster > 0 && stepsTotal > 0) inMasterPercent = Math.min(clampPercent((inMaster / stepsTotal) * 100), percent);
  }
  caption.push(...masterParts(inMaster));

  // Чипы «ещё не начато»: оркестратор — его remaining; без оценки — brief.next (до трёх стадий).
  const mergeAsk = brief.kind === 'waiting_you' && (brief.reason === 'мерж' || brief.next[0]?.stage === 'merge');
  let texts: string[];
  if (est) {
    texts = est.remaining.filter(Boolean);
  } else {
    texts = brief.next.slice(0, CHIP_CAP).map(n => {
      const name = n.personaId ? opts.nameOf?.(n.personaId) : null;
      return name ? `${stageWord(n.stage)} · ${name}` : stageWord(n.stage);
    });
  }
  let chips: BriefChip[];
  if (mergeAsk) {
    chips = [{ text: MERGE_CHIP, tone: 'you' }];
  } else {
    chips = texts.map((t, i) => (i === 0 ? { text: `следом · ${t}`, tone: 'next' as const } : { text: t, tone: 'plain' as const }));
  }

  return {
    label: est ? 'Готовность' : 'Готовность · по трекеру',
    caption, percent,
    ariaLabel: inMaster > 0 ? `Готовность ${percent} %, ${inMaster} в master` : `Готовность ${percent} %`,
    inProgressPercent, stale, closed, staleLabel, tracker,
    inMaster, inMasterPercent,
    chipsLabel: est ? 'Ещё не начато:' : 'Осталось:',
    chips,
    chipsEmpty: closed ? 'ничего — всё сделано' : null,
  };
}

// ── «Срок» ──────────────────────────────────────────────────────────────────────────
// Правило двух оценок (макет feature-summary-2): главное число всегда одно. Свежая оценка
// оркестратора главнее; устаревшая или отсутствующая — по истории. «Расходятся» считает бэк
// (agreement), фронт не сравнивает. Время готовности: у оркестратора — etaEndsAt бэка, у
// истории — computedAt + remainingMinutes.
export interface BriefEtaVerdict {
  text: string;
  // ok — «сходится» (зелёный), warn — «дольше / быстрее / до смены этапа» (жёлтая метка).
  tone: 'ok' | 'warn';
}

export interface BriefEstimateView {
  // Подпись блока: «Срок»; у закрытой — «Итог».
  header: string;
  // Главное число: «≈ 2 ч 40 мин» / «за вами» / «Оценки пока нет» / «шла 6 ч 40 мин».
  main: string;
  // Хвост главного числа: «· к 16:50», «после старта», «после снятия паузы».
  tail: string | null;
  // main не значение, а обещание/пустота: блок красит приглушённым.
  mainMuted: boolean;
  // Подпись источника: «по оценке Софьи · 14 мин назад» / «разброс … · по 14 прошлым фичам».
  source: string | null;
  // Вторая оценка — справка, строка под числом; verdict — метка в её конце.
  alt: string | null;
  verdict: BriefEtaVerdict | null;
  // «Почему» оркестратора: только при расхождении.
  why: string | null;
}

export interface BriefEstimateOptions extends BriefTextOptions {
  nowMs?: number;
}

const NO_ESTIMATE = 'Оценки пока нет';
const HOUR_12 = 12 * 60 * 60 * 1000;

function etaMain(minutes: number): string {
  return minutes < 1 ? 'меньше минуты' : `≈ ${formatDuration(minutes)}`;
}

// «к 16:50»; дальше 12 ч от «сейчас» — дата: «завтра к 10:30», «к 31.10». Нечитаемый момент — null.
// marker — связующее слово («к» / «завтра к»); строка шапки В2 («≈ 18:30») зовёт с «≈» —
// тот же момент, другая интонация строки (общий форматтер, второй копии нет).
export function readyAtText(iso: string | null | undefined, nowMs: number, marker = 'к'): string | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return null;
  if (t - nowMs <= HOUR_12) return `${marker} ${formatClock(iso)}`;
  const target = new Date(t);
  const tomorrow = new Date(nowMs);
  tomorrow.setDate(tomorrow.getDate() + 1);
  const isTomorrow = target.getFullYear() === tomorrow.getFullYear() && target.getMonth() === tomorrow.getMonth() && target.getDate() === tomorrow.getDate();
  return isTomorrow ? `завтра ${marker} ${formatClock(iso)}` : `${marker} ${formatDate(iso)}`;
}

function pastFeatures(n: number): string {
  const d = n % 10, h = n % 100;
  return `по ${n} ${d === 1 && h !== 11 ? 'прошлой фиче' : 'прошлым фичам'}`;
}

function spread(h: { p25: number; p75: number }): string {
  return `${formatDuration(h.p25)} – ${formatDuration(h.p75)}`;
}

export function briefEstimateText(brief: FeatureBrief, opts: BriefEstimateOptions = {}): BriefEstimateView {
  const out = estimateView(brief, opts);
  // Очередь: «старт ≈ 15:00» — справка, если вторая оценка строку не заняла.
  if (brief.kind === 'queued' && brief.queueStartEstimate && !out.alt) {
    const t = formatClock(brief.queueStartEstimate);
    if (t) out.alt = `старт ≈ ${t}`;
  }
  return out;
}

function estimateView(brief: FeatureBrief, opts: BriefEstimateOptions): BriefEstimateView {
  const nowMs = opts.nowMs ?? Date.now();
  const note = brief.note;
  const est = note?.estimate ?? null;
  const orchName = note?.byPersonaId ? opts.nameOf?.(note.byPersonaId) ?? null : null;
  const hist = brief.estimate;
  const histMinutes = hist && hist.remainingMinutes >= 1 ? hist.remainingMinutes : null;
  const orchMinutes = est?.etaMinutes ?? null;
  const staleLabel = note ? voiceStaleLabel(note, brief.kind) : null;
  const orchFresh = orchMinutes != null && !note?.stale;
  const orchStale = orchMinutes != null && !!note?.stale;
  const orchAgo = note ? agoText(note.at, nowMs) : null;
  const base = { header: 'Срок', mainMuted: false, tail: null, source: null, alt: null, verdict: null, why: null };

  if (brief.kind === 'closed' || brief.kind === 'merged') {
    // Итог: «шла N» + на старте оркестратор обещал ≈ M (и история, если есть).
    const elapsed = brief.elapsedMinutes != null && Math.round(brief.elapsedMinutes) > 0 ? formatDuration(brief.elapsedMinutes) : null;
    const promised = brief.initialEtaMinutes != null && brief.initialEtaMinutes >= 1 ? brief.initialEtaMinutes : null;
    const altParts: string[] = [];
    if (promised != null) altParts.push(`на старте ${orchName ? `${orchName} обещала` : 'обещано'} ≈ ${formatDuration(promised)}`);
    if (histMinutes != null) altParts.push(`по истории ≈ ${formatDuration(histMinutes)}`);
    return {
      ...base, header: 'Итог', main: elapsed ? `шла ${elapsed}` : NO_ESTIMATE,
      alt: altParts.length ? altParts.join(' · ') : null,
    };
  }

  // Хвост по состоянию: пауза и очередь — слова; «ждёт вас» — без времени; «идёт» — «к HH:MM».
  const tailFor = (iso: string | null | undefined): string | null => {
    if (brief.kind === 'paused') return 'после снятия паузы';
    if (brief.kind === 'queued') return 'после старта';
    if (brief.kind === 'running') {
      const at = readyAtText(iso, nowMs);
      return at ? `· ${at}` : null;
    }
    return null;
  };
  const histAt = brief.kind === 'running' && histMinutes != null
    ? new Date(Date.parse(brief.computedAt) + histMinutes * 60000).toISOString() : null;
  const histSpread = hist && histMinutes != null ? `по истории ≈ ${formatDuration(histMinutes)} (${spread(hist)})` : null;

  if (brief.kind === 'waiting_you') {
    // Остальное — за человеком: числа оценки нет, времени тоже; строка оркестратора — про мерж.
    const alt = orchMinutes != null
      ? `после вашего «да» — мерж ${etaMain(orchMinutes)}${orchName ? ` (${orchName})` : ''}` : null;
    return { ...base, main: 'за вами', alt };
  }

  if (orchFresh) {
    const source = `по оценке ${orchName ? genitiveName(orchName) : 'оркестратора'}${orchAgo ? ` · ${orchAgo}` : ''}`;
    const out: BriefEstimateView = {
      ...base, main: etaMain(orchMinutes!), tail: tailFor(est?.etaEndsAt), source,
    };
    if (histSpread && est?.agreement) {
      out.alt = histSpread;
      if (est.agreement === 'within') out.verdict = { text: 'сходится', tone: 'ok' };
      else {
        out.verdict = { text: est.agreement === 'above' ? 'дольше, чем по истории' : 'быстрее, чем по истории', tone: 'warn' };
        out.why = est.why || null;
      }
    }
    return out;
  }

  if (histMinutes != null) {
    const out: BriefEstimateView = {
      ...base, main: etaMain(histMinutes), tail: tailFor(histAt),
      source: `разброс ${spread(hist!)} · ${pastFeatures(hist!.sampleSize)}`,
    };
    if (orchStale) {
      out.alt = `${orchName ?? 'оркестратор'}: ${etaMain(orchMinutes!)}${orchAgo ? ` · ${orchAgo}` : ''}`;
      if (staleLabel) out.verdict = { text: staleLabel, tone: 'warn' };
    }
    return out;
  }

  if (brief.kind === 'queued') return { ...base, main: 'Оценка — после старта', mainMuted: true };
  return { ...base, main: NO_ESTIMATE, mainMuted: true, source: 'прошлых фич для сравнения мало' };
}

// Первый факт строки итогов: «идёт 1 ч 10 мин» по elapsedMinutes; у очереди — «в очереди N» по
// queueWaitMinutes. «0 мин» — не факт (тот же порог round, что у formatDuration); у закрытой —
// нет: её длительность уже в «Итог».
export function briefElapsedFact(brief: FeatureBrief): { label: string; value: string } | null {
  const pos = (m: number | null | undefined): m is number => m != null && Math.round(m) > 0;
  if (brief.kind === 'closed' || brief.kind === 'merged') return null;
  if (brief.kind === 'queued') {
    return pos(brief.queueWaitMinutes) ? { label: 'в очереди', value: formatDuration(brief.queueWaitMinutes) } : null;
  }
  return pos(brief.elapsedMinutes) ? { label: 'идёт', value: formatDuration(brief.elapsedMinutes) } : null;
}

// ── «N мин/ч назад» ────────────────────────────────────────────────────────────────
// Момент → относительная подпись (макет: «обновлено 2 мин назад», «обновлено только
// что»; старше суток — дата «24.10, 18:06», как в подписи голоса закрытой). null —
// даты нет или она нечитаемая.
// Полные минуты (floor), а не общий minutesSince с округлением (W5-R-F3b): «только
// что» = меньше минуты, «N мин назад» = N полных минут — округление подняло бы
// 30 с до «1 мин назад».
export function agoText(iso: string | null, nowMs: number): string | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return null;
  const m = Math.max(0, Math.floor((nowMs - t) / 60000));
  if (m < 1) return 'только что';
  if (m < 60) return `${m} мин назад`;
  if (m < 24 * 60) return `${Math.floor(m / 60)} ч назад`;
  return `${formatDate(iso)}, ${formatClock(iso)}`;
}

// Правый верх строки «Сейчас»: «обновлено N назад» по brief.updatedAt — времени последнего
// события состояния (B6, UpdatedAtOf в FeatureBrief.cs:283), а не по computedAt — моменту
// этого ответа (дефект f2da9354: computedAt всегда свежий, строка всегда читалась «только что»).
// Нет updatedAt (null или поля нет — фича без событий состояния) — строки нет: computedAt не
// подставляется («подставлять „сейчас“ значит врать» — FeatureBrief.cs:280). У закрытой —
// дата закрытия (макет §Поведение): mergedAt от вызывающего приоритетнее; его нет — updatedAt:
// он максимум по всем задачам фичи и как правило совпадает с закрытием (CompletedAt
// закрывающей задачи входит в UpdatedAtOf), но событие позже закрытия дату уводит — отсюда
// приоритет mergedAt.
// Порог — у agoText выше: «только что» ТОЛЬКО когда разница меньше минуты, «N мин назад» —
// полных минут (W5-R-F3b): «обновлено 0 мин назад» не печатается — нуль не число, та же
// болезнь, что «идёт 0 мин» / «в очереди 0 мин».
export function briefUpdatedText(brief: FeatureBrief, mergedAt: string | null, nowMs: number): string | null {
  const iso = (brief.kind === 'closed' || brief.kind === 'merged') ? (mergedAt ?? brief.updatedAt ?? null) : (brief.updatedAt ?? null);
  const ago = agoText(iso, nowMs);
  return ago ? `обновлено ${ago}` : null;
}

// ── Голос оркестратора: метка устаревшего ─────────────────────────────────────────
// Метка только при note.stale (считает бэк): в паузе — «до паузы», иначе — «до смены
// этапа» (макет §«Поведение»; по какой именно причине сработал stale — контракт не
// несёт, выбираем по текущему kind).
export function voiceStaleLabel(note: FeatureBriefNote, kind: FeatureBriefKind): string | null {
  if (!note.stale) return null;
  return kind === 'paused' ? 'до паузы' : 'до смены этапа';
}

// Время в подписи голоса: «14 мин назад»; устаревший — «до паузы, 45 мин назад»
// (макет hold). Текст устаревшего голоса не прячем — только метка и приглушение (приглушение — дело F5).
export function voiceWhenText(note: FeatureBriefNote, kind: FeatureBriefKind, nowMs: number): string {
  const ago = agoText(note.at, nowMs);
  const label = voiceStaleLabel(note, kind);
  if (label) return ago ? `${label}, ${ago}` : label;
  return ago ?? '';
}

// Роль в подписи голоса: «ведёт фичу» / «вела фичу» (макет closed; 'merged' — тоже
// прошедшее время, Ф-1.1).
export function voiceRoleText(kind: FeatureBriefKind): string {
  return (kind === 'closed' || kind === 'merged') ? 'вела фичу' : 'ведёт фичу';
}

// ── Сторож перенесённых слов: слова модели (R-F8) ────────────────────────────────────
// Все статические строки, которые модель может сказать в карточке. Их не должно быть в
// коде блока (сторож — FeatureBriefBlock.test.ts): формулировка живёт здесь, блок только
// вызывает модель. Добавляя в модель новую строку, добавляй её в этот список.
// Кроме списка — слова, по праву принадлежащие блоку и случайно совпадающие с моделью:
// «Дальше» — подпись секции блока: блок печатает ровно «Дальше», без тире; модельный
// префикс error/idle — «Дальше — » (с тире), он в списке: модельная строка «Дальше — …»
// защищена, метка блока со списком не конфликтует. «в очереди» — тег карточки
// («в очереди · N из M»): защищена именно метка тега, а не модельный факт «в очереди N»
// — любой вариант тега с хвостом с ним конфликтует, поэтому фраза целиком вне списка.
// Короткие коннекторы и коды причин вне списка по построению: ' из ' живёт в теге блока
// («в очереди · N из M»), ', затем ' — внутри строки с покрытым префиксом ('После …'),
// 'мерж' — код причины от бэка (строки с ним покрыты целыми фразами списка);
// односимвольные куски вроде 'с ' метод не покрывает.
export const BRIEF_MODEL_WORDS: readonly string[] = [
  // «Сейчас» (briefNowText)
  'Ждёт вашего согласия на мерж в master',
  'Ждёт вашего ответа:',
  'Исполнитель встал', // нет причины и неизвестный код — одна фраза (R-F17 п.5)
  'На паузе —', // оба варианта строки паузы
  'Ждёт своей очереди',
  'В очереди:',
  'Готовится: разрез/план',
  'Ничего не идёт',
  'Смержена в master',
  'Закрыта',
  'шага ', // «Ревью шага 3 (бэк)» — часть running-строки
  // Подстрока «Сейчас» (briefSubText); «фича закрыта» покрывает и «Ничего — фича закрыта»
  'без вас фича стоит',
  'исполнители не запускаются',
  'фича закрыта',
  'ждёт ', // «ждёт N · без вас фича стоит»
  'на паузе ', // «на паузе N · исполнители не запускаются»
  // «Сделано» (briefDoneText)
  'шагов приняты',
  ' шагов', // «План готов · 0 из 4 шагов»
  'План готов',
  'правок',
  // «в master» (F1, masterParts): пометка готовности и факта трекера; покрывает и
  // «Смержена в master» (она и так выше списком), и aria-label «N в master»
  'в master',
  // «Дальше» (briefNextText): «После …» — любой префикс живой строки;
  // «Дальше — » — модельный префикс error/idle (метка блока — без тире, вне списка)
  'После ',
  'Дальше — ',
  // «Оценка»/«Итог» (briefEstimateText): «Оценка» покрывает и «Оценка — после старта»
  'Оценки пока нет',
  'Оценка',
  'Итог',
  'Срок',
  'Готовность',
  'за вами',
  'старт ≈',
  // Задачи в работе (briefActiveView, briefActiveTagLabel)
  'ждёт вашего ответа',
  'Ответить',
  'параллельно:',
  'линия:',
  'в работе',
  'во вкладке',
  'остановлен в',
  '· стоит',
  'открыть задачу',
  'другая задача',
  'другие задачи',
  'других задач',
  'ждёт вас',
  'по трекеру',
  'Ещё не начато',
  'Осталось:',
  'следом · ',
  'сходится',
  'чем по истории',
  'после старта',
  'после вашего',
  'снятия паузы', // «≈ N после снятия паузы»
  'идёт ',
  'шла ',
  '— у ', // «— у {Имя}»
  // Голос оркестратора и «N назад» (voice*, agoText/briefUpdatedText)
  'до паузы',
  'до смены этапа',
  'ведёт фичу',
  'вела фичу',
  'только что',
  'мин назад',
  'ч назад',
  'обновлено',
];
