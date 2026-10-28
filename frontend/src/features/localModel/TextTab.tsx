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
// Стенд второй модели (Bonsai, макет docs/mockups/bonsai-toolbar.md): тот же порядок
// блоков, движок — параметр `engine`. Отличия по макету §3–§4:
// — первой строкой — переключатель «Основная · Bonsai», виден только при двух
//   настроенных движках (список — GET /api/local-model/engines);
// — статус 'yielded' разводится точными подписями по состоянию арбитра (format.ts);
//   уступка — нейтральный баннер и нейтральный тон, НЕ авария и не приглушение;
// — при выгруженной модели «Сейчас»/«Скорость» — спокойная строка, не нули;
// — поля из `unsupported` (Кэш, Вытеснения, время агентов) скрыты целиком;
// — видеокарта — своя карточка «Видеокарта · Video» с чипом «общая с ComfyUI»
//   (данные с узла ComfyUI на Video; «Железо» — хост CCS, ссылки туда нет);
// — «Кто сейчас на модели» — места сервера из duty.callsNow, не чаты;
// — «Дежурство» (DutyBlock) — последним блоком, только у выбранного Bonsai и только
//   если снимок принёс duty (макет §3/§5); переход «→ Медиа» — через onOpenMedia от панели.

import { type CSSProperties } from 'react';
import { AlertTriangle, CirclePause, Plug, FolderOpen, Unplug } from 'lucide-react';
import { C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { Button, EmptyState, InlineSegmented } from '../../components/ui';
import { requestOpenModelsSpend } from '../../lib/modelProvidersNav';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import type { LocalModelEngine, LocalModelEngineName, LocalModelStats } from './api';
import type { StatsPollError } from './useLocalModelStats';
import {
  formatGpuMem, formatPower, formatSince, formatTps, formatUpdatedAt, gpuCountLabel, notReportedText, whoRows,
  standDot, standHint, statusPill, type StatusTone, type WhoRow,
} from './format';
import { dutyWhoRows } from './dutyFormat';
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
// Стенд Bonsai (макет §3, .html:414): движок · Video · … — карта на Video одна и общая с
// ComfyUI, её данные — с узла ComfyUI: число карт и TP там не о чём, по макету стоит
// положение стенда «Video» (тот же факт, что в заголовке VideoCard).
function StatusSubline({ data, bonsai }: { data: LocalModelStats; bonsai?: boolean }) {
  const parts: string[] = [];
  if (data.engine) parts.push(data.engine);
  if (bonsai) {
    parts.push('Video');
  } else {
    const n = data.gpus?.length ?? 0;
    if (n > 0) {
      // TP: из необязательного поля tensorParallel, иначе = число карт
      const tp = data.tensorParallel ?? n;
      parts.push(`${gpuCountLabel(n)}, TP=${tp}`);
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

// === Баннер «Стенд не отвечает» ===
// C.dangerBg/C.dangerBorder/C.dangerText; кнопка ghost «Проверить сейчас»
// запускает внеочередной опрос (refetch из панели).
// Для стенда Bonsai — свой текст (макет §4): 'down' у движка с арбитром означает,
// что арбитр её НЕ выгружал, — это авария, а не уступка (Б3b).
function DownBanner({ data, onCheck, bonsai }: { data: LocalModelStats; onCheck: () => void; bonsai?: boolean }) {
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
          {bonsai
            ? `Bonsai не отвечает${since ? ` с ${since}` : ''}, а арбитр её не выгружал.`
            : `Стенд не отвечает${since ? ` с ${since}` : ''}.`}
        </b>{' '}
        {bonsai ? 'Это авария, не уступка: места идут на MiniMax.' : 'Новые ходы на локальной модели встанут в ошибку, пока он не поднимется.'}
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
    state === 'waiting-vram' ? 'Bonsai ждёт память карты.'
      : state === 'starting' ? 'Bonsai загружается.'
        : 'Bonsai уступила карту — это по плану.';
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
function Kpi({ value, unit, label, note, warn }: { value: string; unit?: string; label: string; note?: string; warn?: boolean }) {
  return (
    <div>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.xl, fontWeight: 700, lineHeight: 1.2, color: warn ? C.warningText : C.textHeading }}>
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

// === «Скорость»: генерация и чтение промпта, ток/с на поток, «сейчас» (окно ~60 с) ===
// Два KPI — «сейчас» (genPerStream/prefillPerStream, как чувствует отдельный чат).
// Среднее и пик за окно графика — ТОЛЬКО у графика (дубль под KPI убран).
// «Чтение промпта С ОЖИДАНИЕМ»: знаменатель — время prefill запроса от старта до
// первого токена, а оно включает ожидание очереди шагов, поэтому величина ниже
// реальной скорости вычисления. Без слова «с ожиданием» показатель вводит в
// заблуждение.
function SpeedCard({ data, bonsai }: { data: LocalModelStats; bonsai?: boolean }) {
  if (data.status === 'yielded') {
    return <Card title="Скорость"><UnloadedNote text="Нет данных, пока модель выгружена. Последнее значение — на графике слева от разрыва." /></Card>;
  }
  if (data.speed == null) {
    return <Card title="Скорость"><NotReported data={data} /></Card>;
  }
  return (
    <Card title="Скорость">
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: SP.sm }}>
        <Kpi
          value={formatTps(data.speed.genPerStream)}
          label="генерация на запрос, ток/с, за последнюю минуту"
        />
        {/* «с ожиданием» — про vLLM: его prompt-время включает очередь шагов. У
            llama-server (стенд Bonsai) prompt_seconds_total — чистое время чтения,
            очередь в него не входит, слово было бы неправдой (макет §3) */}
        <Kpi value={formatTps(data.speed.prefillPerStream)} label={bonsai
          ? 'чтение промпта, ток/с, за последнюю минуту'
          : 'чтение промпта с ожиданием, ток/с, за последнюю минуту'} />
      </div>
    </Card>
  );
}

// === «Видеокарты»: строка-ссылка на вкладку «Железо» (только админ) ===
// GpuCard ушёл на «Железо» (GpuDetailCard); здесь — компактная строка-ссылка:
// точка худшего уровня, «память 68° · 81°», плашки, «Железо →». Касание открывает
// «Железо». hardware — только у админа (не-админу поля нет → строки нет, ADR-030).
function GpusCard({ data, onOpenHardware }: { data: LocalModelStats; onOpenHardware: () => void }) {
  if (!data.hardware || data.hardware.gpus.length === 0) return null;
  return <HwLinkRow brief={data.hardware} onOpen={onOpenHardware} />;
}

// === «Видеокарта · Video» (стенд Bonsai, макет §3/§6) ===
// Данные — с узла ComfyUI на Video (b.gpus в ответе stats?engine=bonsai): «Железо» —
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
// заглушки «Движок это не сообщает» трижды подряд были бы шумом). Когда скрыто всё
// кэш-содержание, карточка называется «Нагрузка» (макет §3), и к графику добавляется
// подпись про ступеньки: счётчики llama-server прирастают по завершении запроса.
function kvHidden(data: LocalModelStats): boolean {
  return (data.unsupported ?? []).includes('KvUsagePerc');
}

function ChartCard({ data, window, onWindowChange, isMobile, bonsai }: {
  data: LocalModelStats; window: Window; onWindowChange: (w: Window) => void; isMobile: boolean; bonsai?: boolean;
}) {
  const hideKv = kvHidden(data);
  return (
    <Card title={hideKv ? 'Нагрузка' : 'Нагрузка и кэш'} right={<WindowToggle value={window} onChange={onWindowChange} isMobile={isMobile} />}>
      <ThroughputChart
        history={data.history} window={window} engine={data.engine}
        cleanStepMs={data.cleanStepMs ?? null} unsupported={data.unsupported}
      />
      {bonsai && hideKv && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.45 }}>
          llama-server обновляет счётчики, когда запрос закончен, поэтому график идёт ступеньками.
        </div>
      )}
    </Card>
  );
}

