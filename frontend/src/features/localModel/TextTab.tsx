// Вкладка «LLM» панели «Локальные модели»: сверху вниз — жива ли модель →
// очередь → как быстро → нагрузка и кэш → что с картами → кто на ней
// (макет docs/mockups/local-model-stats-panel.html, раздел «Спецификация для Киры»).
//
// Состояния: notConnected — пустое состояние с кнопкой в «Поставщики моделей»;
// down — баннер «не отвечает с HH:MM» + последние данные приглушённые (opacity .55,
// pointer-events: none); загрузка — скелетоны; поле в `unsupported` — строка
// «Движок {имя} это не сообщает» вместо блока (нули не рисуем);
// нет данных из-за сбоя поллера (error из снимка) — карточка «Нет связи с
// сервером» / «Не удалось получить данные» (Ф10): пустое тело всегда объяснён.
//
// Стенд второй модели (движок на Video, макет docs/mockups/bonsai-toolbar.md): тот же порядок
// блоков, движок — параметр `engine`. Отличия по макету §3–§4:
// — первой строкой — переключатель вкладок моделей, виден только при двух
//   настроенных движках (список — GET /api/local-model/engines);
// — статус 'yielded' разводится точными подписями по состоянию арбитра (format.ts);
//   уступка — нейтральный баннер и нейтральный тон, НЕ авария и не приглушение;
// — при выгруженной модели «Сейчас»/«Скорость» — спокойная строка, не нули;
// — поля из `unsupported` (Кэш, Вытеснения, время агентов) скрыты целиком;
// — видеокарта — своя карточка «Видеокарта · Video» с чипом «общая с ComfyUI»
//   (данные с узла ComfyUI на Video; «Стенды» — хост CCS, ссылки туда нет);
// — «Кто сейчас на модели» — одна карточка на оба стенда: чаты дорожки (свой — кликабелен)
//   и места сервера из duty.callsNow (не кликабельны); счётчик = чаты + места + «прочие»;
// — «Дежурство» (DutyBlock) — последним блоком, только у выбранного второго движка и только
//   если снимок принёс duty (макет §3/§5); переход «→ Медиа» — через onOpenMedia от панели;
// — «семантика llama-server» (подпись «чтение промпта» без «с ожиданием», подпись про
//   ступеньки счётчиков) — по data.engine из снимка, а не по имени стенда: именованный
//   движок на Video может быть и vLLM (LocalEngines:<name>:MetricsEngine) — у него
//   prompt-время включает очередь, а ступенчатая модель счётчиков к нему не относится.

