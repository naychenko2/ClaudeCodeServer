// Вкладка «LLM» панели «Локальные модели», порядок v1 «Сначала ферма»: жива ли модель →
// четыре KPI фермы → нагрузка и кэш → кто на модели → что с картами → «Дежурство» →
// точные метрики (закрыты) → облачное сравнение (последней закрытой строкой)
// (макет docs/mockups/local-model-llm-redesign-v1.html, строки 165–215; спека
// docs/mockups/local-model-llm-redesign-proposal.md, §v1).
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

import { type CSSProperties, useCallback, useRef, useState } from 'react';
import { AlertTriangle, CirclePause, Plug, FolderOpen, Unplug } from 'lucide-react';
import { C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { Button, DisclosureSection, EmptyState, InlineSegmented } from '../../components/ui';
import { requestOpenModelsSpend } from '../../lib/modelProvidersNav';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import type { LocalModelEngine, LocalModelEngineName, LocalModelStats } from './api';
import type { StatsPollError } from './useLocalModelStats';
import {
  answerSpeedMedianHint, answerSpeedTpsHint, answerSpeedWindowLabel, formatGpuMem, formatPower, formatSec,
  formatSince, formatTps, formatUpdatedAt, GEN_SPEED_HINT, gpuCountLabel, notReportedText, whoRows,
  standDot, standHint, statusPill, TTFT_MEDIAN_HINT, TTFT_P90_HINT, WHO_SPEED_HINT,
  CLOUD_SPEED_NO_TTFT_HINT, CLOUD_SPEED_REQUESTS_HINT, CLOUD_SPEED_WINDOW_LABEL, cloudSpeedMedianHint,
  cloudSpeedTpsHint,
  type StatusTone, type WhoRow,
} from './format';
import type { CloudAnswerSpeedState } from './useCloudAnswerSpeed';
import { DutyBlock } from './DutyBlock';
import { FarmKpiCard } from './FarmKpiCard';
import { useAllProjects } from '../projects/useAllProjects';
import { HwLinkRow } from './HwLinkRow';
import { ThroughputChart, WindowToggle, type Window } from './ThroughputChart';

// === Оболочка секции ===
// Карточка: C.bgWhite (тот же фон, что у карточек соседних панелей и контентной
// зоны), рамка C.borderLight, R.xl, паддинг SP.md. Заголовок —
// 11 / 700 / uppercase / C.textMuted.
function Card({ title, right, hint, children }: { title: string; right?: React.ReactNode; hint?: string; children?: React.ReactNode }) {
  return (
    <section style={{
      background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
    }}>
      {/* flexWrap — на 320 переключатель окна уходит под заголовок, а не сжимает его (макет §6) */}
      <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 title={hint} style={{
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

// Ключевое отличие уступки от аварии (макет §4): при 'yielded' метрики молчат, но это
// не отказ — модель выгружена. «Сейчас»/«Скорость» показывают спокойную строку
// C.textMuted, а не нули и не красное: ноль — это значение, а выгруженной модели
// приписывать ноль запросов нечестно.
function UnloadedNote({ text }: { text: string }) {
  return <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{text}</p>;
}

// «Семантика llama-server» — модель отсчётов движка: prompt-время чистое (очереди в
// нём нет), счётчики прирастают по завершении запроса (ступеньки на графике). Определяется
// типом движка из снимка, а НЕ именем стенда: именованный движок на Video бывает и vLLM
// (LocalEngines:<name>:MetricsEngine) — у него prompt-время включает очередь, а
// ступенчатая модель к нему не относится. Тип — как кладёт бэк: 'llama-server' (lowercase).
function isLlamaServer(data: LocalModelStats): boolean {
  return data.engine === 'llama-server';
}

// === Плоские строки метрик внутри раскрытия «Точные метрики локальной модели» ===
// Решение Майи (21.11, после ревью R-Ф-3, п.2): внутри раскрытия — плоские строки .kv
// на всех ширинах, без рамки, фона и скругления Card: shell раскрытия уже несёт границу
// группы, а карточка в карточке — лишняя вложенность и шум, особенно на мобиле
// (макет .html:194–205). Заголовки и все поля — прежние; убрана только внешняя обёртка.
const NO_DATA = 'нет данных';
const METRIC_HEAD: CSSProperties = {
  margin: 0, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary,
};
const METRIC_ROW: CSSProperties = {
  display: 'flex', justifyContent: 'space-between', gap: SP.md, padding: `${SP.xs}px 0`,
};
const METRIC_LABEL: CSSProperties = { fontSize: FS.sm, color: C.textSecondary, minWidth: 0 };
// Значение — моно, на той же строке, что подпись (макет .kv .v). Нет значения —
// спокойная «нет данных» (не «—»: здесь человек должен прочесть, что данных нет).
const METRIC_VALUE: CSSProperties = {
  fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600, lineHeight: 1.3,
  color: C.textHeading, whiteSpace: 'nowrap',
};
const METRIC_VALUE_EMPTY: CSSProperties = { fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted };

// Одна строка метрики: подпись слева, значение справа. Хинт поясняет число (тот же,
// что у KPI); приписка — под значением, справа (число запросов относится к числу).
const metricRow = (
  label: string,
  value: number | null | undefined,
  fmt: (n: number) => string,
  hint?: string,
  note?: string,
) => {
  const has = value != null && Number.isFinite(value);
  return (
    <>
      <div style={METRIC_ROW}>
        <span style={METRIC_LABEL}>{label}</span>
        <span title={hint} style={has ? METRIC_VALUE : METRIC_VALUE_EMPTY}>
          {has ? fmt(value as number) : NO_DATA}
        </span>
      </div>
      {note ? <div style={{ fontSize: FS.xs, color: C.textMuted, textAlign: 'right' }}>{note}</div> : null}
    </>
  );
};

// === «Скорость»: генерация и чтение промпта, ток/с на поток, «сейчас» (окно ~60 с) ===
// Две строки — «сейчас» (genPerStream/prefillPerStream, как чувствует отдельный чат).
// Среднее и пик за окно графика — ТОЛЬКО у графика (дубль под KPI убран).
// «Чтение промпта С ОЖИДАНИЕМ»: знаменатель — время prefill запроса от старта до
// первого токена, а оно включает ожидание очереди шагов, поэтому величина ниже
// реальной скорости вычисления. Без слова «с ожиданием» показатель вводит в
// заблуждение — это про vLLM на любом стенде; у llama-server prompt_seconds_total —
// чистое время чтения, там слово было бы неправдой.
function SpeedCard({ data }: { data: LocalModelStats }) {
  return (
    <section>
      <h3 style={METRIC_HEAD}>Скорость</h3>
      {data.status === 'yielded' ? (
        <UnloadedNote text="Нет данных, пока модель выгружена. Последнее значение — на графике слева от разрыва." />
      ) : data.speed == null ? (
        <NotReported data={data} />
      ) : (
        <>
          {/* Имя говорит, что это НЕ та величина, с которой сверяют облачные оценки
              (для них — раздел «Скорость ответа» ниже): «пока пишет» = только время
              генерации, без ожидания слота и чтения промпта */}
          {metricRow('скорость генерации (пока пишет), ток/с, за последнюю минуту', data.speed.genPerStream, formatTps, GEN_SPEED_HINT)}
          {/* «с ожиданием» — по типу движка: у vLLM prompt-время включает очередь шагов
              (на любом стенде); у llama-server prompt_seconds_total — чистое время чтения,
              очередь в него не входит, слово было бы неправдой (макет §3) */}
          {metricRow(isLlamaServer(data)
            ? 'чтение промпта, ток/с, за последнюю минуту'
            : 'чтение промпта с ожиданием, ток/с, за последнюю минуту', data.speed.prefillPerStream, formatTps)}
        </>
      )}
    </section>
  );
}

// === «Скорость ответа»: сквозное определение, как у облачных оценок ===
// (фича llm-answer-speed, docs/features/llm-answer-speed-spec.md): первые строки —
// сквозная скорость за 5 минут (окно, как у OpenRouter), затем — за час; ниже — медианы
// по запросам и время до первого токена (медиана и p90) по тем же окнам.
// Окна берутся ПО КЛЮЧУ из ответа ('5m'/'1h'), а не по индексу: порядок в ответе не
// значим; окна нет — строки нет (выдуманного окна не придумываем).
// Нет данных — спокойная строка «нет данных», а не нули: ноль — это измеренное значение,
// а здесь измерять было нечего (у выгруженной модели — тот же тон, что у «Скорости»).
const AS_CELL_LABEL: CSSProperties = { fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs };
// Подписи окон в строках — из format.ts (единый источник), а не рукописные:
// переименование окна в одном месте меняет и строки, и подсказки.
const AS_5M = answerSpeedWindowLabel('5m');
const AS_1H = answerSpeedWindowLabel('1h');

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
  const hasAny = windows.some(w => w.endToEndTps != null || w.medianTps != null || w.medianTtftSec != null || w.p90TtftSec != null);
  // Число запросов в окне — припиской под значением: оно говорит, насколько
  // оценка опорная. Форма нейтральная (число после двоеточия): «N запросов» разъезжается
  // на 1 и 21, а таблица склонений ради этой строки не стоит.
  const requestsNote = (n?: number) => (
    n != null && Number.isFinite(n) ? `запросов в окне: ${n}` : undefined
  );
  return (
    <section>
      <h3 style={METRIC_HEAD}>Скорость ответа</h3>
      {data.status === 'yielded' ? (
        <UnloadedNote text="Нет данных, пока модель выгружена." />
      ) : !answer || windows.length === 0 || !hasAny ? (
        <UnloadedNote text="Нет данных: сквозной скорости ещё не измерено." />
      ) : (
        <>
          {metricRow(`сквозная скорость ответа, ${AS_5M}`, w5?.endToEndTps, formatTps, answerSpeedTpsHint(AS_5M), requestsNote(w5?.requests))}
          {metricRow(`сквозная скорость ответа, ${AS_1H}`, w1h?.endToEndTps, formatTps, answerSpeedTpsHint(AS_1H), requestsNote(w1h?.requests))}
          {metricRow(`медиана по запросам, ${AS_5M}`, w5?.medianTps, formatTps, answerSpeedMedianHint(AS_5M, minTokens))}
          {metricRow(`медиана по запросам, ${AS_1H}`, w1h?.medianTps, formatTps, answerSpeedMedianHint(AS_1H, minTokens))}
          {/* TTFT — группой под своей подписью: строки одного окна идут подряд,
              5 мин раньше часа (тот же порядок, что был в сетке) */}
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.md }}>Время до первого токена</div>
          {metricRow(`время до первого токена, медиана, ${AS_5M}`, w5?.medianTtftSec, formatSec, TTFT_MEDIAN_HINT)}
          {metricRow(`время до первого токена, p90, ${AS_5M}`, w5?.p90TtftSec, formatSec, TTFT_P90_HINT)}
          {metricRow(`время до первого токена, медиана, ${AS_1H}`, w1h?.medianTtftSec, formatSec, TTFT_MEDIAN_HINT)}
          {metricRow(`время до первого токена, p90, ${AS_1H}`, w1h?.p90TtftSec, formatSec, TTFT_P90_HINT)}
        </>
      )}
    </section>
  );
}

// === «Сравнить с облаком» — последней ЗАКРЫТОЙ строкой вкладки (Ф-4) ===
// Числа облака — сессионная скорость по нашей нагрузке за сутки (не per-request,
// как у локальных метрик выше): определение разное, и подсказка тела говорит это прямо.
// 403 (не админ) и отсутствие снимка опроса — секции нет вовсе. Пустое окно — та же
// закрытая строка с подписью «нет ходов за сутки», без кнопки и пустого тела.
const CLOUD_GRID: CSSProperties = {
  display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(96px, 1fr))', gap: SP.sm,
};
const CLOUD_ROW: CSSProperties = {
  borderTop: `1px solid ${C.borderLight}`, paddingTop: SP.sm, marginTop: SP.sm,
};
// Подпись разницы окон — явная и в закрытой строке, и в теле (макет .html:212):
// локальные метрики выше — окна 5 мин / 1 ч, облако — сессионные сутки.
const CLOUD_WINDOW_HINT = 'Окна разные: у локальных метрик выше — 5 минут и час, здесь — последние 24 часа. Сравниваем по смыслу, а не как точный забег.';
// Приписка закрытой строки: «моделей: N · за сутки» — нейтральная форма без
// склонений («3 модели» / «1 моделей» разъезжаются; та же цена, что у «запросов в окне: N»).
const cloudMeta = (n: number) => `моделей: ${n} · за сутки`;

// Ключ раскрытия облака — выбор человека, переживает перезаход (спека §v1, строка 126;
// макет .html:239): '1' — открыто, всё остальное — закрыто. Чтение и запись — с
// try/catch (паттерн loadTab/changeTab в LocalModelPanel): в приватном режиме
// исключение не выходит наружу, раскрытие продолжает работать в памяти.
const CLOUD_OPEN_KEY = 'cc_local_llm_cloud_open';
function readCloudOpen(): boolean {
  try {
    return localStorage.getItem(CLOUD_OPEN_KEY) === '1';
  } catch { /* приватный режим: закрыто, состояние живёт в памяти */ }
  return false;
}

// Тело = ПОЛНОЕ прежнее содержимое карточки облака (спека §v1, строка 125): все
// модели, провайдеры, три числа, подсказки и подпись окна. Внешняя рамка Card внутрь
// раскрытия не вкладывается — shell DisclosureSection уже несёт рамку (макет .html:208).
function CloudBody({ data }: { data: NonNullable<CloudAnswerSpeedState['data']> }) {
  const models = data.models ?? [];
  const minTokens = data.minTokensForMedian;
  return (
    <>
      {models.map(m => (
        <div key={`${m.provider}/${m.model}`} style={CLOUD_ROW}>
          <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{m.model}</div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>{m.provider}</div>
          <div style={{ ...CLOUD_GRID, marginTop: SP.sm }}>
            {asCell(m.endToEndTps, formatTps, 'сквозная скорость', cloudSpeedTpsHint())}
            {asCell(m.medianTps, formatTps, 'медиана по ходам', cloudSpeedMedianHint(minTokens))}
            {asCell(m.requests, n => String(n), 'ходов за сутки', CLOUD_SPEED_REQUESTS_HINT)}
          </div>
        </div>
      ))}
      {/* Подпись окна статична: окно — последние 24 часа, его считает бэкенд; дата-пара
          «за 19.11 — 20.11» была бы неправдой (хвост ревью aa8af844, п.1) */}
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm }}>
        {CLOUD_SPEED_WINDOW_LABEL}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs, lineHeight: 1.45 }}>
        {CLOUD_WINDOW_HINT}
      </div>
      {/* Прежде эта фраза была хинтом заголовка карточки; у закрытой строки своего
          хинта нет — честное «TTFT у облака не меряем» остаётся видимым текстом тела */}
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs, lineHeight: 1.45 }}>
        {CLOUD_SPEED_NO_TTFT_HINT}
      </div>
    </>
  );
}