// === «Кто сейчас на модели» ===
// Строка — кнопка (36 desktop / 44 touch): аватар 24, имя FS.sm 600 с
// многоточием, проект FS.xs C.textMuted, справа скорость. Свой чат — клик
// открывает его; чужой — «Чат другого пользователя» без названия и перехода;
// «Прочие запросы: N» — хвост списка.
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
  const speedColor = row.tone === 'wait' ? C.warningText : row.tone === 'read' ? C.textMuted : C.textSecondary;
  const speedStyle: CSSProperties = {
    fontFamily: FONT.mono, fontSize: FS.sm, whiteSpace: 'nowrap',
    color: speedColor, fontWeight: row.tone === 'gen' ? 700 : 400,
  };
  const inner = (
    <>
      <span style={{
        width: 24, height: 24, flex: '0 0 24px', borderRadius: R.full,
        background: C.accentMuted, color: C.textHeading, fontSize: 10, fontWeight: 700,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>{(row.title[0] ?? '·').toUpperCase()}</span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <b style={{
          display: 'block', fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{row.title}</b>
        {row.project && (
          <span style={{
            display: 'block', fontSize: FS.xs, color: C.textMuted,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{row.project}</span>
        )}
      </span>
      <span style={speedStyle}>{row.speed}</span>
    </>
  );
  const base: CSSProperties = {
    display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: minH,
    padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.md, width: '100%',
    background: 'transparent', border: 'none', color: 'inherit', cursor: 'default',
  };
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

function WhoCard({ data, isMobile, onOpen }: { data: LocalModelStats; isMobile: boolean; onOpen: (id: string) => void }) {
  // Имя проекта — из общего стора проектов по projectId (бэк отдаёт только id,
  // и только для своих чатов); чужой чат проекта не получает вовсе
  const projects = useAllProjects();
  if (data.chats == null && (data.othersCount ?? 0) === 0) {
    return (
      <Card title="Кто сейчас на модели">
        <NotReported data={data} />
      </Card>
    );
  }
  const rows = whoRows(data.chats, data.othersCount, id => id ? projects.find(p => p.id === id)?.name : undefined);
  if (rows.length === 0) {
    return (
      <Card title="Кто сейчас на модели">
        <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted }}>Пока никто</p>
      </Card>
    );
  }
  // Честный счётчик: число чатов + «прочие запросы» (строка «Прочие: N» — один элемент
  // списка, но N запросов). Не rows.length: он считал бы «прочие» за 1.
  const total = (data.chats?.length ?? 0) + (data.othersCount ?? 0);
  return (
    <Card
      title="Кто сейчас на модели"
      right={<span style={{ fontSize: FS.xs, color: C.textMuted, fontWeight: 400 }}>{total}</span>}
    >
      <div style={{ display: 'flex', flexDirection: 'column', margin: `0 ${-SP.sm}px` }}>
        {rows.map(r => <WhoRowView key={r.key} row={r} isMobile={isMobile} onOpen={onOpen} />)}
      </div>
    </Card>
  );
}

// === «Кто сейчас на модели» (стенд Bonsai): места сервера, не чаты (макет §3/§4) ===
// Строки из duty.callsNow (журнал EngineCallLedger): не кликабельны, владельца и
// промпта нет; вместо проекта — звено цепочки («Bonsai» / «MiniMax · страховка» /
// «ждёт в очереди»); справа — длительность вызова. Подпись говорит, что это не чаты,
// чтобы строку не пытались открыть.
function WhoPlacesCard({ data, isMobile }: { data: LocalModelStats; isMobile: boolean }) {
  const rows = dutyWhoRows(data.duty?.callsNow);
  const minH = isMobile ? 44 : 36;
  return (
    <Card
      title="Кто сейчас на модели"
      right={rows.length ? <span style={{ fontSize: FS.xs, color: C.textMuted, fontWeight: 400 }}>{rows.length}</span> : undefined}
    >
      {rows.length === 0 ? (
        <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted }}>Пока никто</p>
      ) : (
        <>
          <div style={{ display: 'flex', flexDirection: 'column', margin: `0 ${-SP.sm}px` }}>
            {rows.map(r => (
              <div key={r.key} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: minH, padding: `${SP.sm}px ${SP.sm}px` }}>
                <span style={{
                  width: 24, height: 24, flex: '0 0 24px', borderRadius: R.full,
                  background: C.accentMuted, color: C.textHeading, fontSize: 10, fontWeight: 700,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                }}>{(r.place[0] ?? '·').toUpperCase()}</span>
                <span style={{ flex: 1, minWidth: 0 }}>
                  <b style={{
                    display: 'block', fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                    whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                  }}>{r.place}</b>
                  <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted }}>{r.linkText}</span>
                </span>
                {/* длительность ждущего вызова — warning: он ещё не начался */}
                {r.duration && (
                  <span style={{
                    fontFamily: FONT.mono, fontSize: FS.sm, whiteSpace: 'nowrap',
                    color: r.queued ? C.warningText : C.textSecondary,
                  }}>{r.duration}</span>
                )}
              </div>
            ))}
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm }}>
            Здесь места сервера, а не чаты: строки не кликабельны, владельца и промпта нет.
          </div>
        </>
      )}
    </Card>
  );
}

