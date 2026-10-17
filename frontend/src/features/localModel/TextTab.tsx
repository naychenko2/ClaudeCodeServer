// Вкладка «LLM» панели «Локальные модели»: сверху вниз — жива ли модель →
// очередь → как быстро → нагрузка и кэш → что с картами → кто на ней
// (макет docs/mockups/local-model-stats-panel.html, раздел «Спецификация для Киры»).
//
// Состояния: notConnected — пустое состояние с кнопкой в «Поставщики моделей»;
// down — баннер «не отвечает с HH:MM» + последние данные приглушённые (opacity .55,
// pointer-events: none); загрузка — скелетоны; поле в `unsupported` — строка
// «Движок {имя} это не сообщает» вместо блока (нули не рисуем).

import { type CSSProperties } from 'react';
import { AlertTriangle, Plug, FolderOpen } from 'lucide-react';
import { C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { Button, EmptyState } from '../../components/ui';
import { requestOpenModelsSpend } from '../../lib/modelProvidersNav';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import type { LocalModelStats } from './api';
import { formatSince, formatTps, formatUpdatedAt, gpuCountLabel, whoRows, type WhoRow } from './format';
import { useAllProjects } from '../projects/useAllProjects';
import { GpuCard } from './GpuCard';
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
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
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

// «Движок {имя} это не сообщает» — вместо блока, чьё поле движок не отдал
// (не рисовать нули: ноль — это значение, а не отсутствие данных)
function NotReported({ data }: { data: LocalModelStats }) {
  const engine = data.engine?.trim() || 'движок';
  return (
    <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>
      Движок {engine} это не сообщает
    </p>
  );
}

// === Статус: имя модели + пилюля + подстрока ===
// Пилюля: R.max, FS.xs 700, точка 7 px; работает — success, занята — warning,
// недоступна — danger. Имя — FONT.mono FS.base 700, многоточие.
const STATUS_LABEL: Record<LocalModelStats['status'], string> = {
  working: 'Работает',
  busy: 'Занята',
  down: 'Недоступна',
  notConnected: 'Не подключена',
};
const STATUS_TONE: Record<LocalModelStats['status'], { bg: string; fg: string; dot: string }> = {
  working: { bg: C.successBg, fg: C.successText, dot: C.success },
  busy: { bg: C.warningBg, fg: C.warningText, dot: C.warning },
  down: { bg: C.dangerBg, fg: C.dangerText, dot: C.danger },
  notConnected: { bg: C.bgInset, fg: C.textMuted, dot: C.textMuted },
};

function StatusRow({ data }: { data: LocalModelStats }) {
  const tone = STATUS_TONE[data.status];
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
        {STATUS_LABEL[data.status]}
      </span>
    </div>
  );
}

// Подстрока под статусом: движок · N карт, TP=N · обновлено HH:MM:SS / данные на HH:MM
function StatusSubline({ data }: { data: LocalModelStats }) {
  const parts: string[] = [];
  if (data.engine) parts.push(data.engine);
  const n = data.gpus?.length ?? 0;
  if (n > 0) {
    // TP: из необязательного поля tensorParallel, иначе = число карт
    const tp = data.tensorParallel ?? n;
    parts.push(`${gpuCountLabel(n)}, TP=${tp}`);
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
// C.dangerBg/C.dangerBorder/C.dangerText; кнопка secondary «Проверить сейчас»
// запускает внеочередной опрос (refetch из панели).
function DownBanner({ data, onCheck }: { data: LocalModelStats; onCheck: () => void }) {
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
          Стенд не отвечает{since ? ` с ${since}` : ''}.
        </b>{' '}
        Новые ходы на локальной модели встанут в ошибку, пока он не поднимется.
        <div style={{ marginTop: SP.sm }}>
          <Button variant="ghost" size="xs" onClick={onCheck}>Проверить сейчас</Button>
        </div>
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

function NowCard({ data }: { data: LocalModelStats }) {
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
function SpeedCard({ data }: { data: LocalModelStats }) {
  if (data.speed == null) {
    return <Card title="Скорость"><NotReported data={data} /></Card>;
  }
  return (
    <Card title="Скорость">
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: SP.sm }}>
        <Kpi
          value={formatTps(data.speed.genPerStream)}
          label="генерация на агента, ток/с, за последнюю минуту"
        />
        <Kpi value={formatTps(data.speed.prefillPerStream)} label="чтение промпта, ток/с, за последнюю минуту" />
      </div>
    </Card>
  );
}

// === «Видеокарты»: карточка на каждую карту текстового стенда ===
// gpus === null — блока нет вовсе (макет: карта медиа на другом узле сюда не
// попадает); пустой массив — тоже нет.
function GpusCard({ data }: { data: LocalModelStats }) {
  if (data.gpus == null || data.gpus.length === 0) return null;
  return (
    <Card title="Видеокарты">
      {/* auto-fit, а не auto-fill: одна карта тянется на всю ширину, несколько делят её поровну */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: SP.sm }}>
        {data.gpus.map(g => (
          <GpuCard key={g.index} gpu={g} total={data.gpus!.length} kind="text" />
        ))}
      </div>
    </Card>
  );
}

// === «Нагрузка и кэш»: выработка (ток/с) + график «Кэш, %» + полоса «Вытеснения»
// на общей оси времени. Окно держит LocalModelPanel (его же читает серверный опрос),
// переключатель стоит в шапке карточки (как в макете).
function ChartCard({ data, window, onWindowChange, isMobile }: {
  data: LocalModelStats; window: Window; onWindowChange: (w: Window) => void; isMobile: boolean;
}) {
  return (
    <Card title="Нагрузка и кэш" right={<WindowToggle value={window} onChange={onWindowChange} isMobile={isMobile} />}>
      <ThroughputChart history={data.history} window={window} engine={data.engine} />
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
export function TextTab({ data, loading, isMobile, onOpenChat, onRefetch, window, onWindowChange }: {
  data: LocalModelStats | null;
  loading: boolean;
  isMobile: boolean;
  onOpenChat: (sessionId: string) => void;
  onRefetch: () => void;
  window: Window;
  onWindowChange: (w: Window) => void;
}) {
  // Первое открытие до первого ответа — скелетоны
  if (loading && !data) return <TextTabSkeleton />;
  if (!data) return null;
  if (data.status === 'notConnected') return <NotConnected />;

  const down = data.status === 'down';
  // Приглушение последних данных: opacity .55 + grayscale, клики не работают
  const stale: CSSProperties = down
    ? { opacity: 0.55, filter: 'grayscale(0.6)', pointerEvents: 'none' }
    : {};

  return (
    <>
      {down && <DownBanner data={data} onCheck={onRefetch} />}
      <StatusRow data={data} />
      <StatusSubline data={data} />
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, ...stale }}>
        <NowCard data={data} />
        <SpeedCard data={data} />
        <ChartCard data={data} window={window} onWindowChange={onWindowChange} isMobile={isMobile} />
        {/* «Видеокарты» — детали железа (nvidia-smi), а не живая нагрузка */}
        <GpusCard data={data} />
        <WhoCard data={data} isMobile={isMobile} onOpen={onOpenChat} />
      </div>
    </>
  );
}