// Скелетон первого ответа облака — только внутри открытого тела (спека «Состояния
// всех вариантов», строка 112): закрытая ферма им не блокируется. Тот же C.bgInset
// и та же пульсация, что у TextTabSkeleton; reduced-motion — статично.
function CloudSkeleton() {
  const reduced = usePrefersReducedMotion();
  const pulse: CSSProperties = reduced ? {} : { animation: 'ccs-lm-skeleton 1.4s ease-in-out infinite' };
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, paddingTop: SP.md }} aria-hidden>
      <div style={{ background: C.bgInset, borderRadius: R.sm, height: 14, width: '55%', ...pulse }} />
      <div style={{ background: C.bgInset, borderRadius: R.sm, height: 44, ...pulse }} />
      <div style={{ background: C.bgInset, borderRadius: R.sm, height: 14, width: '70%', ...pulse }} />
    </div>
  );
}

function CloudCompareSection({ state, open, onToggle, isMobile }: {
  state: CloudAnswerSpeedState; open: boolean; onToggle: () => void; isMobile: boolean;
}) {
  if (state.forbidden) return null;
  const data = state.data;
  // Пустое окно (снимок есть, моделей нет) — статичная закрытая строка: без кнопки,
  // chevron и пустого тела (тот же статичный режим DisclosureSection, children === null).
  // Нулевые скорости не рисовать — здесь их и нет вовсе.
  if (data && (data.models ?? []).length === 0) {
    return (
      <DisclosureSection title="Сравнить с облаком" meta="нет ходов за сутки" open={false} onToggle={onToggle} isMobile={isMobile}>
        {null}
      </DisclosureSection>
    );
  }
  return (
    <DisclosureSection
      title="Сравнить с облаком"
      // Припиской закрытой строки бывает только факт снимка («моделей: N · за сутки»);
      // без снимка сказать нечего — пустой meta, не пояснение про окна в этой строке
      // (разница окон остаётся в теле, где она и нужна).
      meta={data ? cloudMeta((data.models ?? []).length) : undefined}
      open={open}
      onToggle={onToggle}
      isMobile={isMobile}
    >
      {data
        ? <CloudBody data={data} />
        : state.loading ? <CloudSkeleton /> : <UnloadedNote text="Нет данных: за сутки не было ни одного хода облачной модели." />}
    </DisclosureSection>
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
          называет, что в неё входит: ожидание слота в движке и чтение промпта — входят
          (заголовки Strata шлёт до них), очередь CCS до отправки — нет; см. WHO_SPEED_HINT (format.ts) */}
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
export function TextTab({ data, loading, error, isMobile, onOpenChat, onRefetch, onOpenHardware, onOpenMedia, window, onWindowChange, engines, engine = 'main', onEngineChange, cloudSpeed }: {
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
  // «Облачные модели: скорость ответа» — снимок из своего опроса (раз в минуту);
  // без него карточки нет (не-админу карточка и не положена).
  cloudSpeed?: CloudAnswerSpeedState;
}) {
  // «Точные метрики локальной модели» — закрыты по умолчанию: первый экран отвечает
  // на главный вопрос без чтения всех метрик (спека §v1, строки 50–52). Состояние
  // держит вкладка; хук — над ранними return: порядок хуков не должен зависеть от
  // того, какое состояние вкладки рисуется.
  const [metricsOpen, setMetricsOpen] = useState(false);

  // «Сравнить с облаком» — закрыто по умолчанию; сохранённый выбор ('1') открывает
  // при монтировании. Следующее значение — от актуального ref-зеркала, а не из
  // замыкания рендера: два клика до ререндера дают два реальных переключения, и
  // финальные aria-expanded и значение ключа совпадают (запись синхронная,
  // блокировок и вторых таймеров нет).
  const [cloudOpen, setCloudOpen] = useState(readCloudOpen);
  const cloudOpenRef = useRef(cloudOpen);
  const toggleCloud = useCallback(() => {
    const next = !cloudOpenRef.current;
    cloudOpenRef.current = next;
    setCloudOpen(next);
    try { localStorage.setItem(CLOUD_OPEN_KEY, next ? '1' : '0'); } catch { /* приватный режим: состояние живёт в памяти */ }
  }, []);

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
        {/* v1: первый экран отвечает на один вопрос — жива ли модель, есть ли очередь
            и насколько быстро она отвечает (макет .html:169–176) */}
        <FarmKpiCard data={data} isMobile={isMobile} />
        <ChartCard data={data} window={window} onWindowChange={onWindowChange} isMobile={isMobile} />
        {/* «Кто сейчас на модели» — одна карточка: main без мест, named — чаты своего
            снимка и места из duty.callsNow (F3) */}
        <WhoCard data={data} isMobile={isMobile} onOpen={onOpenChat} named={named} />
        {/* Видеокарты: основной стенд — строка-ссылка на «Стенды» (хост CCS, только
            админ); второй движок — своя карточка Video, ссылка на «Стенды» была бы неправдой */}
        {named
          ? <VideoCard data={data} />
          : <GpusCard data={data} onOpenHardware={onOpenHardware} />}
        {/* «Дежурство» — последний ЛОКАЛЬНЫЙ блок перед вторичными раскрытиями
            (спека «Состояния всех вариантов», строка 119): только у выбранного
            второй движок; без duty блока нет — DutyBlock сам возвращает null (DutyBlock.tsx:222).
            Внешний guard по data.duty повторял это условие и маскировал в тестах условие
            движка (R-F5) — оставлено единственное условие: движок */}
        {named ? <DutyBlock data={data} isMobile={isMobile} onOpenMedia={onOpenMedia} /> : null}
        {/* «Точные метрики локальной модели» — закрытое по умолчанию раскрытие (v1):
            содержимое — ПОЛНЫЕ «Скорость» и «Скорость ответа», без урезания полей
            (спека §v1, строка 125), — плоскими строками без Card-обёрток (решение Майи,
            макет .html:194–205): границу группы несёт shell раскрытия. Тело смонтировано
            и в закрытом состоянии (display:none, как в AccordionSection) — содержимое
            не пересобирается при открытии, раскрытие ничего не перезапускает. */}
        <DisclosureSection
          title="Точные метрики локальной модели"
          meta="TTFT · медианы"
          open={metricsOpen}
          onToggle={() => setMetricsOpen(v => !v)}
          isMobile={isMobile}
        >
          <SpeedCard data={data} />
          <AnswerSpeedCard data={data} />
        </DisclosureSection>
        {/* Облачное сравнение — последней ЗАКРЫТОЙ строкой вкладки (Ф-4, макет .html:206–214):
            сначала локальное (per-request), затем облачное (сессионная) — определения разные.
            Состояние раскрытия — localStorage вкладки; опрос облака не меняется. */}
        {cloudSpeed ? <CloudCompareSection state={cloudSpeed} open={cloudOpen} onToggle={toggleCloud} isMobile={isMobile} /> : null}
      </div>
    </>
  );
}