// === Переключатель «Основная · Bonsai» (макет §2) ===
// Первая строка вкладки, над стендом. Виден ТОЛЬКО когда настроены оба движка:
// с одним движком стенд открывается сразу, переключателя нет. Подписи — titles из
// /engines («Основная», «Bonsai»): имена моделей в сегмент не ставим (на 320 не
// помещаются, имя и так стоит первой строкой стенда). Точка 6 px и подсказка на
// сегменте «Bonsai» — только когда выбран сам «Bonsai» и по ЕГО снимку: data —
// снимок текущего движка, состояние Bonsai с основного стенда неизвестно (при
// падении основного старая логика зажигала на сегменте «Bonsai недоступна» —
// неправда). Состояние Bonsai на основном стенде придёт в Ф6 из лёгкого опроса
// useLocalModelWatcher. title/aria-label сегмента — та же подсказка, что у рельса.
function EngineSwitch({ data, engines, engine, onChange, isMobile }: {
  data: LocalModelStats; engines: LocalModelEngine[]; engine: LocalModelEngineName;
  onChange: (e: LocalModelEngineName) => void; isMobile: boolean;
}) {
  const configured = engines.filter(e => e.configured);
  if (configured.length < 2) return null;
  // Точка и подсказка — только по снимку выбранного движка (составитель — format.ts)
  const bonsaiSelected = engine === 'bonsai';
  const dot = bonsaiSelected ? standDot(data) : null;
  const hint = bonsaiSelected ? standHint(data) : null;
  return (
    <InlineSegmented<LocalModelEngineName>
      value={engine}
      full
      isMobile={isMobile}
      size="sm"
      options={configured.map(e => ({
        value: e.name,
        label: e.title,
        // как в макете: «Bonsai — уступила карту» (имя один раз, подсказка без префикса)
        title: e.name === 'bonsai' && dot && hint ? `Bonsai — ${hint.replace(/^Bonsai:? ?/, '')}` : undefined,
        icon: e.name === 'bonsai' && dot ? (
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
    <EmptyState
      icon={offline
        ? <Unplug size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
        : <AlertTriangle size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
      title={offline ? 'Нет связи с сервером' : 'Не удалось получить данные'}
      subtitle={offline
        ? 'Данные появятся, как только связь вернётся.'
        : 'Повторим автоматически.'}
    />
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
  // Порядок состояний (Ф10): известная причина важнее скелетона — при сбое и
  // идущем параллельно повторном опросе (loading) карточка остаётся спокойной и
  // не мигает скелетоном каждые 2 с. Скелетон — только когда причины ещё нет:
  // первое открытие до первого ответа.
  if (!data && error) return <NoConnection error={error} />;
  if (loading && !data) return <TextTabSkeleton />;
  if (!data) return null;
  if (data.status === 'notConnected') return <NotConnected />;

  const bonsai = engine === 'bonsai';
  const down = data.status === 'down';
  const yielded = data.status === 'yielded';
  // Приглушение последних данных — только у аварии. Уступка не приглушается:
  // стенд жив, это плановая выгрузка модели (макет §4)
  const stale: CSSProperties = down
    ? { opacity: 0.55, filter: 'grayscale(0.6)', pointerEvents: 'none' }
    : {};

  return (
    <>
      {engines && onEngineChange && (
        <EngineSwitch data={data} engines={engines} engine={engine} onChange={onEngineChange} isMobile={isMobile} />
      )}
      {down && <DownBanner data={data} onCheck={onRefetch} bonsai={bonsai} />}
      {yielded && <YieldBanner data={data} />}
      <StatusRow data={data} />
      <StatusSubline data={data} bonsai={bonsai} />
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, ...stale }}>
        <NowCard data={data} />
        <SpeedCard data={data} bonsai={bonsai} />
        <ChartCard data={data} window={window} onWindowChange={onWindowChange} isMobile={isMobile} bonsai={bonsai} />
        {/* Видеокарты: основной стенд — строка-ссылка на «Железо» (хост CCS, только
            админ); Bonsai — своя карточка Video, ссылка на «Железо» была бы неправдой */}
        {bonsai
          ? <VideoCard data={data} />
          : <GpusCard data={data} onOpenHardware={onOpenHardware} />}
        {bonsai
          ? <WhoPlacesCard data={data} isMobile={isMobile} />
          : <WhoCard data={data} isMobile={isMobile} onOpen={onOpenChat} />}
        {/* «Дежурство» — последний блок стенда Bonsai (макет §3/§5): только у выбранного
            Bonsai; без duty блока нет — DutyBlock сам возвращает null (DutyBlock.tsx:222).
            Внешний guard по data.duty повторял это условие и маскировал в тестах условие
            движка (R-F5) — оставлено единственное условие: движок */}
        {bonsai ? <DutyBlock data={data} isMobile={isMobile} onOpenMedia={onOpenMedia} /> : null}
      </div>
    </>
  );
}
