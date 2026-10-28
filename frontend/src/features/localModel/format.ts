// Чистые функции форматирования вкладки «LLM»: юнит-тесты без React и DOM.
// Все подписи — по спецификации макета (docs/mockups/local-model-stats-panel.html,
// раздел «Спецификация для Киры»): единицы, запятая как десятичный разделитель,
// «—» когда движок значение не отдал.

import type { LocalModelDutyReason, LocalModelJobKind, LocalModelStats } from './api';

// Текст «не сообщает»: с именем движка — «Движок {имя} это не сообщает»,
// без имени — «Движок этого не сообщает» (иначе было бы «Движок движок»).
// Общий helper фичи: и вкладка «LLM», и график «Нагрузка и кэш» говорят одно и то же.
export function notReportedText(engine: string | null | undefined): string {
  const name = engine?.trim();
  return name ? `Движок ${name} это не сообщает` : 'Движок этого не сообщает';
}

// === Числа ===

// Ток/с: «38 ток/с» — целое, без десятичных (движок целыми и отсчитывает)
export function formatTps(v?: number | null): string {
  if (v == null || !Number.isFinite(v)) return '—';
  return `${Math.round(v)} ток/с`;
}

// Долю 0..1 зажать в диапазон: бэк обещает 0..1, но выброс (сбой датчика)
// не должен растягивать полосу или процент
export function clamp01(v: number): number {
  return Math.max(0, Math.min(1, v));
}

// Процент из доли 0..1: «61 %». Бэк отдаёт доли (0.61), не проценты;
// значения вне 0..1 зажимаются (0.5 → «50 %»). Без значения — «—».
export function formatPerc(fraction?: number | null): string {
  if (fraction == null || !Number.isFinite(fraction)) return '—';
  return `${Math.round(clamp01(fraction) * 100)} %`;
}

// Десятичное число по-русски: «21,4»; целое — без хвоста: «24»
function ruNum(n: number): string {
  const r = Math.round(n * 10) / 10;
  return Number.isInteger(r) ? String(r) : r.toFixed(1).replace('.', ',');
}

// Память видеокарты: «21,4/24 ГБ»
export function formatGpuMem(usedMb?: number | null, totalMb?: number | null): string {
  if (usedMb == null || totalMb == null || !Number.isFinite(usedMb) || !Number.isFinite(totalMb)) return '—';
  return `${ruNum(usedMb / 1024)}/${ruNum(totalMb / 1024)} ГБ`;
}

// Мощность: «212/220 Вт»; без лимита карты — «212 Вт»
export function formatPower(w?: number | null, limitW?: number | null): string {
  if (w == null || !Number.isFinite(w)) return '—';
  if (limitW == null || !Number.isFinite(limitW)) return `${Math.round(w)} Вт`;
  return `${Math.round(w)}/${Math.round(limitW)} Вт`;
}

