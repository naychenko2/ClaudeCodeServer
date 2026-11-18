// View-model верхней части вкладки «Встречи» (Ф3): плоский снимок (контракт Ф1) → тексты
// и флаги четырёх блоков (баннер, конвейер, «Сейчас», очередь). Чистые функции: время
// приходит параметром (тест проверяет «23 мин назад» без Date.now()).
//
// Уровень тревоги (level) приходит со снимком — своих порогов («Завис?» 5 мин) здесь
// нет. «Завис?» и «Остановлен» — интерпретация данного бэком level + phase, а не новый
// вывод: «danger» при живом процессе без севших зависимостей — это ровно тот «stale»,
// который посчитал бэкенд (см. TranscriptionStateReader.level). Тревога строки «Пишет в»
// — по прямому полю снимка (outputHost=null при известном outputDir,
// TranscriptionStateReader.cs:91-97), а не по level: при дефиците Dify и чужом узле вывода
// на экране нужны обе строки сразу (макет «Сбои + расхождение»).

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

// «8 с назад», «23 мин назад», «4 ч 31 мин назад» (макет: age): минуты — короткой формой
// «мин», как в макете, а не разговорной «23 минуты». Возраст 0–4 с — тоже «N с назад»:
// снимок пишется каждые ~10 с, попадание в это окно регулярно, а «только что» прятало бы
// настоящий возраст (замечание приёмки; форма макета v1.html — «6 с назад», «14 с назад»).
// Отрицательная разница (часовые пояса) — «только что», без «-3 с назад».
export function agoText(fromMs: number, nowMs: number): string {
  const d = nowMs - fromMs;
  if (d < 0) return 'только что';
  const s = Math.floor(d / 1000);
  if (s < 60) return `${s} с назад`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} мин назад`;
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

// «меньше минуты», «3 мин 12 с», «26 мин», «1 ч 5 мин» — короткая форма длительности с
// секундами: только для «идёт» (там есть точная отметка начала этапа). Для баннера и длины
// встречи — целые минуты (minuteDuration): секундов нет в форме макета. Меньше минуты —
// отдельная форма: «0 мин» читается как «только что началось», «0 с» — как выдуманная точность
export function shortDuration(dMs: number): string {
  if (dMs < MINUTE) return 'меньше минуты';
  const total = Math.round(dMs / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m < 60) return s ? `${m} мин ${s} с` : `${m} мин`;
  const h = Math.floor(m / 60);
  const r = m % 60;
  return r ? `${h} ч ${r} мин` : `${h} ч`;
}

// «23 мин», «1 ч 5 мин» — длительность целыми минутами (баннер «не отчитывался …», длина
// встречи): величина округляется до минут — форма макета и proposal.md:28 («23 мин», «47 мин»
// в .mt .sub), «23 мин 17 с» там не было и выдуманная точность не нужна. Секунды — только
// в «идёт» (shortDuration): там известна точная отметка начала этапа
export function minuteDuration(mins: number): string {
  const m = Math.max(0, Math.round(mins));
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const r = m % 60;
  return r ? `${h} ч ${r} мин` : `${h} ч`;
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
  // heading — заголовок пустого состояния: «Ничего не обрабатывается» — утверждение о
  // состоянии, поэтому при «не проверялось» его нет: экран не должен утверждать одно и то же
  // по-разному (дефект приёмки)
  | { kind: 'empty'; heading: string; note: string }
  | {
    kind: 'active';
    file: string;
    folder: string | null;
    steps: TransStageStepView[];
    // «идёт» — с отметки этапа в снимке; null — отметки нет, строки нет
    runningText: string | null;
    // «запись» — длина самой записи; «—» — её нет в контракте (см. transTabView)
    lengthText: string;
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
// name/file — null, когда имени файла нет (бэковый TranscriptionFailDto.File = string?:
// сбой вне файла). Строка при этом остаётся: без имени, но со временем, папкой и
// комментарием — выдуманного имени не подставляем.
export interface TransMeetingRowView {
  id: string; // ключ строки (folder+file); его передают openMeetingNote (openMeetingNote.ts)
  name: string | null; // имя без расширения; полное имя — в title; null — имени нет
  file: string | null;
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
  // «не проверялось» (дефект приёмки): бэк не прочитал state.json — level=null, а phase
  // «stopped» — следствие отсутствия живого pid (MapPhase: !alive → "stopped",
  // TranscriptionStateReader.cs:234-240), а не утверждение о состоянии. «Остановлен» и
  // «Процесса нет.» при этом — два разных утверждения об одном на одном экране; честное —
  // «не проверялось» (то же слово, что у несчитанных зависимостей, DEP_WORDS.unknown).
  // level=null при живом процессе (phase «batch»/«idle») — не этот случай: там фаза
  // настоящая, плашка остаётся по фазе.
  const unchecked = s.level == null && s.phase === 'stopped';
  const stopped = s.phase === 'stopped' && !unchecked;
  // «Завис?» = danger при не-остановленном конвейере без севших зависимостей: по логике
  // бэка (TranscriptionStateReader) именно так и получается «stale»
  const hung = level === 'danger' && !stopped && !depsDown(s);
  const stale = stopped || hung;
  // «не та машина» — прямой бэковый сигнал, своего порога здесь нет: OutputHost ставится
  // именем узла только если каталог вывода существует локально (TranscriptionStateReader.cs:
  // 91-97); известный outputDir при outputHost=null — конвейер пишет не на этом узле
  // (ровно случай 18.11). Тревога строки не зависит от баннера: при дефиците Dify жёлтая
  // строка остаётся (макет «Сбои + расхождение»: и баннер, и она)
  const writesWarn = s.outputHost == null && typeof s.outputDir === 'string' && s.outputDir.length > 0;

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
        // сервис на worker — системный юнит (/etc/systemd/system/transcription-supervisor.service,
        // User=an): «systemctl --user start» на нём даёт «Unit not found» (дефект приёмки).
        // Имя юнита — то же, что в заголовке баннера: transcription-supervisor
        body: `Новые записи не обрабатываются${since}. Запустить можно на worker: sudo systemctl start transcription-supervisor.`,
      };
    } else if (hung && typeof s.updatedAt === 'number') {
      banner = {
        tone: 'danger',
        // форма макета и proposal.md:28 — «23 мин»: целыми минутами (minuteDuration),
        // а не «23 мин 17 с» из сырого возраста снимка и не разговорная «23 минуты»
        title: `Конвейер не отчитывался ${minuteDuration((nowMs - s.updatedAt) / MINUTE)}`,
        body: 'state.json обновляется каждые ~10 с. Похоже, процесс завис на файле ниже — загляните в журнал сервиса.',
      };
    }
  } else if (level === 'warn') {
    const rows = difyDeficient(s);
    if (rows.length) {
      const total = rows.reduce((a, r) => a + r.local - r.inDify, 0);
      // Пояснение случая 18.11 (макет v1.html:234): «Конвейер пишет на ai, а „Встречи“
      // читают с worker». Имени чужого узла в плоском контракте нет — OutputHost=null
      // значит лишь «не этот узел» (TranscriptionStateReader.cs:91-97). Известный факт
      // формулируется на названном узле (host): «пишет не на „worker“» — «не на этом
      // узле» неоднозначно (браузер может быть на другой машине), чужой узел не назван
      const why = writesWarn && typeof s.host === 'string' && s.host.length > 0
        ? ` Конвейер пишет не на узле «${s.host}» («Встречи» читают с него).`
        : '';
      banner = {
        tone: 'warning',
        title: `${total} ${plural(total, 'встреча', 'встречи', 'встреч')} не ${plural(total, 'попала', 'попали', 'попало')} в Dify`,
        body: rows.map(r => `${r.folder}: локально ${r.local}, в Dify ${r.inDify}`).join('; ') + why,
      };
    }
  }

  // === Конвейер ===
  const badge = unchecked
    ? { tone: 'neutral' as const, label: 'не проверялось' }
    : stopped
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
  const writesText = outParts.length
    ? outParts.join(' · ') + (writesWarn ? ' — не та машина' : '')
    : '—';

  // === Сейчас ===
  let now: TransNowView;
  if (typeof s.file !== 'string' || s.file.length === 0) {
    // Пустое состояние: когда закончилась последняя работа — по последней завершённой
    // встрече (своего поля «конец пачки» контракт не несёт); форма строки — по макету
    // (v1.html:225): «последняя пачка закончилась в HH:MM»
    const last = (s.recent ?? [])[0];
    // «не проверялось» — заголовок и строка о состоянии, а не «Ничего не обрабатывается /
    // Процесса нет.»: бэк не читал state.json и про состояние ничего не сказал (см. unchecked)
    const note = unchecked
      ? 'Снимок состояния (state.json) не читается — чем занят конвейер, не проверялось.'
      : stopped
        ? 'Процесса нет.'
        : last
          ? `последняя пачка закончилась в ${clockText(last.at)}. Новые записи из сетевой папки конвейер подхватит сам.`
          : 'Новые записи из сетевой папки конвейер подхватит сам.';
    now = { kind: 'empty', heading: unchecked ? 'Состояние не проверялось' : 'Ничего не обрабатывается', note };
  } else {
    const idx = s.stage === 'asr' ? 0 : s.stage === 'summary' ? 1 : s.stage === 'dify' ? 2 : -1;
    const steps = STAGE_LABELS.map((label, i) => ({
      label,
      state: i < idx ? ('done' as const) : i === idx ? ('cur' as const) : ('todo' as const),
    }));
    // «идёт» — с отметки начала этапа: бэкенд берёт её из своего поля конвейера
    // stage_started_at (unix-сек → мс, TranscriptionStateReader.cs:179,253-254 — доработка
    // Б1 3453297a3), а не из записи снимка. Только начавшийся этап действительно младше
    // минуты — честная форма «меньше минуты», а не «0 мин» и не выдуманная длительность.
    // durationMin — другое время (возраст последней правки снимка), в строке не участвует.
    const runningText = typeof s.stageStartedAt === 'number' ? shortDuration(nowMs - s.stageStartedAt) : null;
    // «запись» — длина самой записи (макет v1.html:272 «запись 52 мин»). Реальной длины в
    // плоском контракте нет: durationMin = (now − updatedAt state.json) — время последней
    // правки снимка, а не длительность файла. По инварианту «поля нет — «—»»: строка есть,
    // значения нет; не 0 и не выдуманное число. Настоящая длина — хвост фичи.
    const lengthText = '—';
    // Место в пачке — только когда есть индекс (в v1 бэкенд его не несёт)
    const batchText = typeof s.batchIndex === 'number' && typeof s.batchSize === 'number'
      ? `пачка ${s.batchIndex} из ${s.batchSize}`
      : null;
    now = { kind: 'active', file: s.file, folder: s.folder ?? null, steps, runningText, lengthText, batchText };
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
  const rowOf = (r: { at: number; folder?: string | null; file: string | null; minutes?: number | null }, openable: boolean): TransMeetingRowView => ({
    // ключ строки журнала: своего id снимок не несёт. У строки без имени файла ключа нет —
    // уникальность даёт время сбоя: за сутки бывает несколько сбоев вне файла, одинаковый
    // id дал бы collision ключей React. Такая строка не открывается — openMeetingNote её
    // id не получает (ср. TranscriptionFail: file nullable у бэка намеренно)
    id: r.file == null ? `${r.folder ?? ''}/#${r.at}` : `${r.folder ?? ''}/${r.file}`,
    name: meetingName(r.file),
    file: r.file,
    folder: r.folder ?? null,
    timeText: meetingTimeText(r.at, nowMs),
    // длина — целыми минутами (47.3 → «47 мин», форма макета .mt .sub): журнал несёт
    // дробные минуты, секунд в них нет
    lengthText: typeof r.minutes === 'number' ? minuteDuration(r.minutes) : null,
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
    .map(r => {
      // поля типа — wire-контракт (int? Local, int? InDify), поэтому числа здесь — после
      // фильтра выше, который и отсёк строки без чисел (дефицит из неизвестного не считается)
      const local = r.local as number;
      const inDify = r.inDify as number;
      return {
        folder: r.folder.startsWith('Video-') ? r.folder.slice('Video-'.length) : r.folder,
        local, inDify,
        deficit: local > inDify ? local - inDify : null,
      };
    });
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
// полное имя (с расширением) — в title строки. Имени нет (бэковый TranscriptionFailDto.File
// = null — сбой вне файла) — нет и имени строки: lastIndexOf на null упал бы всей вкладкой,
// а выдуманное имя («без имени», «сбой») было бы враньем
function meetingName(file: string | null): string | null {
  if (file == null) return null;
  const i = file.lastIndexOf('.');
  return i > 0 ? file.slice(0, i) : file;
}

// «11:42» сегодня, «вчера 18:05» вчера: окно бэка — 24 часа, старше этих двух дней
// строк нет; дни считаем по календарным суткам (перекос часов — в пределах окна)
function meetingTimeText(at: number, nowMs: number): string {
  const day = (ms: number) => { const d = new Date(ms); d.setHours(0, 0, 0, 0); return d.getTime(); };
  const diff = Math.round((day(nowMs) - day(at)) / 86_400_000);
  return diff >= 1 ? `вчера ${clockText(at)}` : clockText(at);
}
