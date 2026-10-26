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
import { laneWord } from '../../lib/roleLabel';
import type { FeatureBrief, FeatureBriefKind, FeatureBriefNote } from '../../lib/featurePage';

// Резолвер имени персоны по personaId (данные вызывают: usePersonas/getPersonaById).
// null — имя неизвестно: часть строки с именем опускается, строка остаётся валидной
// (тот же приём, что personaName в FeatureProdCards.ts).
export type PersonaNameResolver = (personaId: string) => string | null;

// Дополнительные данные строки «Сейчас» вне brief:
// mergedAt — момент мержа из run.summary.mergedAt (в brief его нет; для строки closed).
export interface BriefTextOptions {
  nameOf?: PersonaNameResolver;
  mergedAt?: string | null;
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
    case 'error':
      // Разрез п. 4: «Исполнитель встал: <причина>» из Attention.Reason.
      return brief.reason ? `Исполнитель встал: ${brief.reason}` : 'Исполнитель встал';
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
    case 'closed': {
      // «Смержена в master 24.10 в 18:05» (макет) — дата мержа от вызывающего (run.summary);
      // без даты — «Закрыта» (разрез п. 4).
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
export function briefSubText(brief: FeatureBrief, nowMs: number): string | null {
  const since = brief.now?.since;
  switch (brief.kind) {
    case 'running': {
      if (!since) return null;
      const dur = minutesSince(since, nowMs);
      return dur != null ? `с ${formatClock(since)} · ${formatDuration(dur)}` : `с ${formatClock(since)}`;
    }
    case 'waiting_you': {
      if (!since) return null;
      const dur = minutesSince(since, nowMs);
      return dur != null ? `ждёт ${formatDuration(dur)} · без вас фича стоит` : 'без вас фича стоит';
    }
    case 'paused': {
      if (!since) return null;
      const dur = minutesSince(since, nowMs);
      return dur != null ? `на паузе ${formatDuration(dur)} · исполнители не запускаются` : 'исполнители не запускаются';
    }
    case 'closed':
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
  if (brief.kind === 'closed') return 'Ничего — фича закрыта';

  // Строка про мерж — только когда мерж действительно ожидается: причина «мерж»
  // (бэк кладёт такой код) или следующий шаг — merge (макет wait). При другой
  // причине — общий префикс ниже; при пустом next строки нет (R-F2: не выдумывать мерж).
  if (brief.kind === 'waiting_you' && (brief.reason === 'мерж' || brief.next[0]?.stage === 'merge')) {
    return 'После вашего «да» — мерж в master';
  }

  let prefix: string;
  switch (brief.kind) {
    case 'running': {
      // «После ревью — …»: активная стадия из now (ее нет — префикса нет).
      if (!brief.now) return null;
      prefix = `После ${stageWord(brief.now.stage)}`;
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

// ── «Оценка» ────────────────────────────────────────────────────────────────────────
export interface BriefEstimateView {
  // Подпись блока: «Оценка»; у закрытой меняется на «Итог» (макет §2 closed).
  header: string;
  // Главная строка: «≈ 2 ч 30 мин осталось» / «Осталось — за вами» / «Оценки пока нет» /
  // queued: «Оценка — после старта» / closed: «шла 6 ч 40 мин».
  main: string;
  // true — main не значение, а обещание: блок красит её приглушённым (макет queue:
  // «Оценка — после старта» (C.textMuted)).
  mainMuted: boolean;
  // Второстепенный факт: «идёт N» по готовому elapsedMinutes; для queued — «в очереди N»
  // (макет) только по отдельному полю queueWaitMinutes (R-F8): у queued нет work-интервалов,
  // число из elapsedMinutes для него всегда 0. Разбивка «из них ждёт вас / пауза» — вне
  // контракта, не показываем — решение плана.
  fact: string | null;
}

const NO_ESTIMATE = 'Оценки пока нет';

export function briefEstimateText(brief: FeatureBrief): BriefEstimateView {
  // «идёт 0 мин» — не факт: ноль работы (или меньше округляемой минуты) не число (R-F8,
  // та же болезнь, что «в очереди 0 мин»). Порог — тот же round, что у formatDuration.
  const elapsed = brief.elapsedMinutes != null && Math.round(brief.elapsedMinutes) > 0
    ? formatDuration(brief.elapsedMinutes) : null;

  if (brief.kind === 'closed') {
    // Итог: «шла 6 ч 40 мин» по elapsedMinutes; «по плану было ≈ 5 ч» — v2, не делаем.
    return { header: 'Итог', main: elapsed ? `шла ${elapsed}` : NO_ESTIMATE, mainMuted: false, fact: null };
  }

  if (brief.kind === 'queued') {
    // Для очереди оценки нет не от нехватки данных, а потому что фича не началась
    // (решение Софьи, R-F2→F5): строка — обещание, не значение (макет: без «осталось»,
    // приглушённая). Факт «в очереди N» — только по отдельному полю контракта
    // queueWaitMinutes (бэк-шаг B8; R-F8): elapsedMinutes — объединение work-интервалов
    // (бэк: timeline.Totals.WorkWallMinutes), а у queued фичи их нет — число из него
    // всегда 0 и для очереди не используется никогда.
    const qw = brief.queueWaitMinutes;
    return { header: 'Оценка', main: 'Оценка — после старта', mainMuted: true, fact: qw != null ? `в очереди ${formatDuration(qw)}` : null };
  }

  const est = brief.estimate;
  let main: string;
  if (!est) {
    main = NO_ESTIMATE;
  } else if (brief.kind === 'waiting_you') {
    // Остальное — за человеком (макет wait); число оценки за ним не стоит.
    main = 'Осталось — за вами';
  } else if (est.remainingMinutes < 1) {
    // «≈ 0» не печатается ни в одном выходе (критерий F2).
    main = 'меньше минуты осталось';
  } else if (brief.kind === 'paused') {
    main = `≈ ${formatDuration(est.remainingMinutes)} после снятия паузы`;
  } else {
    main = `≈ ${formatDuration(est.remainingMinutes)} осталось`;
  }
  return { header: 'Оценка', main, mainMuted: false, fact: elapsed ? `идёт ${elapsed}` : null };
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

// Правый верх строки «Сейчас»: «обновлено N мин назад» по summary/brief.computedAt
// (макет §2; у закрытой вызывающий ставит дату сам — не эту строку).
export function updatedText(iso: string | null, nowMs: number): string | null {
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

// Роль в подписи голоса: «ведёт фичу» / «вела фичу» (макет closed).
export function voiceRoleText(kind: FeatureBriefKind): string {
  return kind === 'closed' ? 'вела фичу' : 'ведёт фичу';
}

// ── Сторож перенесённых слов: слова модели (R-F8) ────────────────────────────────────
// Все статические строки, которые модель может сказать в карточке. Их не должно быть в
// коде блока (сторож — FeatureBriefBlock.test.ts): формулировка живёт здесь, блок только
// вызывает модель. Добавляя в модель новую строку, добавляй её в этот список.
// Кроме списка — слова, по праву принадлежащие блоку и случайно совпадающие с моделью:
// «Дальше» (подпись секции; префикс модели для error/idle — то же слово) и «в очереди»
// (тег карточки; факт модели «в очереди N» — другая строка).
export const BRIEF_MODEL_WORDS: readonly string[] = [
  // «Сейчас» (briefNowText)
  'Ждёт вашего согласия на мерж в master',
  'Ждёт вашего ответа:',
  'Исполнитель встал',
  'На паузе —', // оба варианта строки паузы
  'Ждёт своей очереди',
  'В очереди:',
  'Готовится: разрез/план',
  'Ничего не идёт',
  'Смержена в master',
  'Закрыта',
  // Подстрока «Сейчас» (briefSubText); «фича закрыта» покрывает и «Ничего — фича закрыта»
  'без вас фича стоит',
  'исполнители не запускаются',
  'фича закрыта',
  // «Сделано» (briefDoneText)
  'шагов приняты',
  'План готов',
  'правок',
  // «Дальше» (briefNextText): «После …» — любой префикс живой строки
  'После ',
  // «Оценка»/«Итог» (briefEstimateText): «Оценка» покрывает и «Оценка — после старта»
  'Оценки пока нет',
  'Оценка',
  'Итог',
  'Осталось — за вами',
  'осталось', // «меньше минуты осталось», «≈ N осталось»
  'снятия паузы', // «≈ N после снятия паузы»
  'идёт ',
  'шла ',
  '— у ', // «— у {Имя}»
  // Голос оркестратора и «N назад» (voice*, agoText/updatedText)
  'до паузы',
  'до смены этапа',
  'ведёт фичу',
  'вела фичу',
  'только что',
  'мин назад',
  'ч назад',
  'обновлено',
];
