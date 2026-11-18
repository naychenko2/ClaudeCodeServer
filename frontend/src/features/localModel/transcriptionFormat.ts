// View-model верхней части вкладки «Встречи» (Ф3): плоский снимок (контракт Ф1) → тексты
// и флаги четырёх блоков (баннер, конвейер, «Сейчас», очередь). Чистые функции: время
// приходит параметром (тест проверяет «23 мин назад» без Date.now()).
//
// Уровень тревоги (level) приходит со снимком — своих порогов («Завис?» 5 мин) здесь
// нет. «Завис?» и «Остановлен» — интерпретация данного бэком level + phase, а не новый
// вывод: «danger» при живом процессе без севших зависимостей — это ровно тот «stale»,
// который посчитал бэкенд (см. TranscriptionStateReader.level); «warn» без расхождения
// Dify — ровно то «Пишет в не туда». Отдельных сигналов по полям плоский контракт не
// несёт (у незачемённого бэка они есть: pipeline.level / outputLevel) — при сверке
// контракта эти две строки заменяются прямыми полями.

import type { TranscriptionSnapshot } from './types';

const MINUTE = 60_000;

// === Русские согласования числительных (для «4 встречи не попали в Dify») ===
function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

// «23 мин», «1 ч 5 мин» — для «не отчитывался …»
export function durationWords(mins: number): string {
  if (mins < 60) return `${mins} ${plural(mins, 'минуту', 'минуты', 'минут')}`;
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  const hs = `${h} ${plural(h, 'час', 'часа', 'часов')}`;
  return m ? `${hs} ${m} ${plural(m, 'минуту', 'минуты', 'минут')}` : hs;
}