// «не отвечает с 14:32» — из unix-мс (как downSince у бэка); без момента — пусто
export function formatSince(ms?: number | null): string {
  if (ms == null || !Number.isFinite(ms)) return '';
  const d = new Date(ms);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// «обновлено HH:MM:SS» — время последней точки истории (unix-мс, серверные таймштампы,
// не Date.now() клиента: часы рассинхронизированы). Пустая строка — точек нет, честного
// источника времени нет (фразу не показываем).
export function formatUpdatedAt(history?: { t: number }[] | null): string {
  let max = 0;
  for (const h of history ?? []) if (h.t > max) max = h.t;
  if (max <= 0) return '';
  const d = new Date(max);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `обновлено ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

// === Подписи видеокарт ===

// «RTX 3090 · текст» / «RTX 3090 · медиа» — имя из nvidia-smi хоста плюс место работы
const GPU_KIND_LABEL: Record<'text' | 'media', string> = { text: 'текст', media: 'медиа' };
export function gpuLabel(name: string, kind: 'text' | 'media'): string {
  return `${name} · ${GPU_KIND_LABEL[kind]}`;
}

// «карта 0 из 2»
export function gpuOrdinal(index: number, total: number): string {
  return `карта ${index} из ${total}`;
}

// «1 карта / 2 карты / 5 карт» — русское склонение числа видеокарт
export function gpuCountLabel(n: number): string {
  const abs = Math.abs(n) % 100;
  const last = abs % 10;
  if (abs > 10 && abs < 20) return `${n} карт`;
  if (last === 1) return `${n} карта`;
  if (last >= 2 && last <= 4) return `${n} карты`;
  return `${n} карт`;
}

// === Подписи статуса стенда (макет bonsai-toolbar §4) ===
// Бэкенд отдаёт групповой статус; точную подпись разводит фронт по состоянию арбитра:
// 'yielded' + причины — факт уступки («Уступила карту»), 'yielded' без причин с остатком
// паузы — ожидание возврата, 'starting' — загрузка, 'waiting-vram' — ожидание памяти.
// Тон первых трёх — нейтральный: уступка — штатная работа арбитра, не авария (макет §4);
// «ждёт память» — warning: сама не рассосётся. Без арбитра бэкенд 'yielded' не ставит
// (Б3b), поэтому fallback — групповая подпись.

export type StatusTone = 'success' | 'warning' | 'danger' | 'muted';
export interface StatusPill { label: string; tone: StatusTone }

// Точное состояние дежурства из снимка: 'yielded' стенда разводится по arbiter.state и
// причинам; всё остальное — сам статус. Общий источник для пилюли, точки и подсказки.
type YieldPhase = 'yielding' | 'waiting-return' | 'starting' | 'waiting-vram';
function yieldPhase(data: LocalModelStats): YieldPhase {
  const a = data.duty?.arbiter;
  if (a?.state === 'starting') return 'starting';
  if (a?.state === 'waiting-vram') return 'waiting-vram';
  // 'yielded' без причин с названным остатком паузы — ожидание возврата; 0 — конец паузы,
  // модель вот-вот встанет (демо макета с quiet=0). Без остатка (арбитр его не назвал) —
  // групповая подпись: «вот-вот» без названного срока была бы выдуманной точностью
  if (a?.state === 'yielded' && (a.reasons?.length ?? 0) === 0 && a.quietLeftS != null && Number.isFinite(a.quietLeftS)) return 'waiting-return';
  return 'yielding';
}

export function statusPill(data: LocalModelStats): StatusPill {
  switch (data.status) {
    case 'working': return { label: 'Работает', tone: 'success' };
    case 'busy': return { label: 'Занята', tone: 'warning' };
    case 'down': return { label: 'Недоступна', tone: 'danger' };
    case 'notConnected': return { label: 'Не подключена', tone: 'muted' };
    case 'yielded': {
      switch (yieldPhase(data)) {
        case 'starting': return { label: 'Загружается', tone: 'muted' };
        case 'waiting-vram': return { label: 'Ждёт память карты', tone: 'warning' };
        case 'waiting-return': return { label: 'Ждёт возврата', tone: 'muted' };
        default: return { label: 'Уступила карту', tone: 'muted' };
      }
    }
  }
}

// «Арбитр недоступен» (макет §4, хвост ревью Ф2 4): метрики отвечают (статус обычный),
// но /state молчит — состояние не рассосётся само, и развести уступку и аварию сейчас
// нечем. Общий признак точки и подсказки: 'notConnected' (стенд не настроен) под него не
// попадает — предупреждать о ненастроенном нечем.
function arbiterUnreachable(data: LocalModelStats): boolean {
  return (data.status === 'working' || data.status === 'busy') && data.duty?.arbiter?.reachable === false;
}

// Тон точки на сегменте «Bonsai» и на значке рельса (тот же, Ф6): идущая уступка —
// muted, то, что само не рассосётся (память, молчание стенда или арбитра) — warning (макет §4:
// «accent закреплён за „что-то идёт прямо сейчас“», жёлтый — за неотсашивающееся).
export function standDot(data: LocalModelStats): 'muted' | 'warning' | null {
  switch (data.status) {
    case 'yielded':
      return yieldPhase(data) === 'waiting-vram' ? 'warning' : 'muted';
    case 'down':
      return 'warning';
    default:
      return arbiterUnreachable(data) ? 'warning' : null;
    }
}

// Короткая причина для подсказки (колонка 'short' таблицы KINDS макета,
// bonsai-toolbar.html:288): подсказка — одна строка, полные подписи — в «Дежурстве»
// (dutyFormat.ts). Неизвестный вид — короткой формы нет: подсказка остаётся общей,
// без выдуманного суффикса.
const JOB_KIND_SHORT: Record<LocalModelJobKind, string> = {
  image: 'картинка',
  video: 'видео',
  audio: 'звук',
  lora: 'LoRA',
};
function reasonShort(r: LocalModelDutyReason | undefined): string | null {
  switch (r?.kind) {
    case 'comfy':
      // задание с этого сервера и опознанного вида — короткая форма вида;
      // иначе — «ComfyUI» (строка 'alien' макета)
      return r.known === true && r.jobKind ? JOB_KIND_SHORT[r.jobKind] : 'ComfyUI';
    case 'lease':
      return 'аренда';
    case 'gpu':
      return 'процесс';
    default:
      return null;
  }
}

// «м:сс» из остатка секунд — то же форматирование, что formatClockLeft в dutyFormat.ts:
// подсказка рельса и строка «Вернётся» в «Дежурстве» должны называть одно время одинаково.
// 0 → «0:00», «вот-вот» выбирает вызывающий: в подсказке нет «вернётся через 0:00».
function clockLeft(s: number): string {
  const whole = Math.floor(s);
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

// Подсказка сегмента «Bonsai» (title/aria-label) — та же строка, что подсказка рельса
// (макет §2): «вернётся через м:сс» — остаток из последнего снимка, тот же, что в
// «Дежурстве»; живого тиканья между опросами нет — следующий опрос рельса (Ф6)
// принесёт новое значение. null — подсказки нет (стенд работает).
export function standHint(data: LocalModelStats): string | null {
  switch (data.status) {
    case 'yielded': {
      switch (yieldPhase(data)) {
        case 'starting': return 'Bonsai загружается';
        case 'waiting-vram': return 'Bonsai ждёт память карты';
        case 'waiting-return': {
          const left = data.duty?.arbiter?.quietLeftS ?? 0;
          return left > 0 ? `Bonsai вернётся через ${clockLeft(left)}` : 'Bonsai вот-вот вернётся';
        }
        default: {
          const short = reasonShort(data.duty?.arbiter?.reasons?.[0]);
          return short ? `Bonsai уступила карту: ${short}` : 'Bonsai уступила карту';
        }
      }
    }
    case 'down': return 'Bonsai недоступна';
    default:
      // Та же таблица макета §4, что у standDot: подсказка «арбитр не отвечает»
      return arbiterUnreachable(data) ? 'Bonsai: арбитр не отвечает' : null;
  }
}

// Короткий статус пункта «Локальные модели» в мобильном меню «⋯» (колонка
// «Подсказка рельса / меню «⋯»» таблицы §4): одно слово или «через м:сс» — строка
// меню короткая, полные формулировки живут в подсказке рельса (standHint) и в
// «Дежурстве». Тон и наличие статуса — ровно те же, что у точки (standDot):
// 'muted' — штатный курс арбитра, 'warning' — то, что само не рассосётся. null —
// статуса нет: фича в норме или её состояния не знают (опрос второй дорожки живёт
// только у админа с флагом).
export type MenuStatusTone = 'muted' | 'warning';
export interface MenuStatus { text: string; tone: MenuStatusTone }

export function menuStatus(data: LocalModelStats | null): MenuStatus | null {
  if (!data) return null;
  switch (data.status) {
    case 'yielded': {
      switch (yieldPhase(data)) {
        case 'starting': return { text: 'загружается', tone: 'muted' };
        case 'waiting-vram': return { text: 'нет памяти', tone: 'warning' };
        case 'waiting-return': {
          // «через м:сс» из остатка паузы; на нуле — «вот-вот», а не «через 0:00»
          const left = data.duty?.arbiter?.quietLeftS ?? 0;
          return { text: left > 0 ? `через ${clockLeft(left)}` : 'вот-вот', tone: 'muted' };
        }
        default: return { text: 'уступила', tone: 'muted' };
      }
    }
    case 'down': return { text: 'недоступна', tone: 'warning' };
    default:
      return arbiterUnreachable(data) ? { text: 'нет арбитра', tone: 'warning' } : null;
  }
}

// Тон точки на САМОЙ кнопке «⋯» мобильной шапки (Ф9, макет bonsai-toolbar, renderPhone:
// точка на кнопке «Ещё» — тот же bonsaiDot/dotColor, что у рельса). Кнопка и её пункт —
// один сигнал одного состояния: тон и наличие берутся ровно оттуда же, откуда статус
// пункта (menuStatus), а он — из standDot; три индикатора не могут разойтись. null —
// точки нет: состояние в норме или не известно (снимка нет).
export function triggerDot(data: LocalModelStats | null): MenuStatusTone | null {
  return menuStatus(data)?.tone ?? null;
}

// === Строки «Кто сейчас на модели» ===

// Строка списка: чат со скоростью. Чужой чат — «Чат другого пользователя»
// БЕЗ названия и перехода (правило: даже админ не видит чужие названия);
// «Прочие запросы: N» — хвост списка, тоже без перехода.
export type WhoRow =
  | {
      kind: 'chat';
      own: boolean;
      // Чужой чат — заглушка, не название
      title: string;
      project?: string;
      speed: string;
      // gen — «38 ток/с» (C.textSecondary), read — «читает» (C.textMuted),
      // wait — «ждёт» (C.warningText)
      tone: 'gen' | 'read' | 'wait';
      sessionId?: string;
      key: string;
    }
  | { kind: 'others'; count: number; key: string };

// Порядок строк — как в макете: идущие, затем ждущие (в порядке поступления,
// номер очереди стенд не отдаёт). Скорость: генерация — ток/с, чтение промпта —
// «читает», ожидание — «ждёт» без номера. Имя проекта резолвит вызывающий из
// стора проектов по projectId (бэк отдаёт только id, и только для своих чатов);
// для чужого чата проекта нет вовсе.
export function whoRows(
  chats?: LocalModelStats['chats'],
  othersCount?: number,
  projectNameFor?: (projectId?: string) => string | undefined,
): WhoRow[] {
  const rows: WhoRow[] = [];
  (chats ?? []).forEach((c, i) => {
    const key = `chat-${i}`;
    let speed: string;
    let tone: 'gen' | 'read' | 'wait';
    if (c.phase === 'waiting') {
      speed = 'ждёт';
      tone = 'wait';
    } else if (c.phase === 'generating' && c.genTps != null && Number.isFinite(c.genTps)) {
      speed = formatTps(c.genTps);
      tone = 'gen';
    } else {
      // prefill/чтение промпта и прочие фазы без скорости
      speed = 'читает';
      tone = 'read';
    }
    rows.push({
      kind: 'chat',
      own: c.own,
      title: c.own ? (c.title ?? '—') : 'Чат другого пользователя',
      project: c.own ? projectNameFor?.(c.projectId) : undefined,
      speed,
      tone,
      sessionId: c.sessionId,
      key,
    });
  });
  if ((othersCount ?? 0) > 0) rows.push({ kind: 'others', count: othersCount!, key: 'others' });
  return rows;
}
