// Вкладка «Инциденты» раздела «Телеметрия»: слева список горящих и недавних, справа —
// досье по выбранному (разрез по тегам, упавшие ходы, затронутые чаты, логи окна).
//
// Мобила живёт по рецепту раздела: два ВИДА (список | карточка) с переключением по
// выбору и BackButton, а не сплит с двумя скроллящимися зонами на одном экране.
// Авто-выбор первого инцидента — только на десктопе: на телефоне человек иначе
// приземляется в середину чужого досье.
import { useCallback, useEffect, useState } from 'react';
import {
  AlertTriangle, Flame, Gauge, MessageSquare, RefreshCw, ScrollText, Unplug,
} from 'lucide-react';
import type {
  IncidentChat, IncidentDossier, IncidentStatus, IncidentSummary,
} from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { Badge, BackButton, Button, EmptyState, SidebarSection, WaitingIndicator } from '../../components/ui';
import type { BadgeTone } from '../../components/ui/Badge';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';

// Ширина колонки списка на десктопе — как у соседних разделов с деревом слева
const LIST_W = 320;
// Сколько строк показывает бэкенд в списках ходов и логов (IncidentQueries.RowLimit).
// Держим здесь, чтобы честно писать «10 из N», а не молча обрезать.
const ROW_LIMIT = 10;

interface Props {
  // Статус раздела со страницы (GET /api/telemetry/status): по нему выбирается ПУСТОЕ
  // состояние. Без него выключенная телеметрия выглядела бы как «всё тихо» — самое
  // опасное враньё для этой фичи.
  status: { configured: boolean; reachable: boolean } | null;
  statusLoading: boolean;
  // Отпечаток из диплинка уведомления — открыть сразу карточку
  initialFingerprint?: string | null;
}

type DossierError = { kind: 'notFound' | 'network' };

