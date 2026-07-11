import { useEffect, useState, useRef } from 'react';
import type { Project, Session, AgentInfo, Persona } from '../types';
import { api } from '../lib/api';
import { onMessage, onReconnected } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { isOnline } from '../lib/offline';
import { StatusBadge } from './StatusBadge';
import { EditSessionDialog } from './EditSessionDialog';
import { C, R, SHADOW, MODAL_W, FONT } from '../lib/design';
import { Modal, ModalActions, Button, IconButton } from './ui';
import { getPersonaById, usePersonas, usePersonasVersion, personaLabel } from '../lib/personas';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import { CompanionSelector, type CompanionSelection } from './CompanionSelector';
import { agentDotColor } from './AgentSelector';
import { ExpiryBadge } from './ExpiryBadge';

// Время создания сессии: сегодня — часы:минуты, иначе — дата
function sessTime(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  if (d.toDateString() === now.toDateString())
    return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
}

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string, autoSelect?: boolean) => void;
  onSessionUpdated?: (session: Session) => void;
  onSessionsChanged?: (count: number) => void;
  isMobile?: boolean;
  workflowRunningFor?: string;
  // .md-агенты проекта — для единого селектора собеседника
  agents?: AgentInfo[];
  // Запомненный .md-агент проекта (localStorage в WorkspacePage) — начальный выбор
  selectedAgent?: AgentInfo | null;
  // Персист выбора .md-агента (WorkspacePage кладёт в localStorage)
  onAgentChange?: (agent: AgentInfo | null) => void;
}

