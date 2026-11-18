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

export interface TransTabView {
  banner: TransBannerView | null;
  pipeline: TransPipelineView;
  now: TransNowView;
  queue: TransQueueView;
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

  return { banner, pipeline: { badge, updatedText, updatedAlarm: stale, whereText, pidText, writesText, writesWarn }, now, queue, stale };
}
