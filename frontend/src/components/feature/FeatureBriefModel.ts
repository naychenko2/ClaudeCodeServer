// Перевод ключей brief в слова (волна 6 «Сводка состояния фичи», шаг F2).
//
// ЧИСТЫЙ модуль: ни DOM, ни запросов, ни пересчёта — только текст из полей `brief`
// (разрез п. 7: «своих у фронта только перевод ключей стадий в слова и "N ч назад"
// от at»; kind/stale/next/estimate фронт НЕ выводит). Форматтеры времени — общие,
// из FeatureStatsBits.ts (тот же «N мин/ч», что в ленте); линия — laneWord из roleLabel.
//
// Строки: макет docs/mockups/feature-summary-1.md (состояния §2 и §«Токены поимённо»);
// для kind, которых в макете нет (error/draft/idle) — строки разреза п. 4.
// Оценки нет → нейтральное «Оценки пока нет» (решение плана: макет и разрез называют
// разные причины, контракт причину не несёт). «≈ 0» не печатается нигде.

import { formatClock, formatDate, formatDuration, stageLabel } from './FeatureStatsBits';
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
      const parts = [stageLabel(n.stage)];
      if (n.stepRef) parts.push(`шага ${n.stepRef}`);
      const lane = laneWord(n.lane);
      if (lane) parts.push(`(${lane})`);
      const name = n.personaId ? opts.nameOf?.(n.personaId) : null;
      if (name) parts.push(`— у ${genitiveName(name)}`);
      return parts.join(' ');
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
  // closed: «шла 6 ч 40 мин».
  main: string;
  // Второстепенный факт: «идёт N» по готовому elapsedMinutes (разбивка «из них ждёт
  // вас / пауза» — вне контракта, не показываем — решение плана).
  fact: string | null;
}

const NO_ESTIMATE = 'Оценки пока нет';

export function briefEstimateText(brief: FeatureBrief): BriefEstimateView {
  const elapsed = brief.elapsedMinutes != null ? formatDuration(brief.elapsedMinutes) : null;

  if (brief.kind === 'closed') {
    // Итог: «шла 6 ч 40 мин» по elapsedMinutes; «по плану было ≈ 5 ч» — v2, не делаем.
    return { header: 'Итог', main: elapsed ? `шла ${elapsed}` : NO_ESTIMATE, fact: null };
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
  return { header: 'Оценка', main, fact: elapsed ? `идёт ${elapsed}` : null };
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