export function IncidentsPanel({ status, statusLoading, initialFingerprint }: Props) {
  const isMobile = useIsMobile();

  const [items, setItems] = useState<IncidentSummary[]>([]);
  const [listStatus, setListStatus] = useState<IncidentStatus | null>(null);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState(false);
  const [listTick, setListTick] = useState(0);

  const [selected, setSelected] = useState<string | null>(initialFingerprint ?? null);
  const [dossier, setDossier] = useState<IncidentDossier | null>(null);
  const [dossierLoading, setDossierLoading] = useState(false);
  const [dossierError, setDossierError] = useState<DossierError | null>(null);
  const [cardTick, setCardTick] = useState(0);

  // Мобильный вид: список или карточка. Диплинк открывает сразу карточку.
  const [view, setView] = useState<'list' | 'item'>(initialFingerprint ? 'item' : 'list');

  // Диплинк может прийти в уже открытый раздел (событие cc-open-incident) —
  // реагируем на смену пропса, а не только на монтирование
  useEffect(() => {
    if (!initialFingerprint) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- открытие карточки по диплинку из уведомления
    setSelected(initialFingerprint);
    setView('item');
  }, [initialFingerprint]);

  useEffect(() => {
    let alive = true;
    setListLoading(true);
    setListError(false);
    api.telemetry.incidents()
      .then(res => {
        if (!alive) return;
        setItems(res.items);
        setListStatus(res.status);
      })
      .catch(() => { if (alive) setListError(true); })
      .finally(() => { if (alive) setListLoading(false); });
    return () => { alive = false; };
  }, [listTick]);

  // Авто-выбор первого — только десктоп (см. шапку файла)
  useEffect(() => {
    if (isMobile || selected || items.length === 0) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- десктопный сплит без выбора выглядит сломанным
    setSelected(items[0].fingerprint);
  }, [isMobile, items, selected]);

  useEffect(() => {
    if (!selected) { setDossier(null); return; }
    let alive = true;
    setDossierLoading(true);
    setDossierError(null);
    api.telemetry.incident(selected)
      .then(d => { if (alive) setDossier(d); })
      .catch((e: unknown) => {
        if (!alive) return;
        setDossier(null);
        // 404 (протухший диплинк из уведомления) и сетевой сбой — разные истории,
        // и лечатся по-разному: одно «Повторить» тут ничего не объясняет
        const message = e instanceof Error ? e.message : '';
        setDossierError({ kind: message.includes('404') ? 'notFound' : 'network' });
      })
      .finally(() => { if (alive) setDossierLoading(false); });
    return () => { alive = false; };
  }, [selected, cardTick]);

  const openIncident = useCallback((fingerprint: string) => {
    setSelected(fingerprint);
    setView('item');
  }, []);

  const firing = items.filter(i => i.isFiring);
  const recent = items.filter(i => !i.isFiring);

  const list = (
    <div style={{
      width: isMobile ? '100%' : LIST_W, flexShrink: 0, minHeight: 0, overflowY: 'auto',
      borderRight: isMobile ? 'none' : `1px solid ${C.borderLight}`,
      padding: `${SP.sm}px ${SP.md}px`,
    }}>
      {listError ? (
        <EmptyState
          compact
          icon={<AlertTriangle size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Список не загрузился"
          subtitle="Бэкенд не ответил на запрос инцидентов."
          action={<Button size={isMobile ? 'md' : 'sm'} variant="ghost"
            leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => setListTick(t => t + 1)}>Повторить</Button>}
        />
      ) : listLoading || (statusLoading && items.length === 0) ? (
        <div style={{ padding: SP.md }}>
          <WaitingIndicator hint="Читаю алерты SigNoz" />
        </div>
      ) : items.length === 0 ? (
        <EmptyPlaceholder status={status} listStatus={listStatus} isMobile={isMobile} />
      ) : (
        <>
          {firing.length > 0 && (
            <SidebarSection title="Горит" count={firing.length} storageKey="cc_incidents_firing">
              {firing.map(i => (
                <IncidentRow key={i.fingerprint} incident={i}
                  active={i.fingerprint === selected} onClick={() => openIncident(i.fingerprint)} />
              ))}
            </SidebarSection>
          )}
          {recent.length > 0 && (
            <SidebarSection title="Недавние" count={recent.length} storageKey="cc_incidents_recent">
              {recent.map(i => (
                <IncidentRow key={i.fingerprint} incident={i}
                  active={i.fingerprint === selected} onClick={() => openIncident(i.fingerprint)} />
              ))}
            </SidebarSection>
          )}
        </>
      )}
    </div>
  );

  const card = (
    <div style={{ flex: 1, minWidth: 0, minHeight: 0, overflowY: 'auto', padding: SP.lg }}>
      {isMobile && (
        <div style={{ marginBottom: SP.md }}>
          <BackButton onClick={() => setView('list')}>
            <span style={{ fontSize: FS.base, color: C.textSecondary }}>Все инциденты</span>
          </BackButton>
        </div>
      )}
      {dossierLoading ? (
        <WaitingIndicator hint="Собираю досье по инциденту" />
      ) : dossierError ? (
        <EmptyState
          compact
          icon={dossierError.kind === 'notFound'
            ? <ScrollText size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />
            : <AlertTriangle size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title={dossierError.kind === 'notFound' ? 'Инцидент не найден' : 'Досье не собралось'}
          subtitle={dossierError.kind === 'notFound'
            ? 'Он погас давно и вышел из истории — ссылка из уведомления устарела.'
            : 'Запрос к бэкенду не прошёл.'}
          action={<Button size={isMobile ? 'md' : 'sm'} variant="ghost"
            leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => setCardTick(t => t + 1)}>Повторить</Button>}
        />
      ) : dossier ? (
        <DossierCard dossier={dossier} isMobile={isMobile} />
      ) : (
        <EmptyState
          compact
          icon={<Flame size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Инцидент не выбран"
          subtitle="Выбери инцидент в списке — соберу досье."
        />
      )}
    </div>
  );

  if (isMobile) return view === 'list' ? list : card;

  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
      {list}
      {card}
    </div>
  );
}