export function SessionList({ project, activeSession, onSelect, onSessionUpdated, onSessionsChanged, isMobile = false, workflowRunningFor, agents = [], selectedAgent, onAgentChange }: Props) {
  const online = useOnline();
  // Подписка на стор персон — перерисоваться, когда список подгрузится (аватары сессий персон)
  usePersonasVersion();
  const personas = usePersonas();
  // Доступные в контексте проекта персоны: проектные (этого проекта) + глобальные
  const ctxPersonas = personas.filter(p => p.scope === 'global' || (p.scope === 'project' && p.projectId === project.id));
  // Выбранный собеседник нового чата (локально): персона ИЛИ .md-агент.
  // Агент инициализируется из запомненного выбора проекта (localStorage в WorkspacePage).
  const [companion, setCompanion] = useState<{ persona: Persona | null; agent: AgentInfo | null }>(
    () => ({ persona: null, agent: selectedAgent ?? null })
  );
  const handleCompanionSelect = (sel: CompanionSelection) => {
    const next = { persona: sel.persona ?? null, agent: sel.agent ?? null };
    setCompanion(next);
    onAgentChange?.(next.agent); // персист .md-агента (персона в localStorage не запоминается)
  };
  const [sessions, setSessions] = useState<Session[]>([]);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  const [editTarget, setEditTarget] = useState<Session | null>(null);
  const initializedRef = useRef(false);
  // Свежие activeSession/onSelect для обработчика chat_deleted (realtime-подписка живёт дольше рендера)
  const activeRef = useRef(activeSession);
  activeRef.current = activeSession;
  const onSelectRef = useRef(onSelect);
  onSelectRef.current = onSelect;

  useEffect(() => { onSessionsChanged?.(sessions.length); }, [sessions.length, onSessionsChanged]);

  const createNew = async (): Promise<Session> => {
    // Выбрана персона → чат от её лица в этом проекте (projectId кладёт сюда
    // и чат глобальной персоны); выбран .md-агент → обычная сессия с агентом;
    // никто → обычная сессия
    const s = companion.persona
      ? await api.personas.createChat(companion.persona.id, { mode: 'auto', projectId: project.id })
      : await api.sessions.create(project.id, 'auto', undefined, undefined, undefined, companion.agent?.fileName);
    // Чужую (глобальную) сессию в список этого проекта не добавляем — поллинг сам синхронит
    if (s.projectId === project.id) setSessions(prev => [s, ...prev]);
    onSelect(s);
    return s;
  };


  // Загрузка и поллинг сессий
  useEffect(() => {
    initializedRef.current = false;

    const init = async () => {
      // Офлайн без кэша — список недоступен, выходим без выбора
      const list = await api.sessions.list(project.id).catch(() => null);
      if (!list) return;
      setSessions(list);
      if (!initializedRef.current) {
        initializedRef.current = true;
        if (!activeSession) {
          if (list.length > 0) {
            onSelect(list[0], undefined, true);
          } else if (isOnline()) {
            // Офлайн чат не создаём — мутации недоступны
            const s = await api.sessions.create(project.id);
            setSessions([s]);
            onSelect(s, undefined, true);
          }
        }
      }
    };

    init();
    const interval = setInterval(() => {
      api.sessions.list(project.id).then(setSessions).catch(() => {});
    }, 5000);
    return () => clearInterval(interval);
  }, [project.id]);

  // Подписка на статусы в реальном времени. Членство в project-группе держит WorkspacePage.
  useEffect(() => {
    let mounted = true;

    // Переподключение — рефетчим статусы (могли пропустить status_changed)
    onReconnected(() => {
      if (!mounted) return;
      api.sessions.list(project.id).then(list => {
        if (mounted) setSessions(list);
      }).catch(() => {});
    });

    const unsub = onMessage(msg => {
      if (!mounted) return;
      // Сессия удалена на сервере (в т.ч. авто-удаление временной) — убираем из списка;
      // если была открыта — переключаемся на первую оставшуюся
      if (msg.type === 'chat_deleted') {
        setSessions(prev => {
          const updated = prev.filter(s => s.id !== msg.sessionId);
          if (activeRef.current?.id === msg.sessionId && updated.length > 0)
            queueMicrotask(() => onSelectRef.current(updated[0], undefined, true));
          return updated;
        });
        return;
      }
      if (msg.type !== 'status_changed') return;
      setSessions(prev => prev.map(s =>
        s.id === msg.sessionId
          ? {
              ...s,
              status: msg.status as Session['status'],
              ...(msg.lastMessage !== undefined && { lastMessage: msg.lastMessage }),
              ...(msg.messageCount !== undefined && msg.messageCount > 0 && { messageCount: msg.messageCount }),
            }
          : s
      ));
    });

    return () => {
      mounted = false;
      unsub();
    };
  }, [project.id]);

  // Если активную сессию отредактировали из шапки чата — подхватываем название/модель,
  // не затирая статус, который приходит по realtime
  useEffect(() => {
    if (!activeSession) return;
    setSessions(prev => prev.map(s =>
      s.id === activeSession.id ? { ...s, name: activeSession.name, model: activeSession.model } : s
    ));
  }, [activeSession?.id, activeSession?.name, activeSession?.model]);

  const handleSessionUpdated = (updated: Session) => {
    setSessions(prev => prev.map(s => s.id === updated.id ? { ...s, ...updated } : s));
    if (activeSession?.id === updated.id) onSessionUpdated?.(updated);
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    // Кнопка удаления скрыта офлайн, но сеть могла упасть между показом и кликом —
    // защищаемся от unhandled rejection и не закрываем диалог при сбое
    try {
      await api.sessions.delete(project.id, deleteTarget.id);
    } catch {
      setDeleteTarget(null);
      return;
    }
    const updated = sessions.filter(s => s.id !== deleteTarget.id);
    setSessions(updated);
    setDeleteTarget(null);
    if (activeSession?.id === deleteTarget.id) {
      if (updated.length > 0) {
        onSelect(updated[0], undefined, true);
      } else {
        try {
          const s = await api.sessions.create(project.id);
          setSessions([s]);
          onSelect(s);
        } catch { /* офлайн/сбой — список пуст, создастся при возврате онлайн */ }
      }
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {online && (
        <div style={{ padding: '10px 12px', borderBottom: `1px solid ${C.divider}`, display: 'flex', alignItems: 'center', gap: 8 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Button variant="dashed" size="md" fullWidth onClick={createNew}
              leftIcon={
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
                  <path d="M12 5v14M5 12h14" />
                </svg>
              }
            >
              {companion.persona
                ? `Чат с «${personaLabel(companion.persona)}»`
                : companion.agent
                ? `Чат с «${companion.agent.name}»`
                : 'Новый чат'}
            </Button>
          </div>
          {/* Выбор собеседника нового чата: персоны (проектные + глобальные) и .md-агенты */}
          <CompanionSelector
            personas={ctxPersonas}
            agents={agents}
            selectedPersona={companion.persona}
            selectedAgentName={companion.agent?.fileName ?? null}
            onSelect={handleCompanionSelect}
            isMobile={isMobile}
            dropUp={false}
          />
        </div>
      )}

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 8px' }}>
        {sessions.map((s, index) => {
          const isActive = activeSession?.id === s.id;
          // Сессия от лица персоны: слева мини-аватар, имя персоны и акцент её цвета
          const persona = s.personaId ? getPersonaById(s.personaId) : undefined;
          // Групповой чат: стек мини-аватаров участников + подпись «Групповой»
          const group = (s.participants?.length ?? 0) > 1
            ? s.participants!.map(id => getPersonaById(id)).filter(p => p !== undefined)
            : [];
          const accent = persona ? agentDotColor(persona.avatar?.color) : C.accent;
          return (
          <div
            key={s.id}
            onClick={() => onSelect(s)}
            style={{
              position: 'relative',
              // отдельные longhand-свойства: со shorthand + undefined React обнуляет padding-left
              paddingTop: isMobile ? 14 : 11,
              paddingBottom: isMobile ? 14 : 11,
              paddingRight: isMobile ? 16 : 12,
              // у активной карточки добавляем слева место под акцентную полосу
              paddingLeft: (isMobile ? 16 : 12) + (isActive ? 6 : 0),
              borderRadius: isMobile ? 16 : R.xl,
              marginBottom: 5,
              cursor: 'pointer',
              overflow: 'hidden',
              background: isActive ? C.accentLight : C.bgWhite,
              border: '1px solid ' + (isActive ? accent : C.borderLight),
              boxShadow: isActive
                ? SHADOW.button
                : SHADOW.card,
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'flex-start',
              gap: (persona || group.length > 1) ? 9 : 0,
            }}
          >
            {/* Акцентная полоса слева — явный маркер текущего чата (у сессий персоны — её цветом) */}
            {isActive && (
              <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: accent }} />
            )}
            {group.length > 1 ? (
              // Вертикальный плотный стек: важно количество участников, а не лица —
              // карточку не распирает по ширине даже при 4 персонах
              <div style={{ flexShrink: 0, marginTop: 1, display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
                {group.map((p, i) => (
                  <div key={p!.id} style={{
                    marginTop: i === 0 ? 0 : -15, position: 'relative', zIndex: group.length - i,
                    borderRadius: '50%', border: `1.5px solid ${C.bgWhite}`,
                  }}>
                    <PersonaAvatar persona={p!} size={22} />
                  </div>
                ))}
              </div>
            ) : persona && (
              <div style={{ flexShrink: 0, marginTop: 1 }}><PersonaAvatar persona={persona} size={28} /></div>
            )}
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
                {(s.status === 'active') && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.success, flexShrink: 0 }} />
                )}
                {s.status === 'finished' && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.textMuted, flexShrink: 0 }} />
                )}
                <span style={{ fontSize: 13.5, fontWeight: isActive ? 700 : 600, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.name ?? `Чат #${index + 1}`}
                </span>
                {(s.status === 'starting' || s.status === 'working' || s.status === 'waiting' || s.status === 'error' || s.status === 'orphaned') && (
                  <StatusBadge status={s.status} />
                )}
                {workflowRunningFor === s.id && (
                  <div title="Выполняется Workflow" style={{
                    display: 'flex', alignItems: 'center', gap: 3,
                    padding: '1px 5px',
                    background: C.accentLight, border: `1px solid ${C.accentMuted}`, borderRadius: 4,
                    flexShrink: 0,
                  }}>
                    <div className="tool-spinner" style={{ width: 8, height: 8 }} />
                    <span style={{ fontFamily: 'Hanken Grotesk, sans-serif', fontSize: 10, fontWeight: 600, color: C.accent, lineHeight: 1 }}>WF</span>
                  </div>
                )}
              </div>
              {group.length > 1 ? (
                <div style={{ fontSize: 11.5, fontWeight: 600, color: accent, marginBottom: 3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  Групповой · {group.length} участника
                </div>
              ) : persona && (
                <div style={{ fontSize: 11.5, fontWeight: 600, color: accent, marginBottom: 3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {personaLabel(persona)}
                </div>
              )}
              {s.lastMessage && (
                <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.lastMessage}
                </div>
              )}
            </div>
            {/* Правая колонка: время создания (сверху, вправо) + действия */}
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 5, flexShrink: 0, paddingLeft: 6 }}>
              <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, lineHeight: 1, whiteSpace: 'nowrap' }}>
                {sessTime(s.createdAt)}
              </span>
              <ExpiryBadge session={s} />
              {online && (<div style={{ display: 'flex' }}>
              <IconButton onClick={e => { e.stopPropagation(); setEditTarget(s); }} title="Настройки чата" size="xs">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                  <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                </svg>
              </IconButton>
              <IconButton onClick={e => { e.stopPropagation(); setDeleteTarget(s); }} title="Удалить чат" size="xs" tone="danger">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <polyline points="3 6 5 6 21 6" />
                  <path d="M19 6l-1 14H6L5 6" />
                  <path d="M10 11v6M14 11v6" />
                  <path d="M9 6V4h6v2" />
                </svg>
              </IconButton>
              </div>)}
            </div>
          </div>
          );
        })}
      </div>

      {editTarget && (
        <EditSessionDialog
          session={editTarget}
          onSaved={handleSessionUpdated}
          onClose={() => setEditTarget(null)}
        />
      )}

      {deleteTarget && (
        <Modal
          title="Удалить чат?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Чат «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name ?? 'Новый чат'}</strong>» будет удалён без возможности восстановления.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleDelete}
              onCancel={() => setDeleteTarget(null)}
            />
          }
        />
      )}
    </div>
  );
}