import { type CSSProperties } from 'react';
import { AlertTriangle, CirclePause, Plug, FolderOpen, Unplug } from 'lucide-react';
import { C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { Button, EmptyState, InlineSegmented } from '../../components/ui';
import { requestOpenModelsSpend } from '../../lib/modelProvidersNav';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import type { LocalModelEngine, LocalModelEngineName, LocalModelStats } from './api';
import type { StatsPollError } from './useLocalModelStats';
import {
  answerSpeedMedianHint, answerSpeedTpsHint, answerSpeedWindowLabel, formatGpuMem, formatPower, formatSec,
  formatSince, formatTps, formatUpdatedAt, GEN_SPEED_HINT, gpuCountLabel, notReportedText, whoRows,
  standDot, standHint, statusPill, TTFT_MEDIAN_HINT, TTFT_P90_HINT, WHO_SPEED_HINT,
  type StatusTone, type WhoRow,
} from './format';
import { DutyBlock } from './DutyBlock';
import { useAllProjects } from '../projects/useAllProjects';
import { HwLinkRow } from './HwLinkRow';
import { ThroughputChart, WindowToggle, type Window } from './ThroughputChart';

// === Оболочка секции ===
// Карточка: C.bgWhite (тот же фон, что у карточек соседних панелей и контентной
// зоны), рамка C.borderLight, R.xl, паддинг SP.md. Заголовок —
// 11 / 700 / uppercase / C.textMuted.
function Card({ title, right, children }: { title: string; right?: React.ReactNode; children?: React.ReactNode }) {
  return (
    <section style={{
      background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
    }}>
      {/* flexWrap — на 320 переключатель окна уходит под заголовок, а не сжимает его (макет §6) */}
      <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 style={{
          margin: 0, fontSize: 11, fontWeight: 700, textTransform: 'uppercase',
          letterSpacing: '0.06em', color: C.textSecondary,
        }}>{title}</h3>
        {right}
      </div>
      {children}
    </section>
  );
}

// «Движок {имя} это не сообщает» / «Движок этого не сообщает» — вместо блока,
// чьё поле движок не отдал (не рисовать нули: ноль — это значение, а не
// отсутствие данных). Текст — общий helper фичи (format.ts), его же берёт график.
function NotReported({ data }: { data: LocalModelStats }) {
  return (
    <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>
      {notReportedText(data.engine)}
    </p>
  );
}

// === Статус: имя модели + пилюля + подстрока ===
// Пилюля: R.max, FS.xs 700, точка 7 px. Тон — из statusPill (format.ts): там точные
// подписи 'yielded' разведены по состоянию арбитра («Уступила карту», «Ждёт возврата»,
// «Загружается» — нейтральные; «Ждёт память карты» — warning: память сама не
// освободится). Нейтральный тон уступки — bgInset/textSecondary/textMuted (макет §6):
// уступка — штатная работа арбитра, не авария.
const TONE_COLORS: Record<StatusTone, { bg: string; fg: string; dot: string }> = {
  success: { bg: C.successBg, fg: C.successText, dot: C.success },
  warning: { bg: C.warningBg, fg: C.warningText, dot: C.warning },
  danger: { bg: C.dangerBg, fg: C.dangerText, dot: C.danger },
  muted: { bg: C.bgInset, fg: C.textSecondary, dot: C.textMuted },
};

function StatusRow({ data }: { data: LocalModelStats }) {
  const pill = statusPill(data);
  const tone = TONE_COLORS[pill.tone];
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
      <span title={data.model} style={{
        flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: FS.base, fontWeight: 700,
        color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>{data.model ?? '—'}</span>
      <span style={{
        flexShrink: 0, display: 'inline-flex', alignItems: 'center', gap: 5,
        background: tone.bg, color: tone.fg, borderRadius: R.max,
        padding: `${SP.xxs}px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 700, whiteSpace: 'nowrap',
      }}>
        <span style={{ width: 7, height: 7, borderRadius: R.full, background: tone.dot, flexShrink: 0 }} />
        {pill.label}
      </span>
    </div>
  );
}

// Подстрока под статусом: main — движок · N карт, TP=N · обновлено HH:MM:SS / данные на HH:MM.
// Подписи из Stats (engineLabel, topology) заменяют движок и «TP=N», если заданы.
// Стенд на Video (макет §3, .html:414): движок · Video · … — карта на Video одна и общая с
// ComfyUI, её данные — с узла ComfyUI: число карт и TP там не о чём, по макету стоит
// положение стенда «Video» (тот же факт, что в заголовке VideoCard).
function StatusSubline({ data, named }: { data: LocalModelStats; named?: boolean }) {
  const parts: string[] = [];
  const engine = data.engineLabel || data.engine;
  if (engine) parts.push(engine);
  if (named) {
    parts.push('Video');
  } else {
    const n = data.gpus?.length ?? 0;
    if (n > 0) {
      // TP: из необязательного поля tensorParallel, иначе = число карт
      const tp = data.tensorParallel ?? n;
      parts.push(`${gpuCountLabel(n)}, ${data.topology || `TP=${tp}`}`);
    }
  }
  if (data.status === 'down') {
    const since = formatSince(data.downSince);
    parts.push(since ? `данные на ${since}` : 'данные на —');
  } else {
    // Честное «обновлено»: время последней точки истории (серверные таймштампы).
    // Точек нет — честного источника времени нет → фразу не показываем.
    const updated = formatUpdatedAt(data.history);
    if (updated) parts.push(updated);
  }
  return (
    <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: -SP.xs }}>
      {parts.join(' · ')}
    </div>
  );
}

// === Баннер «Движок не отвечает» ===
// C.dangerBg/C.dangerBorder/C.dangerText; кнопка ghost «Проверить сейчас»
// запускает внеочередной опрос (refetch из панели).
// Для стенда на Video — свой текст (макет §4): 'down' у движка с арбитром означает,
// что арбитр её НЕ выгружал, — это авария, а не уступка (Б3b).
function DownBanner({ data, onCheck, named }: { data: LocalModelStats; onCheck: () => void; named?: boolean }) {
  const since = formatSince(data.downSince);
  return (
    <div style={{
      display: 'flex', gap: SP.sm, alignItems: 'flex-start',
      background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, color: C.dangerText,
      borderRadius: R.lg, padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm, lineHeight: 1.45,
    }}>
      <span style={{ flexShrink: 0, marginTop: 1 }}>
        <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </span>
      <div style={{ minWidth: 0 }}>
        <b style={{ fontWeight: 700 }}>
          {named
            ? `Модель не отвечает${since ? ` с ${since}` : ''}, а арбитр её не выгружал.`
            : `Движок не отвечает${since ? ` с ${since}` : ''}.`}
        </b>{' '}
        {named ? 'Это авария, не уступка: места идут на MiniMax.' : 'Новые ходы на локальной модели встанут в ошибку, пока он не поднимется.'}
        <div style={{ marginTop: SP.sm }}>
          <Button variant="ghost" size="xs" onClick={onCheck}>Проверить сейчас</Button>
        </div>
      </div>
    </div>
  );
}

// === Нейтральный баннер уступки (макет §4/§6) ===
// 'yielded' — плановая выгрузка модели: фон C.bgInset, рамка C.borderLight,
// текст C.textSecondary, иконка CirclePause. НЕ красный и без приглушения
// остальных блоков: стенд жив, просто модель сейчас не на карте.
function YieldBanner({ data }: { data: LocalModelStats }) {
  const state = data.duty?.arbiter?.state;
  const head =
    state === 'waiting-vram' ? 'Модель ждёт память карты.'
      : state === 'starting' ? 'Модель загружается.'
        : 'Модель уступила карту — это по плану.';
  return (
    <div style={{
      display: 'flex', gap: SP.sm, alignItems: 'flex-start',
      background: C.bgInset, border: `1px solid ${C.borderLight}`, color: C.textSecondary,
      borderRadius: R.lg, padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm, lineHeight: 1.45,
    }}>
      <span style={{ flexShrink: 0, marginTop: 1 }}>
        <CirclePause size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </span>
      <div style={{ minWidth: 0 }}>
        <b style={{ fontWeight: 700 }}>{head}</b>{' '}
        Модель выгружена, места идут на MiniMax. Причина и срок — в блоке «Дежурство» ниже.
      </div>
    </div>
  );
}

// === «Сейчас»: идут / ждут ===
// Крупные числа — FONT.mono FS.xl 700; «ждут» — warning, только когда > 0.
// title — подсказка к самому числу: что это и с чем сравнивать (тексты подсказок — format.ts)
function Kpi({ value, unit, label, note, warn, title }: { value: string; unit?: string; label: string; note?: string; warn?: boolean; title?: string }) {
  return (
    <div>
      <div title={title} style={{ fontFamily: FONT.mono, fontSize: FS.xl, fontWeight: 700, lineHeight: 1.2, color: warn ? C.warningText : C.textHeading }}>
        {value}{unit && <small style={{ fontSize: FS.xs, fontWeight: 400, color: C.textMuted }}> {unit}</small>}
      </div>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xxs }}>{label}</div>
      {note && <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>{note}</div>}
    </div>
  );
}

// Ключевое отличие уступки от аварии (макет §4): при 'yielded' метрики молчат, но это
// не отказ — модель выгружена. «Сейчас»/«Скорость» показывают спокойную строку
// C.textMuted, а не нули и не красное: ноль — это значение, а выгруженной модели
// приписывать ноль запросов нечестно.
function UnloadedNote({ text }: { text: string }) {
  return <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{text}</p>;
}

function NowCard({ data }: { data: LocalModelStats }) {
  if (data.status === 'yielded') {
    return <Card title="Сейчас"><UnloadedNote text="Модель выгружена — запросов к ней нет." /></Card>;
  }
  if (data.running == null || data.waiting == null) {
    return <Card title="Сейчас"><NotReported data={data} /></Card>;
  }
  return (
    <Card title="Сейчас">
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: SP.sm }}>
        <Kpi value={String(data.running)} label="запросов идёт" />
        <Kpi value={String(data.waiting)} label="ждут в очереди" warn={data.waiting > 0} />
      </div>
    </Card>
  );
}

// «Семантика llama-server» — модель отсчётов движка: prompt-время чистое (очереди в
// нём нет), счётчики прирастают по завершении запроса (ступеньки на графике). Определяется
// типом движка из снимка, а НЕ именем стенда: именованный движок на Video бывает и vLLM
// (LocalEngines:<name>:MetricsEngine) — у него prompt-время включает очередь, а
// ступенчатая модель к нему не относится. Тип — как кладёт бэк: 'llama-server' (lowercase).
function isLlamaServer(data: LocalModelStats): boolean {
  return data.engine === 'llama-server';
}

// === «Скорость»: генерация и чтение промпта, ток/с на поток, «сейчас» (окно ~60 с) ===
// Два KPI — «сейчас» (genPerStream/prefillPerStream, как чувствует отдельный чат).
// Среднее и пик за окно графика — ТОЛЬКО у графика (дубль под KPI убран).
// «Чтение промпта С ОЖИДАНИЕМ»: знаменатель — время prefill запроса от старта до
// первого токена, а оно включает ожидание очереди шагов, поэтому величина ниже
// реальной скорости вычисления. Без слова «с ожиданием» показатель вводит в
// заблуждение — это про vLLM на любом стенде; у llama-server prompt_seconds_total —
// чистое время чтения, там слово было бы неправдой.
function SpeedCard({ data }: { data: LocalModelStats }) {
  if (data.status === 'yielded') {
    return <Card title="Скорость"><UnloadedNote text="Нет данных, пока модель выгружена. Последнее значение — на графике слева от разрыва." /></Card>;
  }
  if (data.speed == null) {
    return <Card title="Скорость"><NotReported data={data} /></Card>;
  }
  return (
    <Card title="Скорость">
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: SP.sm }}>
        {/* Имя говорит, что это НЕ та величина, с которой сверяют облачные оценки
            (для них — карточка «Скорость ответа» ниже): «пока пишет» = только время
            генерации, без ожидания слота и чтения промпта */}
        <Kpi
          value={formatTps(data.speed.genPerStream)}
          label="скорость генерации (пока пишет), ток/с, за последнюю минуту"
          title={GEN_SPEED_HINT}
        />
        {/* «с ожиданием» — по типу движка: у vLLM prompt-время включает очередь шагов
            (на любом стенде); у llama-server prompt_seconds_total — чистое время чтения,
            очередь в него не входит, слово было бы неправдой (макет §3) */}
        <Kpi value={formatTps(data.speed.prefillPerStream)} label={isLlamaServer(data)
          ? 'чтение промпта, ток/с, за последнюю минуту'
          : 'чтение промпта с ожиданием, ток/с, за последнюю минуту'} />
      </div>
    </Card>
  );
}

// === «Скорость ответа»: сквозное определение, как у облачных оценок ===
// (фича llm-answer-speed, docs/features/llm-answer-speed-spec.md): заглавное число —
// сквозная скорость за 5 минут (окно, как у OpenRouter), рядом — за час; ниже — медианы
// по запросам и время до первого токена (медиана и p90) по тем же окнам.
// Окна берутся ПО КЛЮЧУ из ответа ('5m'/'1h'), а не по индексу: порядок в ответе не
// значим; окна нет — строки нет (выдуманного окна не придумываем).
// Нет данных — спокойная строка «нет данных», а не нули: ноль — это измеренное значение,
// а здесь измерять было нечего (у выгруженной модели — тот же тон, что у «Скорости»).
const NO_DATA = 'нет данных';
const AS_CELL_LABEL: CSSProperties = { fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs };
const AS_GRID: CSSProperties = { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: SP.sm };

// Значение с подписью и подсказкой: есть число — моно, нет — спокойная «нет данных»
// (не «—»: здесь человек должен прочесть, что данных нет, а не гадать по прочерку).
function asCell(v: number | null | undefined, fmt: (n: number) => string, label: string, hint: string, big?: boolean) {
  const has = v != null && Number.isFinite(v);
  return (
    <div>
      <div
        title={hint}
        style={has
          ? { fontFamily: FONT.mono, fontWeight: 700, lineHeight: 1.2, color: C.textHeading, fontSize: big ? FS.xl : FS.md }
          : { fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}
      >
        {has ? fmt(v as number) : NO_DATA}
      </div>
      <div style={AS_CELL_LABEL}>{label}</div>
    </div>
  );
}

function AnswerSpeedCard({ data }: { data: LocalModelStats }) {
  const answer = data.answerSpeed;
  const windows = answer?.windows ?? [];
  const minTokens = answer?.minTokensForMedian;
  const w5 = windows.find(w => w.window === '5m');
  const w1h = windows.find(w => w.window === '1h');
  if (data.status === 'yielded') {
    return <Card title="Скорость ответа"><UnloadedNote text="Нет данных, пока модель выгружена." /></Card>;
  }
  const hasAny = windows.some(w => w.endToEndTps != null || w.medianTps != null || w.medianTtftSec != null || w.p90TtftSec != null);
  if (!answer || windows.length === 0 || !hasAny) {
    return <Card title="Скорость ответа"><UnloadedNote text="Нет данных: сквозной скорости ещё не измерено." /></Card>;
  }
  // Число запросов в окне — подписью под заглавным числом: оно говорит, насколько
  // оценка опорная. Форма нейтральная (число после двоеточия): «N запросов» разъезжается
  // на 1 и 21, а таблица склонений ради этой строки не стоит.
  const requestsNote = (n?: number) => (
    n != null && Number.isFinite(n)
      ? <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>запросов в окне: {n}</div>
      : null
  );
  const head = (w: typeof w5, key: string) => {
    const name = answerSpeedWindowLabel(key);
    return (
      <div>
        {asCell(w?.endToEndTps, formatTps, name, answerSpeedTpsHint(name), true)}
        {requestsNote(w?.requests)}
      </div>
    );
  };
  const median = (w: typeof w5, key: string) => {
    const name = answerSpeedWindowLabel(key);
    return asCell(w?.medianTps, formatTps, `медиана по запросам, ${name}`, answerSpeedMedianHint(name, minTokens));
  };
  const ttft = (w: typeof w5, key: string) => {
    const name = answerSpeedWindowLabel(key);
    return (
      <div>
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>{name}</div>
        <div style={{ ...AS_GRID, marginTop: SP.xxs }}>
          {asCell(w?.medianTtftSec, formatSec, 'медиана', TTFT_MEDIAN_HINT)}
          {asCell(w?.p90TtftSec, formatSec, 'p90', TTFT_P90_HINT)}
        </div>
      </div>
    );
  };
  return (
    <Card title="Скорость ответа">
      <div style={AS_GRID}>{head(w5, '5m')}{head(w1h, '1h')}</div>
      <div style={{ ...AS_GRID, marginTop: SP.md }}>{median(w5, '5m')}{median(w1h, '1h')}</div>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.md }}>Время до первого токена</div>
      <div style={{ ...AS_GRID, marginTop: SP.sm }}>{ttft(w5, '5m')}{ttft(w1h, '1h')}</div>
    </Card>
  );
}

// === «Видеокарты»: строка-ссылка на вкладку «Стенды» (только админ) ===
// GpuCard ушёл на «Стенды» (GpuDetailCard); здесь — компактная строка-ссылка:
// точка худшего уровня, «память 68° · 81°», плашки, «Стенды →». Касание открывает
// «Стенды». hardware — только у админа (не-админу поля нет → строки нет, ADR-030).
function GpusCard({ data, onOpenHardware }: { data: LocalModelStats; onOpenHardware: () => void }) {
  if (!data.hardware || data.hardware.gpus.length === 0) return null;
  return <HwLinkRow brief={data.hardware} onOpen={onOpenHardware} />;
}

// === «Видеокарта · Video» (стенд на Video, макет §3/§6) ===
// Данные — с узла ComfyUI на Video (b.gpus в ответе stats?engine=named): «Стенды» —
// это хост CCS, ссылки туда нет. Чип «общая с ComfyUI» — карточка стоит на Video и
// делит её с ComfyUI (макет §3). ComfyUI не ответил (gpus пустые) — «Нет данных о
// карте…», карточка остаётся: человек должен видеть, что карта есть, но молчит узел.
const GPU_CHIP_STYLE: CSSProperties = {
  fontSize: FS.xs, color: C.textMuted, border: `1px solid ${C.borderLight}`,
  borderRadius: R.max, padding: `0 ${SP.sm}px`, whiteSpace: 'nowrap',
};

function VideoCard({ data }: { data: LocalModelStats }) {
  const chip = <span style={GPU_CHIP_STYLE}>общая с ComfyUI</span>;
  const gpu = data.gpus?.[0];
  if (!gpu) {
    return (
      <Card title="Видеокарта · Video" right={chip}>
        <UnloadedNote text="Нет данных о карте: узел ComfyUI на Video не отвечает." />
      </Card>
    );
  }
  const total = gpu.memTotalMb ?? 0;
  const used = gpu.memUsedMb ?? 0;
  const usedPerc = total > 0 ? Math.min(100, Math.round((used / total) * 100)) : null;
  // Заливка полосы: warning, когда память держит чужая работа (ждёт VRAM) или
  // заполнено > 85 %; иначе success (макет §6)
  const vramWait = data.duty?.arbiter?.state === 'waiting-vram';
  const fillColor = vramWait || (usedPerc ?? 0) > 85 ? C.warning : C.success;
  const miniKpi = (value: string, label: string) => (
    <div>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.md, fontWeight: 700, color: C.textHeading }}>{value}</div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>{label}</div>
    </div>
  );
  return (
    <Card title="Видеокарта · Video" right={chip}>
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, fontSize: FS.sm }}>
        <span style={{ color: C.textMuted }}>Память</span>
        <span style={{ fontFamily: FONT.mono, fontWeight: 600, color: C.textHeading }}>{formatGpuMem(gpu.memUsedMb, gpu.memTotalMb)}</span>
      </div>
      <div style={{ height: 6, borderRadius: R.max, background: C.track, overflow: 'hidden', marginTop: SP.sm }}>
        {usedPerc != null && <div style={{ height: '100%', width: `${usedPerc}%`, background: fillColor }} />}
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: SP.sm, marginTop: SP.md }}>
        {miniKpi(gpu.utilPerc != null ? `${gpu.utilPerc} %` : '—', 'загрузка')}
        {miniKpi(gpu.tempC != null ? `${gpu.tempC}°` : '—', 'температура')}
        {miniKpi(formatPower(gpu.powerW, gpu.powerLimitW), 'мощность')}
      </div>
    </Card>
  );
}

// === «Нагрузка и кэш»: выработка (ток/с) + график «Кэш, %» + полоса «Вытеснения»
// на общей оси времени. Окно держит LocalModelPanel (его же читает серверный опрос),
// переключатель стоит в шапке карточки (как в макете).
// Поля, названные в data.unsupported, ThroughputChart прячет целиком (макет §3:
// заглушки «Движок это не сообщает» трижды подряд были бы шумом) — это про ЛЮБОЙ стенд,
// «named» к сокрытию не причём. Когда скрыто всё кэш-содержание, карточка называется
// «Нагрузка» (макет §3), и к графику добавляется подпись про ступеньки — только для
// llama-server-семантики (isLlamaServer): ступенчатые счётчики прирастают по завершении
// запроса у него; vLLM-именованный стенд сообщает непрерывно, подпись про него была бы
// неправдой даже при скрытом KV.
function kvHidden(data: LocalModelStats): boolean {
  return (data.unsupported ?? []).includes('KvUsagePerc');
}

function ChartCard({ data, window, onWindowChange, isMobile }: {
  data: LocalModelStats; window: Window; onWindowChange: (w: Window) => void; isMobile: boolean;
}) {
  const hideKv = kvHidden(data);
  return (
    <Card title={hideKv ? 'Нагрузка' : 'Нагрузка и кэш'} right={<WindowToggle value={window} onChange={onWindowChange} isMobile={isMobile} />}>
      <ThroughputChart
        history={data.history} window={window} engine={data.engine}
        cleanStepMs={data.cleanStepMs ?? null} unsupported={data.unsupported}
      />
      {isLlamaServer(data) && hideKv && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.45 }}>
          llama-server обновляет счётчики, когда запрос закончен, поэтому график идёт ступеньками.
        </div>
      )}
    </Card>
  );
}

// === «Кто сейчас на модели» ===
// Строка чата — кнопка (36 desktop / 44 touch): аватар 24, имя FS.sm 600 с
// многоточием, проект FS.xs C.textMuted, справа скорость. Свой чат — клик
// открывает его; чужой — «Чат другого пользователя» без названия и перехода;
// строка места — div без перехода; «Прочие запросы: N» — хвост списка.
const WHO_AVATAR: CSSProperties = {
  width: 24, height: 24, flex: '0 0 24px', borderRadius: R.full,
  background: C.accentMuted, color: C.textHeading, fontSize: 10, fontWeight: 700,
  display: 'flex', alignItems: 'center', justifyContent: 'center',
};
const WHO_TITLE: CSSProperties = {
  display: 'block', fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
};

function WhoRowView({ row, isMobile, onOpen }: { row: WhoRow; isMobile: boolean; onOpen: (id: string) => void }) {
  const minH = isMobile ? 44 : 36;
  if (row.kind === 'others') {
    return (
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: minH,
        padding: `${SP.sm}px ${SP.sm}px`, fontSize: FS.sm, color: C.textMuted,
      }}>
        Прочие запросы: {row.count}
      </div>
    );
  }
  const base: CSSProperties = {
    display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: minH,
    padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.md, width: '100%',
    background: 'transparent', border: 'none', color: 'inherit', cursor: 'default',
  };
  // Строка места — действие сервера из журнала вызовов (duty.callsNow), а не чат:
  // div без перехода (разрез v2, §4). Вместо проекта — звено цепочки; справа —
  // длительность вызова: ждущий (ещё не начался) — warning, та же, что строка «Дежурства».
  if (row.kind === 'place') {
    return (
      <div style={base}>
        <span style={WHO_AVATAR}>{(row.title[0] ?? '·').toUpperCase()}</span>
        <span style={{ flex: 1, minWidth: 0 }}>
          <b style={WHO_TITLE}>{row.title}</b>
          <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted }}>{row.linkText}</span>
        </span>
        {row.duration && (
          <span style={{
            fontFamily: FONT.mono, fontSize: FS.sm, whiteSpace: 'nowrap',
            color: row.queued ? C.warningText : C.textSecondary,
          }}>{row.duration}</span>
        )}
      </div>
    );
  }
  const speedColor = row.tone === 'wait' ? C.warningText : row.tone === 'read' ? C.textMuted : C.textSecondary;
  const speedStyle: CSSProperties = {
    fontFamily: FONT.mono, fontSize: FS.sm, whiteSpace: 'nowrap',
    color: speedColor, fontWeight: row.tone === 'gen' ? 700 : 400,
  };
  const inner = (
    <>
      <span style={WHO_AVATAR}>{(row.title[0] ?? '·').toUpperCase()}</span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <b style={WHO_TITLE}>{row.title}</b>
        {row.project && (
          <span style={{
            display: 'block', fontSize: FS.xs, color: C.textMuted,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{row.project}</span>
        )}
      </span>
      {/* Подсказка к числу скорости: формулу не меняем (её считает бэк), подсказка
          называет, что в неё входит: текст — по формуле бэка, см. WHO_SPEED_HINT (format.ts) */}
      <span style={speedStyle} title={row.tone === 'gen' ? WHO_SPEED_HINT : undefined}>{row.speed}</span>
    </>
  );
  // Чужой чат — div без перехода; свой — кнопка (клик открывает чат)
  if (!row.own || !row.sessionId) {
    return <div style={base}>{inner}</div>;
  }
  return (
    <button
      type="button"
      style={{ ...base, cursor: 'pointer' }}
      onClick={() => onOpen(row.sessionId!)}
      title="Открыть чат"
    >{inner}</button>
  );
}

// «Кто сейчас на модели» — одна карточка на оба стенда (F3): main — только чаты
// (без мест, прежний рендер); named — чаты своего снимка и места из duty.callsNow
// в одном списке (порядок whoRows: чаты → места → «прочие»). Места читает только
// named: рендер ровно того, что отдал /stats?engine= выбранного стенда, без склейки
// с чужим снимком (инвариант: чат на стенде X — только во вкладке X).
function WhoCard({ data, isMobile, onOpen, named }: { data: LocalModelStats; isMobile: boolean; onOpen: (id: string) => void; named?: boolean }) {
  // Имя проекта — из общего стора проектов по projectId (бэк отдаёт только id,
  // и только для своих чатов); чужой чат проекта не получает вовсе
  const projects = useAllProjects();
  const callsNow = named ? data.duty?.callsNow : undefined;
  // main: движок ничего не сообщил о чатах — «Движок это не сообщает». У named этой
  // ветки нет: журнал вызовов — дело сервера, а не движка, пустой список — «Пока никто»
  if (!named && data.chats == null && (data.othersCount ?? 0) === 0) {
    return (
      <Card title="Кто сейчас на модели">
        <NotReported data={data} />
      </Card>
    );
  }
  const rows = whoRows(data.chats, data.othersCount, id => id ? projects.find(p => p.id === id)?.name : undefined, callsNow);
  if (rows.length === 0) {
    return (
      <Card title="Кто сейчас на модели">
        <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted }}>Пока никто</p>
      </Card>
    );
  }
  // Честный счётчик: чаты + места + «прочие запросы» (строка «Прочие: N» — один элемент
  // списка, но N запросов). Не rows.length: он считал бы «прочие» за 1.
  const total = (data.chats?.length ?? 0) + (callsNow?.length ?? 0) + (data.othersCount ?? 0);
  const hasPlaces = rows.some(r => r.kind === 'place');
  return (
    <Card
      title="Кто сейчас на модели"
      right={<span style={{ fontSize: FS.xs, color: C.textMuted, fontWeight: 400 }}>{total}</span>}
    >
      <div style={{ display: 'flex', flexDirection: 'column', margin: `0 ${-SP.sm}px` }}>
        {rows.map(r => <WhoRowView key={r.key} row={r} isMobile={isMobile} onOpen={onOpen} />)}
      </div>
      {/* Строка места выглядит как строка чата, но перехода нет — подпись говорит об
          этом, чтобы её не пытались открыть (макет §3/§4) */}
      {hasPlaces && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm }}>
          Места в списке — это вызовы сервера, а не чаты: они не кликабельны, владельца и промпта нет.
        </div>
      )}
    </Card>
  );
}

// === Переключатель вкладок моделей (макет §2) ===
// Первая строка вкладки, над её содержимым — над стендом, над карточкой сбоя и над
// скелетоном (дефект b129c182): список движков живёт своим опросом
// (useLocalModelEngines) и не зависит от снимка статистики, поэтому уйти на другой
// стенд можно и когда данных нет. Виден ТОЛЬКО когда настроены оба движка:
// с одним движком стенд открывается сразу, переключателя нет. Подписи — titles из
// /engines («Flash-Next», «Gemma 4»): имена моделей в сегмент не ставим (на 320 не
// помещаются, имя и так стоит первой строкой стенда). Точка 6 px и подсказка на
// сегменте второго движка — только когда выбран сам «второй движок» и по ЕГО снимку: data —
// снимок текущего движка, состояние второй движок с основного стенда неизвестно (при
// падении основного старая логика зажигала на сегменте «Модель недоступна» —
// неправда). Снимка нет (data === null) — нет и точки с подсказкой: выдумывать
// состояние по чужому снимку нечем. Состояние второй движок на основном стенде придёт в
// Ф6 из лёгкого опроса useLocalModelWatcher. title/aria-label сегмента — та же
// подсказка, что у рельса.
function EngineSwitch({ data, engines, engine, onChange, isMobile }: {
  data: LocalModelStats | null; engines: LocalModelEngine[]; engine: LocalModelEngineName;
  onChange: (e: LocalModelEngineName) => void; isMobile: boolean;
}) {
  const configured = engines.filter(e => e.configured);
  if (configured.length < 2) return null;
  // Точка и подсказка — только по снимку выбранного движка (составитель — format.ts)
  const namedSelected = engine !== 'main';
  const dot = data && namedSelected ? standDot(data) : null;
  const hint = data && namedSelected ? standHint(data) : null;
  return (
    <InlineSegmented<LocalModelEngineName>
      value={engine}
      full
      isMobile={isMobile}
      size="sm"
      options={configured.map(e => ({
        value: e.name,
        label: e.title,
        // как в макете: «Gemma 4 — уступила карту» (имя модели один раз, подсказка без префикса «Модель»)
        title: e.name !== 'main' && dot && hint ? `${e.title} — ${hint.replace(/^Модель:? ?/, '')}` : undefined,
        icon: e.name !== 'main' && dot ? (
          <span style={{
            width: 6, height: 6, borderRadius: R.full, flexShrink: 0,
            background: dot === 'warning' ? C.warning : C.textMuted,
          }} />
        ) : undefined,
      }))}
      onChange={onChange}
    />
  );
}

// === «Не подключена»: пустое состояние ===
// Кружок 48 C.bgInset, заголовок FONT.serif FS.lg, текст FS.sm C.textSecondary,
// кнопка primary — открывает «Поставщики моделей» (вкладка «Модели», где живёт
// адрес движка).
function NotConnected() {
  return (
    <EmptyState
      icon={<Plug size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
      title="Локальная модель не подключена"
      subtitle={
        <>
          Адрес движка задаётся в «Поставщиках моделей». После подключения здесь появятся
          скорость, очередь и видеокарты.
        </>
      }
      action={
        <Button
          variant="primary"
          size="sm"
          leftIcon={<FolderOpen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => requestOpenModelsSpend('slots')}
        >
          Открыть «Поставщики моделей»
        </Button>
      }
    />
  );
}

// === «Нет связи» / сбой опроса (Ф10) ===
// Связь оборвалась до первого ответа stats (или сразу после смены движка — поллер
// сбрасывает снимок): вместо пустого тела — спокойная карточка, объясняющая
// причину. Чисел «как текущих» здесь нет по построению: данных нет — показывать
// нечего. Тон — не авария: опрос продолжается сам, карточка гаснет с первым
// успешным ответом. Формулировка — по признаку из снимка поллера: 'offline' —
// нет связи с сервером, 'failed' — прочий сбой запроса.
function NoConnection({ error }: { error: StatsPollError }) {
  const offline = error === 'offline';
  return (
    // Живое состояние вкладки: карточка появляется и гаснет без участия человека
    // (Ф10) — role="status" + aria-live="polite" доносят её смену скринридеру.
    // Общий EmptyState не трогаем: в других местах он не обязан announce'иться.
    <div role="status" aria-live="polite">
      <EmptyState
        icon={offline
          ? <Unplug size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
          : <AlertTriangle size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
        title={offline ? 'Нет связи с сервером' : 'Не удалось получить данные'}
        subtitle={offline
          ? 'Данные появятся, как только связь вернётся.'
          : 'Повторим автоматически.'}
      />
    </div>
  );
}

// === Загрузка: скелетоны блоков (C.bgInset, пульс; reduced-motion — статично) ===
function TextTabSkeleton() {
  const reduced = usePrefersReducedMotion();
  const pulse: CSSProperties = reduced ? {} : { animation: 'ccs-lm-skeleton 1.4s ease-in-out infinite' };
  const bar = (w: string | number, h: number): CSSProperties => ({
    background: C.bgInset, borderRadius: R.sm, width: w, height: h, ...pulse,
  });
  const block = (h: number): CSSProperties => ({ background: C.bgInset, borderRadius: R.xl, height: h, ...pulse });
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }} aria-hidden>
      <div style={bar('60%', 20)} />
      <div style={block(76)} />
      <div style={block(76)} />
      {/* «Нагрузка и кэш»: 280 px — карточка без поля вытеснений (самое частое состояние) */}
      <div style={block(280)} />
      <div style={block(120)} />
      <div style={block(110)} />
    </div>
  );
}

// === Вкладка ===
// engine — какой стенд рисуем ('main' по умолчанию — прежнее поведение без правок);
// engines — список движков для переключателя (пустой/один — переключателя нет);
// onEngineChange — выбор человека (панель пишет его в стор Ф1, тот в localStorage);
// onOpenMedia — переход на вкладку «Медиа» по ссылке «→ Медиа» в «Дежурстве».
export function TextTab({ data, loading, error, isMobile, onOpenChat, onRefetch, onOpenHardware, onOpenMedia, window, onWindowChange, engines, engine = 'main', onEngineChange }: {
  data: LocalModelStats | null;
  loading: boolean;
  // Причина последнего сбоя поллера (из снимка, Ф10): без данных определяет,
  // объяснять ли пустоту. Не передан или null — прежнее поведение.
  error?: StatsPollError | null;
  isMobile: boolean;
  onOpenChat: (sessionId: string) => void;
  onRefetch: () => void;
  onOpenHardware: () => void;
  onOpenMedia: () => void;
  window: Window;
  onWindowChange: (w: Window) => void;
  engines?: LocalModelEngine[];
  engine?: LocalModelEngineName;
  onEngineChange?: (e: LocalModelEngineName) => void;
}) {
  // Переключатель — над ЛЮБЫМ состоянием вкладки, а не только над стендом
  // (дефект b129c182): упал один стенд — уйти на другой надо из интерфейса, а не
  // через F5. Условие видимости то же, что раньше (список + обработчик; сам
  // EngineSwitch молчит при одном configured), порядок и отступы со стендом —
  // прежние: переключатель остаётся первым элементом потока.
  const engineSwitch = engines && onEngineChange ? (
    <EngineSwitch data={data} engines={engines} engine={engine} onChange={onEngineChange} isMobile={isMobile} />
  ) : null;

  // Порядок состояний (Ф10): известная причина важнее скелетона — при сбое и
  // идущем параллельно повторном опросе (loading) карточка остаётся спокойной и
  // не мигает скелетоном каждые 2 с. Скелетон — только когда причины ещё нет:
  // первое открытие до первого ответа.
  if (!data && error) return <>{engineSwitch}<NoConnection error={error} /></>;
  if (loading && !data) return <>{engineSwitch}<TextTabSkeleton /></>;
  if (!data) return null;
  if (data.status === 'notConnected') return <NotConnected />;

  const named = engine !== 'main';
  const down = data.status === 'down';
  const yielded = data.status === 'yielded';
  // Приглушение последних данных — только у аварии. Уступка не приглушается:
  // стенд жив, это плановая выгрузка модели (макет §4)
  const stale: CSSProperties = down
    ? { opacity: 0.55, filter: 'grayscale(0.6)', pointerEvents: 'none' }
    : {};

  return (
    <>
      {engineSwitch}
      {down && <DownBanner data={data} onCheck={onRefetch} named={named} />}
      {yielded && <YieldBanner data={data} />}
      <StatusRow data={data} />
      <StatusSubline data={data} named={named} />
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, ...stale }}>
        <NowCard data={data} />
        <SpeedCard data={data} />
        {/* «Скорость ответа» — сразу после «Скорости»: сквозная оценка (та, с которой
            сверяют облачные) идёт за справочной «пока пишет» */}
        <AnswerSpeedCard data={data} />
        <ChartCard data={data} window={window} onWindowChange={onWindowChange} isMobile={isMobile} />
        {/* Видеокарты: основной стенд — строка-ссылка на «Стенды» (хост CCS, только
            админ); второй движок — своя карточка Video, ссылка на «Стенды» была бы неправдой */}
        {named
          ? <VideoCard data={data} />
          : <GpusCard data={data} onOpenHardware={onOpenHardware} />}
        {/* «Кто сейчас на модели» — одна карточка: main без мест, named — чаты своего
            снимка и места из duty.callsNow (F3) */}
        <WhoCard data={data} isMobile={isMobile} onOpen={onOpenChat} named={named} />
        {/* «Дежурство» — последний блок стенда на Video (макет §3/§5): только у выбранного
            второй движок; без duty блока нет — DutyBlock сам возвращает null (DutyBlock.tsx:222).
            Внешний guard по data.duty повторял это условие и маскировал в тестах условие
            движка (R-F5) — оставлено единственное условие: движок */}
        {named ? <DutyBlock data={data} isMobile={isMobile} onOpenMedia={onOpenMedia} /> : null}
      </div>
    </>
  );
}
