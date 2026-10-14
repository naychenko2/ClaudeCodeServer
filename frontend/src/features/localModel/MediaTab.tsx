// Вкладка «Медиа» панели «Локальные модели»: ComfyUI на второй видеокарте —
// картинки, видео, звук. Сверху вниз (макет local-model-stats-panel.html, раздел
// «Медиа · спецификация для Киры»): жив ли ComfyUI и что в памяти → карта →
// что считается → очередь → последний час. Только просмотр: отменять/переставлять
// чужие задачи отсюда нельзя (свою — из её карточки в чате).
//
// Состояния: configured:false (comfy.status=disabled) — «не подключено»;
// down — баннер «не отвечает с HH:MM» + последние данные приглушённые;
// загрузка — скелетоны; карта gpus==null — деградация «карта не сообщает».

import { type CSSProperties, type ReactNode } from 'react';
import { AlertTriangle, Check, Image as ImageIcon, Clapperboard, AudioLines, Weight, Plug } from 'lucide-react';
import { C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { Button, EmptyState, ProgressBar } from '../../components/ui';
import { useAllProjects } from '../projects/useAllProjects';
import type { LocalModelMedia, MediaDone, MediaQueue, MediaRunning } from './api';
import {
  type ComfyTone, comfyStatusInfo, loadedModelLine, formatClockTime, formatClock,
  queueStartLine, isEtaOverdue, doneOutcomeLine, lastHourSummary, runningProgress,
  remainingLine, isOwnJob, jobOwnerLabel, slotsLine, queueJobLabel,
  LAST_HOUR_VISIBLE,
} from './mediaFormat';
import { GpuCard } from './GpuCard';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';

// === Оболочка секции ===
// Карточка: C.bgWhite (тот же фон, что у карточек соседних панелей и контентной
// зоны), рамка C.borderLight, R.xl, паддинг SP.md. Заголовок —
// 11 / 700 / uppercase / C.textMuted (как в TextTab).
function Card({ title, right, children }: { title: string; right?: ReactNode; children?: ReactNode }) {
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

// === Статус ComfyUI: имя + пилюля ===
// Пилюля: R.max, FS.xs 700, точка 7 px; работает — success, занят — warning,
// не отвечает — danger, выключено — muted.
const COMFY_TONE: Record<ComfyTone, { bg: string; fg: string; dot: string }> = {
  ok: { bg: C.successBg, fg: C.successText, dot: C.success },
  busy: { bg: C.warningBg, fg: C.warningText, dot: C.warning },
  down: { bg: C.dangerBg, fg: C.dangerText, dot: C.danger },
  disabled: { bg: C.bgInset, fg: C.textMuted, dot: C.textMuted },
};

function ComfyStatusRow({ media }: { media: LocalModelMedia }) {
  const info = comfyStatusInfo(media.comfy.status);
  const tone = COMFY_TONE[info.tone];
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
      <span style={{
        flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: FS.base, fontWeight: 700,
        color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>ComfyUI</span>
      <span style={{
        flexShrink: 0, display: 'inline-flex', alignItems: 'center', gap: 5,
        background: tone.bg, color: tone.fg, borderRadius: R.max,
        padding: `${SP.xxs}px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 700, whiteSpace: 'nowrap',
      }}>
        <span style={{ width: 7, height: 7, borderRadius: R.full, background: tone.dot, flexShrink: 0 }} />
        {info.label}
      </span>
    </div>
  );
}

// Подстрока: «1 карта · обновлено 2 с назад» / «данные на HH:MM» (down)
function ComfySubline({ media }: { media: LocalModelMedia }) {
  const parts: string[] = [];
  const n = media.gpus?.length ?? 0;
  if (n > 0) parts.push(`${n} карта`);
  const since = formatClockTime(media.comfy.downSince);
  parts.push(media.comfy.status === 'down' && since ? `данные на ${since}` : 'обновлено 2 с назад');
  return (
    <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: -SP.xs }}>
      {parts.join(' · ')}
    </div>
  );
}

// === «Модель в памяти» ===
// loaded — имя модели (FONT.mono 600 C.textPrimary); empty/unknown — C.textMuted.
function LoadedModelLine({ media }: { media: LocalModelMedia }) {
  const line = loadedModelLine(media);
  const loaded = line.state === 'loaded';
  return (
    <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
      Модель в памяти:{' '}
      <span style={{
        fontFamily: loaded ? FONT.mono : undefined,
        fontWeight: loaded ? 600 : 400,
        color: loaded ? C.textPrimary : C.textMuted,
      }}>{line.label}</span>
    </div>
  );
}

// === Видеокарта медиа ===
// gpus есть — карточка (GpuCard, «RTX 3090 · медиа»); gpus==null — деградация.
function MediaGpuCard({ media }: { media: LocalModelMedia }) {
  const gpus = media.gpus;
  if (gpus == null || gpus.length === 0) {
    return (
      <Card title="Видеокарта">
        <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>
          Карта не сообщает
        </p>
      </Card>
    );
  }
  return (
    <Card title="Видеокарта">
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))', gap: SP.sm }}>
        {gpus.map(g => (
          <GpuCard key={g.uuid || g.index} gpu={g} total={gpus.length} kind="media" />
        ))}
      </div>
    </Card>
  );
}

// === Плитка вида (вид прогона) ===
// 28 px, C.bgInset, R.md, иконка 15 C.textSecondary: Image / Clapperboard / AudioLines.
function kindIcon(op: string): ReactNode {
  const s = op.toLowerCase();
  if (s.includes('видео') || s.includes('video') || s.includes('сцен')) {
    return <Clapperboard size={15} strokeWidth={ICON_STROKE} />;
  }
  if (s.includes('картинк') || s.includes('изображ') || s.includes('image') || s.includes('обложк')) {
    return <ImageIcon size={15} strokeWidth={ICON_STROKE} />;
  }
  if (s.includes('звук') || s.includes('речь') || s.includes('песн') || s.includes('аудио') || s.includes('audio') || s.includes('tts') || s.includes('стем') || s.includes('обучени')) {
    return <AudioLines size={15} strokeWidth={ICON_STROKE} />;
  }
  return <Clapperboard size={15} strokeWidth={ICON_STROKE} />;
}

function KindTile({ op }: { op: string }) {
  return (
    <span style={{
      width: 28, height: 28, flex: '0 0 28px', borderRadius: R.md, background: C.bgInset,
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary,
    }}>{kindIcon(op)}</span>
  );
}

// === Бейдж «тяжёлая» ===
// C.warningBg/C.warningText, R.max, 10.5 px 700, иконка Weight; flex:none — не срезается.
function HeavyBadge() {
  return (
    <span style={{
      flex: 'none', display: 'inline-flex', alignItems: 'center', gap: 3,
      background: C.warningBg, color: C.warningText, borderRadius: R.max,
      padding: `1px ${SP.xs}px`, fontSize: 10.5, fontWeight: 700, whiteSpace: 'nowrap',
    }}>
      <Weight size={11} strokeWidth={ICON_STROKE} />
      тяжёлая
    </span>
  );
}

// === «Сейчас считается» ===
// Строка задачи: плитка вида, операция (FS.sm 600 C.textHeading, многоточие),
// бейдж «тяжёлая», чат (свой — название, чужой — «Чат другого пользователя»),
// полоса 4 px (шаг/шагов), под ней «Шаг X из Y» и «осталось ≈ N» / «время не замерено».
// Своя строка — кнопка (переход в чат); чужая — div без перехода.
function RunningRow({ r, isMobile, onOpen }: { r: MediaRunning; isMobile: boolean; onOpen: (id: string) => void }) {
  const projects = useAllProjects();
  const minH = isMobile ? 44 : 36;
  const progress = runningProgress(r);
  const own = isOwnJob(r);
  // Чат: свой — имя проекта (из стора по projectId; бэкенд не отдаёт название чата),
  // чужой — заглушка «Чат другого пользователя»
  const owner = r.own
    ? (r.projectId ? projects.find(p => p.id === r.projectId)?.name : undefined) ?? '—'
    : jobOwnerLabel(r);

  const inner = (
    <>
      <KindTile op={r.op} />
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
          <b style={{
            fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{r.op}</b>
          {r.heavy && <HeavyBadge />}
        </span>
        <span style={{
          display: 'block', fontSize: FS.xs, color: C.textMuted,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{owner}</span>
      </span>
    </>
  );

  const base: CSSProperties = {
    display: 'flex', alignItems: 'flex-start', gap: SP.sm, minHeight: minH,
    padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.md, width: '100%',
    background: 'transparent', border: 'none', color: 'inherit', cursor: 'default',
    boxSizing: 'border-box',
  };

  // Полоса + подписи: шаг/шагов слева, «осталось ≈ N» / «время не замерено» справа
  const bar = (
    <div style={{ marginTop: SP.xs }}>
      <ProgressBar
        value={Math.round((progress.fraction ?? 0) * 100)}
        tone="accent"
        estimate={progress.mode === 'estimate'}
        indeterminate={progress.mode === 'indeterminate'}
        size="thin"
      />
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, marginTop: SP.xxs }}>
        <span style={{ fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.mono }}>
          {r.steps != null && r.steps.total > 0 ? `Шаг ${r.steps.step} из ${r.steps.total}` : 'идёт'}
        </span>
        <span style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, whiteSpace: 'nowrap' }}>
          {remainingLine(r)}
        </span>
      </div>
    </div>
  );

  if (!own) {
    return (
      <div style={base}>
        {inner}
        {bar}
      </div>
    );
  }
  return (
    <button type="button" style={{ ...base, cursor: 'pointer' }} onClick={() => onOpen(r.sessionId!)} title="Открыть чат">
      {inner}
      {bar}
    </button>
  );
}

function RunningCard({ media, isMobile, onOpen }: { media: LocalModelMedia; isMobile: boolean; onOpen: (id: string) => void }) {
  if (media.running.length === 0) return null;
  return (
    <Card title="Сейчас считается">
      <div style={{ display: 'flex', flexDirection: 'column', margin: `0 ${-SP.sm}px` }}>
        {media.running.map((r, i) => (
          <RunningRow key={r.sessionId ?? `run-${i}`} r={r} isMobile={isMobile} onOpen={onOpen} />
        ))}
      </div>
      {media.running.some(r => r.heavy) && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, marginTop: SP.xs }}>
          Тяжёлая задача занимает карту целиком: очередь стартует после неё.
        </div>
      )}
    </Card>
  );
}

// === «Видеокарта свободна» (нет ни идущего, ни очереди) ===
function GpuFreeCard() {
  return (
    <Card title="Сейчас считается">
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={{
          width: 36, height: 36, flex: '0 0 36px', borderRadius: R.full,
          background: C.successBg, color: C.successText,
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </span>
        <div>
          <b style={{ display: 'block', fontSize: FS.sm, fontWeight: 700, color: C.textHeading }}>
            Видеокарта свободна
          </b>
          <span style={{ fontSize: FS.xs, color: C.textSecondary }}>Локальная генерация начнётся сразу</span>
        </div>
      </div>
    </Card>
  );
}

// === Счётчик мест очереди (ячейки 10×6) ===
// Занятые — C.accent, ожидающие — C.info, свободные — C.track.
function SlotDots({ used, queued, limit }: { used: number; queued: number; limit: number }) {
  const cells = Math.max(0, limit);
  return (
    <span style={{ display: 'inline-flex', gap: 3, verticalAlign: 'middle', marginRight: 6 }}>
      {Array.from({ length: cells }).map((_, i) => {
        const color = i < used ? C.accent : i < used + queued ? C.info : C.track;
        return <i key={i} style={{ width: 10, height: 6, borderRadius: R.max, background: color, display: 'block' }} />;
      })}
    </span>
  );
}

// === Строка очереди: только просмотр, без кнопок ===
function QueueRow({ q, isMobile, onOpen }: { q: MediaQueue; isMobile: boolean; onOpen: (id: string) => void }) {
  const projects = useAllProjects();
  const minH = isMobile ? 44 : 36;
  const own = isOwnJob(q);
  const owner = q.own
    ? (q.projectId ? projects.find(p => p.id === q.projectId)?.name : undefined) ?? '—'
    : jobOwnerLabel(q);
  const start = queueStartLine(q.startInSeconds, q.etaSeconds, Date.now(), isMobile);

  const inner = (
    <>
      <KindTile op={q.op} />
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
          <b style={{
            fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{queueJobLabel(q)}</b>
          {q.heavy && <HeavyBadge />}
        </span>
        <span style={{
          display: 'block', fontSize: FS.xs, color: C.textMuted,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{owner}</span>
      </span>
      <span style={{
        flexShrink: 0, textAlign: 'right', fontFamily: FONT.mono, fontSize: FS.xs,
        color: C.textSecondary, whiteSpace: 'nowrap',
      }}>{start}</span>
    </>
  );

  const base: CSSProperties = {
    display: 'flex', alignItems: 'flex-start', gap: SP.sm, minHeight: minH,
    padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.md, width: '100%',
    background: 'transparent', border: 'none', color: 'inherit', cursor: 'default',
    boxSizing: 'border-box',
  };

  if (!own) {
    return <div style={base}>{inner}</div>;
  }
  return (
    <button type="button" style={{ ...base, cursor: 'pointer' }} onClick={() => onOpen(q.sessionId!)} title="Открыть чат">
      {inner}
    </button>
  );
}

function QueueCard({ media, isMobile, onOpen }: { media: LocalModelMedia; isMobile: boolean; onOpen: (id: string) => void }) {
  const { used, limit } = media.slots;
  const queued = media.queue.length;
  return (
    <Card
      title="Очередь"
      right={
        <span style={{ display: 'inline-flex', alignItems: 'center', fontSize: FS.xs, color: C.textMuted }}>
          <SlotDots used={used} queued={queued} limit={limit} />
          {slotsLine(media.slots)}
        </span>
      }
    >
      {media.queue.length === 0 ? (
        <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted }}>Очередь пуста</p>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', margin: `0 ${-SP.sm}px` }}>
          {media.queue.map((q, i) => (
            <QueueRow key={q.sessionId ?? `q-${i}`} q={q} isMobile={isMobile} onOpen={onOpen} />
          ))}
        </div>
      )}
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, marginTop: SP.xs }}>
        Мест {limit} на всех. У одного человека до 2 задач, из них 1 тяжёлая.
      </div>
    </Card>
  );
}

// === «Последний час» ===
// Строка: плитка вида, операция, «готово в HH:MM» / «упала: …»; справа факт против
// обещанного ETA (подсветка при превышении на 20 %+). Только div, без перехода.
function DoneRow({ d }: { d: MediaDone }) {
  const overdue = isEtaOverdue(d.runSeconds, d.etaSeconds);
  const failed = d.outcome === 'failed';
  const fact = formatClock(d.runSeconds);
  const promised = d.etaSeconds != null ? `обещали ${formatClock(d.etaSeconds)}` : 'без замера';
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: SP.sm, minHeight: 36,
      padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.md, boxSizing: 'border-box',
    }}>
      <KindTile op={d.op} />
      <span style={{ flex: 1, minWidth: 0 }}>
        <b style={{
          display: 'block', fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{d.op}</b>
        <span style={{
          display: 'block', fontSize: FS.xs,
          color: failed ? C.dangerText : C.textMuted,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{doneOutcomeLine(d)}</span>
      </span>
      <span style={{
        flexShrink: 0, textAlign: 'right', fontFamily: FONT.mono, fontSize: FS.xs,
        color: overdue ? C.warningText : C.textSecondary, whiteSpace: 'nowrap', lineHeight: 1.4,
      }}>
        {fact}
        <br />
        <span style={{ color: C.textMuted }}>{promised}</span>
      </span>
    </div>
  );
}

function LastHourCard({ media }: { media: LocalModelMedia }) {
  const all = media.lastHour;
  if (all.length === 0) return null;
  const visible = all.slice(0, LAST_HOUR_VISIBLE);
  const summary = lastHourSummary(all);
  const avg = summary.avgOverPerc != null ? ` · в среднем на ${Math.round(summary.avgOverPerc * 100)} % дольше обещанного` : '';
  const more = summary.more > 0 ? ` · и ещё ${summary.more}` : '';
  return (
    <Card title="Последний час">
      <div style={{ display: 'flex', flexDirection: 'column', margin: `0 ${-SP.sm}px` }}>
        {visible.map((d, i) => <DoneRow key={`${d.finishedAt}-${i}`} d={d} />)}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>
        Готово {summary.done} · упало {summary.failed}{avg}{more}
      </div>
    </Card>
  );
}

// === Баннер «Локальная генерация недоступна» ===
function DownBanner({ media, onCheck }: { media: LocalModelMedia; onCheck: () => void }) {
  const since = formatClockTime(media.comfy.downSince);
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
          Локальная генерация недоступна{since ? ` с ${since}` : ''}.
        </b>{' '}
        Картинки можно рисовать в облаке.
        <div style={{ marginTop: SP.sm }}>
          <Button variant="ghost" size="xs" onClick={onCheck}>Проверить сейчас</Button>
        </div>
      </div>
    </div>
  );
}

// === «Не подключено» (comfy.status=disabled / configured:false) ===
function MediaNotConnected() {
  return (
    <EmptyState
      icon={<Plug size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
      title="ComfyUI не подключён"
      subtitle={
        <>
          Медиа-стенд (картинки, видео, звук) выключен. После подключения здесь появятся
          очередь, карта и «последний час».
        </>
      }
    />
  );
}

// === Загрузка: скелетоны ===
function MediaTabSkeleton() {
  const reduced = usePrefersReducedMotion();
  const pulse: CSSProperties = reduced ? {} : { animation: 'ccs-lm-skeleton 1.4s ease-in-out infinite' };
  const block = (h: number): CSSProperties => ({ background: C.bgInset, borderRadius: R.xl, height: h, ...pulse });
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }} aria-hidden>
      <div style={{ ...block(20), height: 20 }} />
      <div style={block(64)} />
      <div style={block(96)} />
      <div style={block(96)} />
    </div>
  );
}

// === Вкладка ===
export function MediaTab({ data, loading, isMobile, onOpenChat, onRefetch }: {
  data: LocalModelMedia | null;
  loading: boolean;
  isMobile: boolean;
  onOpenChat: (sessionId: string) => void;
  onRefetch: () => void;
}) {
  // Первое открытие до первого ответа — скелетоны
  if (loading && !data) return <MediaTabSkeleton />;
  if (!data) return null;
  if (!data.configured || data.comfy.status === 'disabled') return <MediaNotConnected />;

  const down = data.comfy.status === 'down';
  const stale: CSSProperties = down
    ? { opacity: 0.55, filter: 'grayscale(0.6)', pointerEvents: 'none' }
    : {};

  return (
    <>
      {down && <DownBanner media={data} onCheck={onRefetch} />}
      <ComfyStatusRow media={data} />
      <ComfySubline media={data} />
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, ...stale }}>
        <LoadedModelLine media={data} />
        <MediaGpuCard media={data} />
        {data.running.length === 0 && data.queue.length === 0
          ? <GpuFreeCard />
          : <>
              <RunningCard media={data} isMobile={isMobile} onOpen={onOpenChat} />
              <QueueCard media={data} isMobile={isMobile} onOpen={onOpenChat} />
            </>}
        <LastHourCard media={data} />
      </div>
    </>
  );
}