// «8 с назад», «23 мин назад», «4 ч 31 мин назад» (макет: age). Отрицательная разница
// (часовые пояса) — «только что», без «-3 с назад».
export function agoText(fromMs: number, nowMs: number): string {
  const d = nowMs - fromMs;
  if (d < 5_000) return 'только что';
  const s = Math.floor(d / 1000);
  if (s < 60) return `${s} с назад`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} ${plural(m, 'минуту', 'минуты', 'минут')} назад`;
  const h = Math.floor(m / 60);
  const rm = m % 60;
  const hs = `${h} ${plural(h, 'час', 'часа', 'часов')} назад`;
  return rm ? `${h} ч ${rm} мин назад` : hs;
}

// «6 минут», «4 ч 31 мин» — сколько файл ждёт (макет: «ждёт 6 мин»), без «назад»
export function sinceWords(fromMs: number, nowMs: number): string {
  const d = Math.max(0, nowMs - fromMs);
  const s = Math.floor(d / 1000);
  if (s < 60) return `${s} с`;
  const m = Math.floor(s / 60);
  if (m < 60) return durationWords(m);
  const h = Math.floor(m / 60);
  const rm = m % 60;
  return rm ? `${h} ч ${rm} мин` : durationWords(h * 60);
}

// «3 мин 12 с», «48 с», «26 мин» — длительность работы файла (durationMin — минуты,
// как их считает бэкенд: (now − updated))
export function durationText(min: number): string {
  const total = Math.max(0, Math.round(min * 60));
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m === 0) return `${s} с`;
  return s ? `${m} мин ${s} с` : `${m} мин`;
}

// «07:12» — местное время (для «не обрабатываются с 07:12»)
function clockText(ms: number): string {
  const d = new Date(ms);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// Строки сверки с дефицитом (local > inDify); числа, которых нет (v1: inDify=null), — не строки
function difyDeficient(s: TranscriptionSnapshot): { folder: string; local: number; inDify: number }[] {
  return (s.dify ?? [])
    .filter(r => typeof r.local === 'number' && typeof r.inDify === 'number' && r.local > r.inDify)
    .map(r => ({ folder: r.folder, local: r.local as number, inDify: r.inDify as number }));
}

// === Вид-модель ===
export type BadgeSemantic = 'accent' | 'success' | 'warning' | 'danger' | 'neutral';

export interface TransBannerView { tone: 'warning' | 'danger'; title: string; body: string }

export interface TransPipelineView {
  badge: { tone: BadgeSemantic; label: string };
  updatedText: string;
  // «Завис?»/«Остановлен»: строка «Обновлено» краснеет (макет ageLv t-danger)
  updatedAlarm: boolean;
  whereText: string;
  // «pid 41233»; null — pid нет, компонент рисует «нет процесса» курсивом (макет .na)
  pidText: string | null;
  writesText: string;
  // жёлтая строка «не та машина» (макет t-warn)
  writesWarn: boolean;
}

export interface TransStageStepView { label: string; state: 'done' | 'cur' | 'todo' }

export type TransNowView =
  | { kind: 'empty'; note: string }
  | {
    kind: 'active';
    file: string;
    folder: string | null;
    steps: TransStageStepView[];
    runningText: string | null;
    batchText: string | null;
  };

export interface TransQueueFileView { file: string; folder: string | null; waitText: string }

export interface TransQueueView {
  asrCount: number;
  asrNote: string;
  sumCount: number;
  sumNote: string;
  files: TransQueueFileView[];
}

// === Низ вкладки (Ф4): строка сбоя/встречи, строка сверки, строка зависимости ===

// Строка «Сбоев за сутки» и «Последних встреч» одна по составу (макет .mt): имя, папка,
// время, длина, спикеры, комментарий. Сбой не открывается (openable=false, красная
// строка); встреча открывается, только когда mdPath в снимке есть.
export interface TransMeetingRowView {
  id: string; // ключ строки (folder+file); его передают openMeetingNote (openMeetingNote.ts)
  name: string; // имя без расширения; полное имя — в title
  file: string;
  folder: string | null;
  timeText: string; // «11:42» сегодня, «вчера 18:05» вчера (окно бэка — 24 ч, старше нет)
  lengthText: string | null; // «47 мин»; минут нет — строки нет
  speakersText: string | null; // «4 спикера»; нет/0 — строки нет
  comment: string | null;
  openable: boolean;
}

// Строка сверки с Dify. inDify не число (бэк не смог прочитать список документов) —
// строки нет вовсе: дефицит из неизвестного не считается (тот же фильтр, что у
// баннера расхождения выше). deficit — local − inDify > 0; «сходится» — равны.
export interface TransDifyRowView {
  folder: string; // без общего префикса «Video-» (узкая лента, proposal.md)
  local: number;
  inDify: number;
  deficit: number | null; // null — плашки дефицита нет (равны или в Dify больше)
}

// Строка зависимости: точка и слово. Подписи известных ключей — по макету;
// незнакомый ключ (бэк добавит) показывается как есть, без выдуманного имени.
export const DEP_NAMES: Record<string, string> = {
  asr: 'Распознавание', summary: 'Сводки', dify: 'Dify', share: 'Сетевая папка',
};
export const DEP_WORDS: Record<'ok' | 'slow' | 'down' | 'unknown', string> = {
  ok: 'в норме', slow: 'медленно', down: 'не отвечает', unknown: 'не проверялось',
};

export interface TransDepRowView {
  key: string;
  name: string;
  word: string;
  state: 'ok' | 'slow' | 'down' | 'unknown';
}

export interface TransTabView {
  banner: TransBannerView | null;
  pipeline: TransPipelineView;
  now: TransNowView;
  queue: TransQueueView;
  failures: TransMeetingRowView[];
  meetings: TransMeetingRowView[];
  difyRows: TransDifyRowView[];
  difyAgeText: string | null; // «N мин назад» по checkedAt; нет — строки нет
  deps: TransDepRowView[];
  // «Завис?»/«Остановлен»: значения «Сейчас» и «Очереди» гаснут до C.textMuted,
  // «идёт» краснеет. При danger по севшей зависимости — НЕ гаснет (макет «Сбои»:
  // конвейер жив, значения настоящие).
  stale: boolean;
}

const STAGE_LABELS = ['Распознавание', 'Сводка', 'В Dify'];

// level вне «warn»/«danger» (ok, unknown, поля нет) — тревоги нет: ни баннера, ни гашения
type Alarm = 'ok' | 'warn' | 'danger';
function alarmOf(s: TranscriptionSnapshot): Alarm {
  return s.level === 'warn' || s.level === 'danger' ? s.level : 'ok';
}

function depsDown(s: TranscriptionSnapshot): boolean {
  return (s.deps ?? []).some(d => d.state === 'down');
}

export function transTabView(s: TranscriptionSnapshot, nowMs: number): TransTabView {
  const level = alarmOf(s);
  const stopped = s.phase === 'stopped';
  // «Завис?» = danger при не-остановленном конвейере без севших зависимостей: по логике
  // бэка (TranscriptionStateReader) именно так и получается «stale»
  const hung = level === 'danger' && !stopped && !depsDown(s);
  const stale = stopped || hung;

  // === Баннер: текст строго из proposal.md; свой текст не сочиняем.
  // danger по севшей зависимости — текста в предложении нет: причину видит блок
  // «Зависимости» (Ф4), баннера нет. warn без расхождения Dify — «Пишет в не туда»:
  // это жёлтая строка в карточке конвейера, не баннер. ===
  let banner: TransBannerView | null = null;
  if (level === 'danger') {
    if (stopped) {
      const since = typeof s.updatedAt === 'number' ? ` с ${clockText(s.updatedAt)}` : '';
      banner = {
        tone: 'danger',
        title: 'Сервис transcription-supervisor остановлен',
        body: `Новые записи не обрабатываются${since}. Запустить можно на worker: systemctl --user start transcription-supervisor.`,
      };
    } else if (hung && typeof s.updatedAt === 'number') {
      const mins = Math.max(1, Math.round((nowMs - s.updatedAt) / MINUTE));
      banner = {
        tone: 'danger',
        title: `Конвейер не отчитывался ${durationWords(mins)}`,
        body: 'state.json обновляется каждые ~10 с. Похоже, процесс завис на файле ниже — загляните в журнал сервиса.',
      };
    }
  } else if (level === 'warn') {
    const rows = difyDeficient(s);
    if (rows.length) {
      const total = rows.reduce((a, r) => a + r.local - r.inDify, 0);
      banner = {
        tone: 'warning',
        title: `${total} ${plural(total, 'встреча', 'встречи', 'встреч')} не ${plural(total, 'попала', 'попали', 'попало')} в Dify`,
        body: rows.map(r => `${r.folder}: локально ${r.local}, в Dify ${r.inDify}`).join('; '),
      };
    }
  }

  // === Конвейер ===
  const badge = stopped
    ? { tone: 'danger' as const, label: 'Остановлен' }
    : hung
      ? { tone: 'danger' as const, label: 'Завис?' }
      : s.phase === 'batch'
        ? { tone: 'accent' as const, label: 'Идёт пачка' }
        : s.phase === 'idle'
          ? { tone: 'success' as const, label: 'Ожидание' }
          : { tone: 'neutral' as const, label: 'нет данных' };

  const updatedText = typeof s.updatedAt === 'number' ? agoText(s.updatedAt, nowMs) : '—';

  const whereText = s.host ?? '—';
  const pidText = typeof s.pid === 'number' ? `pid ${s.pid}` : null;

  const outParts = [s.outputHost, s.outputDir].filter((x): x is string => typeof x === 'string' && x.length > 0);
  const writesWarn = level === 'warn' && !difyDeficient(s).length;
  const writesText = outParts.length
    ? outParts.join(' · ') + (writesWarn ? ' — не та машина' : '')
    : '—';

  // === Сейчас ===
  let now: TransNowView;
  if (typeof s.file !== 'string' || s.file.length === 0) {
    // Пустое состояние: когда закончилась последняя работа — по последней завершённой
    // встрече (своего поля «конец пачки» контракт не несёт)
    const last = (s.recent ?? [])[0];
    const note = stopped
      ? 'Процесса нет.'
      : last
        ? `Последняя завершена в ${clockText(last.at)}. Новые записи из сетевой папки конвейер подхватит сам.`
        : 'Новые записи из сетевой папки конвейер подхватит сам.';
    now = { kind: 'empty', note };
  } else {
    const idx = s.stage === 'asr' ? 0 : s.stage === 'summary' ? 1 : s.stage === 'dify' ? 2 : -1;
    const steps = STAGE_LABELS.map((label, i) => ({
      label,
      state: i < idx ? ('done' as const) : i === idx ? ('cur' as const) : ('todo' as const),
    }));
    const runningText = typeof s.durationMin === 'number'
      ? durationText(s.durationMin)
      : typeof s.stageStartedAt === 'number'
        ? durationText((nowMs - s.stageStartedAt) / MINUTE)
        : null;
    // Место в пачке — только когда есть индекс (в v1 бэкенд его не несёт)
    const batchText = typeof s.batchIndex === 'number' && typeof s.batchSize === 'number'
      ? `пачка ${s.batchIndex} из ${s.batchSize}`
      : null;
    now = { kind: 'active', file: s.file, folder: s.folder ?? null, steps, runningText, batchText };
  }

  // === Очередь ===
  const asrQueue = s.asrQueue ?? [];
  const asrCount = asrQueue.length;
  const sumCount = typeof s.summaryQueue === 'number' ? s.summaryQueue : 0;
  const queue: TransQueueView = {
    asrCount,
    asrNote: asrCount ? 'в сетевой папке' : 'всё разобрано',
    sumCount,
    sumNote: sumCount ? 'транскрипт готов' : 'всё разобрано',
    files: asrQueue.map(q => ({
      file: q.file,
      folder: q.folder ?? null,
      waitText: typeof q.since === 'number' ? `ждёт ${sinceWords(q.since, nowMs)}` : 'ждёт',
    })),
  };

  // === Сбои и встречи (одна форма строки, разные флаги; порядок — как в снимке) ===
  const rowOf = (r: { at: number; folder?: string | null; file: string; minutes?: number | null }, openable: boolean): TransMeetingRowView => ({
    id: `${r.folder ?? ''}/${r.file}`, // ключ строки журнала: своего id снимок не несёт
    name: meetingName(r.file),
    file: r.file,
    folder: r.folder ?? null,
    timeText: meetingTimeText(r.at, nowMs),
    lengthText: typeof r.minutes === 'number' ? meetingLengthText(r.minutes) : null,
    speakersText: null,
    comment: null,
    openable,
  });
  const failures: TransMeetingRowView[] = (s.fails ?? []).map(f => ({
    ...rowOf(f, false),
    comment: typeof f.comment === 'string' && f.comment.length > 0 ? f.comment : null,
  }));
  const meetings: TransMeetingRowView[] = (s.recent ?? []).map(m => ({
    ...rowOf(m, typeof m.mdPath === 'string' && m.mdPath.length > 0),
    speakersText: typeof m.speakers === 'number' && m.speakers > 0
      ? `${m.speakers} ${plural(m.speakers, 'спикер', 'спикера', 'спикеров')}`
      : null,
    comment: typeof m.comment === 'string' && m.comment.length > 0 ? m.comment : null,
  }));

  // === Сверка с Dify ===: строки без inDify нет (см. TransDifyRowView); папки без общего
  // префикса «Video-» — в узкой ленте имена папок остаются читаемыми (proposal.md)
  const difyRows: TransDifyRowView[] = (s.dify ?? [])
    .filter(r => typeof r.local === 'number' && typeof r.inDify === 'number')
    .map(r => ({
      folder: r.folder.startsWith('Video-') ? r.folder.slice('Video-'.length) : r.folder,
      local: r.local, inDify: r.inDify,
      deficit: r.local > r.inDify ? r.local - r.inDify : null,
    }));
  const difyAgeText = typeof s.checkedAt === 'number' ? agoText(s.checkedAt, nowMs) : null;

  // === Зависимости ===: порядок — как в снимке; значение вне множества — «не проверялось»
  const KNOWN_DEP = ['ok', 'slow', 'down', 'unknown'] as const;
  const deps: TransDepRowView[] = (s.deps ?? []).map(d => {
    const state: 'ok' | 'slow' | 'down' | 'unknown' = (KNOWN_DEP as readonly string[]).includes(d.state) ? d.state : 'unknown';
    return { key: d.key, name: DEP_NAMES[d.key] ?? d.key, word: DEP_WORDS[state], state };
  });

  return {
    banner, pipeline: { badge, updatedText, updatedAlarm: stale, whereText, pidText, writesText, writesWarn },
    now, queue, failures, meetings, difyRows, difyAgeText, deps, stale,
  };
}

// «2026-11-18 планёрка.md» → «2026-11-18 планёрка»: расширение в имени строки не нужно,
// полное имя (с расширением) — в title строки
function meetingName(file: string): string {
  const i = file.lastIndexOf('.');
  return i > 0 ? file.slice(0, i) : file;
}

// «47 мин», «1 ч 5 мин» — длина записи короткой формой (макет .mt .sub; не разговорные
// «47 минут», как у предупреждений баннера)
export function meetingLengthText(min: number): string {
  const m = Math.max(0, Math.round(min));
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const r = m % 60;
  return r ? `${h} ч ${r} мин` : `${h} ч`;
}

// «11:42» сегодня, «вчера 18:05» вчера: окно бэка — 24 часа, старше этих двух дней
// строк нет; дни считаем по календарным суткам (перекос часов — в пределах окна)
function meetingTimeText(at: number, nowMs: number): string {
  const day = (ms: number) => { const d = new Date(ms); d.setHours(0, 0, 0, 0); return d.getTime(); };
  const diff = Math.round((day(nowMs) - day(at)) / 86_400_000);
  return diff >= 1 ? `вчера ${clockText(at)}` : clockText(at);
}