/// Пустое состояние выбирается по СТАТУСУ, а не по длине списка
function EmptyPlaceholder({ status, listStatus, isMobile }: {
  status: { configured: boolean; reachable: boolean } | null;
  listStatus: IncidentStatus | null;
  isMobile: boolean;
}) {
  const notConfigured = listStatus === 'notConfigured' || status?.configured === false;
  const unavailable = listStatus === 'unavailable' || (status?.configured && !status.reachable);

  if (notConfigured) {
    return (
      <EmptyState
        compact
        // Выключенный раздел — не авария: аварийный значок пугал бы на ровном месте
        icon={<Gauge size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Телеметрия не настроена"
        subtitle="Раздел выключен администратором — инциденты никто не собирает."
      />
    );
  }
  if (unavailable) {
    return (
      <EmptyState
        compact
        icon={<Unplug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="SigNoz не отвечает"
        subtitle="Стек наблюдаемости не поднят — списка инцидентов сейчас нет."
        action={<Button size={isMobile ? 'md' : 'sm'} variant="ghost"
          onClick={() => window.location.reload()}>Обновить</Button>}
      />
    );
  }
  return (
    <EmptyState
      compact
      icon={<Flame size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
      title="Инцидентов нет"
      subtitle="Ни одно правило телеметрии не загоралось."
    />
  );
}

function IncidentRow({ incident, active, onClick }: {
  incident: IncidentSummary;
  active: boolean;
  onClick: () => void;
}) {
  const [hover, setHover] = useState(false);
  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={active}
      onClick={onClick}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick(); } }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'flex-start', gap: SP.sm, minWidth: 0,
        padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.lg, cursor: 'pointer',
        background: active ? C.bgSelected : hover ? C.bgInset : 'transparent',
        transition: 'background 0.15s',
      }}
    >
      <Flame
        size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
        style={{ marginTop: 3, flexShrink: 0, color: incident.isFiring ? C.danger : C.textMuted }}
      />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{
          fontSize: FS.base, fontWeight: active ? 600 : 500, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }} title={incident.title}>
          {incident.title}
        </div>
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
          {formatTime(incident.isFiring ? incident.startedAt : incident.resolvedAt)}
          {incident.environment ? ` · ${incident.environment}` : ''}
        </div>
      </div>
    </div>
  );
}

function DossierCard({ dossier, isMobile }: { dossier: IncidentDossier; isMobile: boolean }) {
  const { incident } = dossier;
  const chatsCount = dossier.chats.length;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, minWidth: 0 }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', minWidth: 0 }}>
          <span style={{
            fontFamily: FONT.serif, fontSize: isMobile ? FS.lg : FS.xl, fontWeight: 700,
            color: C.textHeading, minWidth: 0,
          }}>
            {incident.title}
          </span>
          <Badge tone={severityTone(incident.severity)} size="xs">
            {incident.isFiring ? 'горит' : 'погас'}
            {incident.severity ? ` · ${incident.severity}` : ''}
          </Badge>
        </div>
        {incident.description && (
          <div style={{ fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5 }}>
            {incident.description}
          </div>
        )}
        {/* Строка «насколько плохо»: до неё карточка показывала факт, но не масштаб */}
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>
          падений {dossier.turnsTotal} · чатов {chatsCount} · окно {formatTime(dossier.from)} — {formatTime(dossier.to)}
        </div>
      </div>

      {/* Баннер чужого контура стоит ДО любых действий: иначе человек нажмёт «Обсудить»
          раньше, чем узнает, что локальных данных тут нет */}
      {dossier.isForeignEnvironment && (
        <div style={{
          background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: R.lg,
          padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm, color: C.warningText, lineHeight: 1.5,
        }}>
          Инцидент другого контура ({incident.environment}). Локальных чатов и расхода по нему
          на этом инстансе нет — виден только разрез телеметрии.
        </div>
      )}

      {dossier.status === 'notConfigured' && (
        <EmptyState compact icon={<Gauge size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Телеметрия не настроена" subtitle="Данных для разбора нет." />
      )}
      {dossier.status === 'unavailable' && (
        <EmptyState compact icon={<Unplug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="SigNoz не ответил" subtitle="Данные за окно инцидента собрать не удалось." />
      )}

      {dossier.breakdown.length > 0 && (
        <Section title={`Разрез по ${dossier.breakdownTag}`}>
          {dossier.breakdown.map(row => (
            <div key={row.label} style={rowStyle}>
              <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {row.label}
              </span>
              <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>
                {Math.round(row.count)}
              </span>
            </div>
          ))}
        </Section>
      )}

      {dossier.chats.length > 0 && (
        <Section title="Затронутые чаты">
          {dossier.chats.map(chat => (
            <ChatRow key={chat.chatId} chat={chat} isMobile={isMobile} />
          ))}
        </Section>
      )}

      {dossier.turns.length > 0 && (
        <Section title="Упавшие ходы"
          note={dossier.turnsTotal > ROW_LIMIT ? `${dossier.turns.length} из ${dossier.turnsTotal}` : undefined}>
          {dossier.turns.map((turn, idx) => (
            <div key={`${turn.traceId}-${idx}`} style={rowStyle}>
              <span style={{ color: C.textMuted, fontSize: FS.xs, flexShrink: 0 }}>{formatTime(turn.at)}</span>
              <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {[turn.provider, turn.model, turn.errorType].filter(Boolean).join(' · ')}
              </span>
              {turn.durationMs > 0 && (
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
                  {Math.round(turn.durationMs / 100) / 10} с
                </span>
              )}
            </div>
          ))}
        </Section>
      )}

      {dossier.logs.length > 0 && (
        <Section title="Логи окна"
          note={dossier.logsTotal > ROW_LIMIT ? `${dossier.logs.length} из ${dossier.logsTotal}` : undefined}>
          {dossier.logs.map((line, idx) => (
            <div key={idx} style={{ ...rowStyle, alignItems: 'flex-start' }}>
              <span style={{ color: C.textMuted, fontSize: FS.xs, flexShrink: 0 }}>{formatTime(line.at)}</span>
              <span style={{
                flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: FS.xs,
                color: line.severity === 'Error' ? C.dangerText : C.textSecondary,
                wordBreak: 'break-word',
              }}>
                {line.message}
              </span>
            </div>
          ))}
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>
            Логи связаны с ходами по времени: у логов пустой trace_id.
          </div>
        </Section>
      )}

      {dossier.ruleUrl && (
        <a href={dossier.ruleUrl} target="_blank" rel="noopener noreferrer"
          style={{ fontSize: FS.sm, color: C.textSecondary, textDecoration: 'underline', width: 'fit-content' }}>
          Правило в SigNoz
        </a>
      )}
    </div>
  );
}

