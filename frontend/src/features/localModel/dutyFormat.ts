// Форматирование блока «Дежурство» (DutyBlock) и строк «Кто сейчас» для движка Bonsai:
// чистые функции, тесты без React и DOM (как format.ts). Все подписи — по макету
// docs/mockups/bonsai-toolbar.md (раздел 5) и контракту §5 (типы — lib/api.ts).
// Строки арбитра (lease, gpu-команда, неизвестный kind) — недоверенные: их возвращаем
// как текст, компонент обязан рендерить их текстовыми узлами, не HTML.

import type {
  LocalModelArbiterMode,
  LocalModelChainLink,
  LocalModelDutyAnswers,
  LocalModelDutyArbiter,
  LocalModelDutyCall,
  LocalModelDutyReason,
  LocalModelJobKind,
  LocalModelStats,
} from './api';
import { formatSince } from './format';

// «—» — нет значения: ноль — это значение, отсутствие — прочерк (конвенция фичи).
const DASH = '—';

// === «Вернётся через м:сс» ===

// Секунды → «1:15» (м:сс); 0 → «вот-вот» (не «0:00»); нет значения → «—».
export function formatClockLeft(s?: number | null): string {
  if (s == null || !Number.isFinite(s)) return DASH;
  if (s <= 0) return 'вот-вот';
  const whole = Math.floor(s);
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

// Строка «Вернётся». null — строки нет: у «Загружается», «Ждёт памяти», «Работает»
// срока возврата нет, и выдумывать его нельзя. Пауз в /state нет (умолчания эпика),
// поэтому при причинах — «после паузы», без чисел; численный срок — только когда
// причин уже нет и арбитр назвал quietLeftS (макет §5).
export function returnLineText(a: LocalModelDutyArbiter | null): string | null {
  if (!a || a.state !== 'yielded') return null;
  if ((a.reasons?.length ?? 0) > 0) return 'после паузы, когда задание закончится';
  if (a.quietLeftS == null) return DASH;
  const clock = formatClockLeft(a.quietLeftS);
  return clock === 'вот-вот' ? 'вот-вот' : `через ${clock}`;
}

// === Причины уступки ===

export type DutyReasonLine = {
  text: string;
  // только 'gpu': командная строка отдельной моно-строкой (pid человеку не нужен)
  mono?: string;
  // переход «→ Медиа»: только для задания ComfyUI с этого сервера (known:true)
  toMedia: boolean;
};

// Вид задания по шву IComfyStats.DescribePromptKind; таблица соответствия — на бэкенде,
// здесь только подписи (макет §5).
const JOB_KIND_LABEL: Record<LocalModelJobKind, string> = {
  image: 'картинка',
  video: 'видео H3',
  audio: 'звук',
  lora: 'обучение LoRA',
};

export function dutyReasonLine(r: LocalModelDutyReason): DutyReasonLine {
  switch (r.kind) {
    case 'comfy': {
      // known:false — prompt_id нет в реестре ЭТОГО инстанса: перехода нет (ComfyUI
      // общий у прода и дева); known:null — не известно, перехода тоже нет
      if (r.known === false) return { text: 'задание ComfyUI не с этого сервера', toMedia: false };
      if (r.known === true) {
        // задание с этого сервера: переход работает и без опознанного вида («Медиа»
        // показывает все задания реестра), но вида в подписи нет
        return { text: r.jobKind ? JOB_KIND_LABEL[r.jobKind] : 'задание ComfyUI', toMedia: true };
      }
      return { text: 'задание ComfyUI', toMedia: false };
    }
    case 'lease':
      // текст присылает кто угодно из сети (≤64 знака, обрезал сервер): только как
      // текст, в кавычках-ёлочках, без моно и без перехода
      return { text: `аренда: «${r.raw}»`, toMedia: false };
    case 'gpu': {
      // raw = «pid:командная строка»: pid наружу не отдаётся, команда — моно, многоточие
      const i = r.raw.indexOf(':');
      return { text: 'чужой процесс на карте', mono: i >= 0 ? r.raw.slice(i + 1) : r.raw, toMedia: false };
    }
    default:
      // значение kind вне списка — неизвестный вид: raw как текст, без перехода
      return { text: r.raw, toMedia: false };
  }
}

// Несколько причин — первая и «и ещё N»: остальные не перечисляем (макет §5).
export function dutyReasonsSummary(reasons?: LocalModelDutyReason[] | null): { main: DutyReasonLine | null; more: number } {
  const list = reasons ?? [];
  if (list.length === 0) return { main: null, more: 0 };
  return { main: dutyReasonLine(list[0]), more: list.length - 1 };
}

// === Цепочка «Bonsai → страховка → облако» ===

// Цепочка — ТИПИЧНЫЙ путь (контракт): состав плашек фиксирован макетом. Третье звено
// подписано «облако»: это не обязательно claude (разрез §2), title объясняет.
export const CHAIN_ORDER: LocalModelChainLink[] = ['bonsai', 'insurance', 'claude'];
export const CHAIN_LABEL: Record<LocalModelChainLink, string> = {
  bonsai: 'Bonsai',
  insurance: 'MiniMax',
  claude: 'облако',
};
export const CHAIN_SUFFIX: Record<LocalModelChainLink, string> = {
  bonsai: '',
  insurance: ' · страховка',
  claude: '',
};
export const CHAIN_LAST_TITLE = 'Финальный шаг: модель места или слота, обычно claude';

// «Звено сейчас» — куда пойдёт СЛЕДУЮЩИЙ вызов места (живого знания о последнем
// ответе нет, макет §4): модель активна → bonsai; уступила/недоступна → страховка.
export function currentChainLink(status: LocalModelStats['status']): LocalModelChainLink {
  return status === 'working' || status === 'busy' ? 'bonsai' : 'insurance';
}

export function nextChainHint(link: LocalModelChainLink): string {
  return link === 'bonsai' ? 'Следующий вызов места пойдёт на Bonsai' : 'Следующий вызов места пойдёт на MiniMax';
}

// === Ответы мест за час ===

const HOUR_MS = 60 * 60_000;

export type AnswersSegment = {
  key: 'bonsai' | 'insurance' | 'claude' | 'missed';
  label: string;
  // цвет берёт компонент: success / info / accent / danger
  count: number;
  // ширина сегмента полосы в %; null — полосой не рисуется (нулевой остаётся в легенде
  // с числом 0; при нулевом итоге — null у всех: знаменателя нет, долей нет)
  widthPerc: number | null;
};

// Доли считаются по суммам четырёх корзин окна (это все вызовы окна). Нулевой итог —
// не «нет данных» (нет данных — весь блок null): это работа без ответов, и доля при
// нулевом знаменателе — «—», а не NaN.
export function answersSummary(a: LocalModelDutyAnswers | null): { total: number; segments: AnswersSegment[] } {
  const src = a ?? { bonsai: 0, insurance: 0, claude: 0, missed: 0, sinceAt: 0 };
  const nums = [src.bonsai, src.insurance, src.claude, src.missed].map(n => (Number.isFinite(n) ? Math.max(0, n) : 0));
  const total = nums[0] + nums[1] + nums[2] + nums[3];
  const labels = ['Bonsai', 'MiniMax', 'облако', 'без ответа'];
  const keys: AnswersSegment['key'][] = ['bonsai', 'insurance', 'claude', 'missed'];
  const segments = keys.map((key, i) => ({
    key,
    label: labels[i],
    count: nums[i],
    widthPerc: total > 0 && nums[i] > 0 ? (nums[i] / total) * 100 : null,
  }));
  return { total, segments };
}

// Значение строки «Кто ответил», когда доли не считаются: «—» (не NaN).
// null — строки нет, вместо неё полоса с легендой.
export function answersDash(a: LocalModelDutyAnswers | null): string | null {
  return answersSummary(a).total === 0 ? DASH : null;
}

// Заголовок и подсказка блока: сервер работает меньше часа (sinceAt ближе к now, чем
// на час) — «Ответы мест · N мин» и «сервер перезапущен в HH:MM». Часы Video и CCS
// расходятся (контракт): if sinceAt в будущем относительно now — окно считаем полным.
export function answersWindowTitle(a: LocalModelDutyAnswers, now: number): { title: string; hint?: string } {
  const span = now - a.sinceAt;
  if (Number.isFinite(a.sinceAt) && Number.isFinite(now) && span >= 0 && span < HOUR_MS) {
    const min = Math.max(1, Math.round(span / 60_000));
    const since = formatSince(a.sinceAt);
    return { title: `Ответы мест · ${min} мин`, hint: since ? `сервер перезапущен в ${since}` : 'сервер недавно перезапущен' };
  }
  return { title: 'Ответы мест за час' };
}

// === Счётчики уступок ===

// «Уступила · вернулась 14 · 14» + период. Периода «за час» у арбитра нет: счётчики
// ведут с начала периода (sinceAt). При нулевых счётчиках метка времени выглядела бы
// как поломка, поэтому — «с запуска арбитра» (контракт, lib/api.ts); при ненулевых —
// «с HH:MM». Нет данных о счётчиках — строки нет (null).
export function dutyCounters(a: LocalModelDutyArbiter | null): { value: string; period: string } | null {
  if (!a || a.yields == null || a.returns == null) return null;
  const value = `${a.yields} · ${a.returns}`;
  if (a.yields === 0 && a.returns === 0) return { value, period: 'с запуска арбитра' };
  const since = formatSince(a.sinceAt);
  return { value, period: since ? `с ${since}` : 'с запуска арбитра' };
}

// Десятичное по-русски с единицей: «0,2 с»; целое — без хвоста: «6 с».
function ruSeconds(v?: number | null): string {
  if (v == null || !Number.isFinite(v)) return DASH;
  const r = Math.round(v * 10) / 10;
  return `${Number.isInteger(r) ? String(r) : r.toFixed(1).replace('.', ',')} с`;
}

// «Уступает · возвращается 0,2 с · 6,1 с» — последний раз; если значений нет — строки
// нет (макет §5). Одно из двух без значения — «—» на своём месте.
export function dutySwitchTimes(a: LocalModelDutyArbiter | null): string | null {
  if (!a || (a.lastYieldS == null && a.lastReturnS == null)) return null;
  return `${ruSeconds(a.lastYieldS)} · ${ruSeconds(a.lastReturnS)}`;
}

// Чип режима карты в шапке блока. mode:null (в т.ч. без арбитра) — чипа нет.
export const DUTY_MODE_CHIP: Record<LocalModelArbiterMode, string> = {
  exclusive: 'карта целиком',
  shared: 'рядом с ComfyUI',
};

// === «Кто сейчас» для Bonsai: строки из callsNow ===

// Это журнал вызовов мест, а не чаты: строка не кликабельна, владельца и промпта нет
// (разрез v2, §4). link:null — «ждёт в очереди»: вызов уже в журнале, звена ещё нет.
export type DutyCallRow = {
  key: string;
  place: string;
  // «Bonsai» / «MiniMax · страховка» / «облако»; для queued — «ждёт в очереди»
  linkText: string;
  queued: boolean;
  // «2,1 с»; '' — длительности нет (вызов ещё не начался): значения не показываем
  duration: string;
};

export function dutyWhoRows(calls?: LocalModelDutyCall[] | null): DutyCallRow[] {
  return (calls ?? []).map((c, i) => {
    const queued = c.link == null;
    return {
      key: `call-${i}`,
      place: c.place,
      // c.link проверяется здесь, а не через queued: TS не сужает тип по булевой переменной;
      // звено — полным именем, как в строках макета («MiniMax · страховка»)
      linkText: c.link == null ? 'ждёт в очереди' : CHAIN_LABEL[c.link] + CHAIN_SUFFIX[c.link],
      queued,
      duration: c.durationS != null && Number.isFinite(c.durationS) ? ruSeconds(c.durationS) : '',
    };
  });
}