// Строка затронутого чата. Заголовку — flex: 1 + minWidth: 0, иначе на 320px первым
// до нуля сжимается именно название, ради которого строку и открывают.
function ChatRow({ chat, isMobile }: { chat: IncidentChat; isMobile: boolean }) {
  const meta = [
    `падений ${chat.failures}`,
    chat.totalTokens > 0 ? `${chat.totalTokens} токенов` : null,
    chat.mcpFailures.length > 0 ? `MCP: ${chat.mcpFailures.length}` : null,
  ].filter(Boolean).join(' · ');

  return (
    <div style={{
      ...rowStyle,
      flexDirection: isMobile ? 'column' : 'row',
      alignItems: isMobile ? 'flex-start' : 'center',
      gap: isMobile ? SP.xxs : SP.sm,
    }}>
      <span style={{
        flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        display: 'flex', alignItems: 'center', gap: SP.xs, maxWidth: '100%',
      }}>
        <MessageSquare size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, color: C.textMuted }} />
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {chat.title || 'Чат без названия'}
        </span>
      </span>
      <span
        // Полный список отвалившихся инструментов — в подсказку: в строке он не поместится
        title={chat.mcpFailures.length > 0 ? `Отказы MCP: ${chat.mcpFailures.join(', ')}` : undefined}
        style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0, whiteSpace: 'nowrap' }}
      >
        {meta}
      </span>
    </div>
  );
}

function Section({ title, note, children }: {
  title: string; note?: string; children: React.ReactNode;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <div style={{
        fontSize: FS.xs, fontWeight: 600, color: C.textSecondary,
        textTransform: 'uppercase', letterSpacing: '.03em',
      }}>
        {title}
        {note && <span style={{ fontWeight: 400, color: C.textMuted }}> · {note}</span>}
      </div>
      <div style={{
        background: C.bgInset, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
        padding: `${SP.xs}px ${SP.md}px`, minWidth: 0,
      }}>
        {children}
      </div>
    </div>
  );
}

const rowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0,
  padding: `${SP.xs}px 0`, fontSize: FS.base, color: C.textPrimary,
};

function severityTone(severity?: string | null): BadgeTone {
  switch ((severity ?? '').toLowerCase()) {
    case 'critical':
    case 'error': return 'danger';
    case 'warning': return 'warning';
    case 'info': return 'info';
    default: return 'neutral';
  }
}

function formatTime(value?: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}
