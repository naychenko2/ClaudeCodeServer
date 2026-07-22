import { useEffect, useState, useRef } from 'react';
import { Plus } from 'lucide-react';
import type { Project, Session } from '../types';
import { api } from '../lib/api';
import { onMessage, onReconnected } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { isOnline } from '../lib/offline';
import { EditSessionDialog } from './EditSessionDialog';
import { C, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Button } from './ui';
import { usePersonas, usePersonasVersion } from '../lib/personas';
import { FilterBar } from './FilterBar';
import { useChatFilters, useSanitizePersonaFilter } from '../lib/chatFilters';
import { useLastMechanicVersion } from '../lib/lastMechanic';
import { ChatCard } from './ChatCard';
import { ListDateDivider } from './ListDateDivider';
import { groupChats } from '../lib/chatGroups';

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string, autoSelect?: boolean) => void;
  onSessionUpdated?: (session: Session) => void;
  onSessionsChanged?: (count: number) => void;
  isMobile?: boolean;
  workflowRunningFor?: string;
}

export function SessionList({ project, activeSession, onSelect, onSessionUpdated, onSessionsChanged, isMobile = false, workflowRunningFor }: Props) {
  const online = useOnline();
  // Подписка на стор персон — перерисоваться, когда список подгрузится (аватары сессий персон)
  usePersonasVersion();
  // Подписка на стор механик — перерисовать список при запуске новой механики
  useLastMechanicVersion();
  const personas = usePersonas();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  const [editTarget, setEditTarget] = useState<Session | null>(null);
  // Карточка под курсором — на ней показываем действия (на тач-устройствах hover нет, там действия видны всегда)
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const initializedRef = useRef(false);
  // Свежие activeSession/onSelect для обработчика chat_deleted (realtime-подписка живёт дольше рендера)
  const activeRef = useRef(activeSession);
  activeRef.current = activeSession;
  const onSelectRef = useRef(onSelect);
  onSelectRef.current = onSelect;

  useEffect(() => { onSessionsChanged?.(sessions.length); }, [sessions.length, onSessionsChanged]);

  const createNew = async (): Promise<Session> => {
    const s = await api.sessions.create(project.id, 'auto');
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

  // === Фильтры списка чатов ===
  // Персистятся в localStorage отдельно для каждого проекта (scope = project.id)
  const { filters, patch } = useChatFilters(project.id);
  const visibleOrigins = new Set(filters.origins);

  // Персоны в списке (для селектора фильтра)
  const personaIdsInList = [...new Set(sessions.filter(s => s.personaId).map(s => s.personaId!))];
  useSanitizePersonaFilter(filters, patch, personaIdsInList, sessions.length > 0);

  // Применение фильтров
  const filteredSessions = sessions.filter(s => {
    if (!visibleOrigins.has(s.origin)) return false;
    if (filters.activeOnly && Date.now() - new Date(s.updatedAt).getTime() > 5 * 60 * 1000) return false;
    if (filters.personaId && s.personaId !== filters.personaId) return false;
    return true;
  });
  const hiddenCount = sessions.length - filteredSessions.length;

  // Номер в подписи безымянного чата берём из исходного порядка списка:
  // группировка тасует карточки по дням, и позиция в группе давала бы скачущие номера
  const numberById = new Map(sessions.map((s, i) => [s.id, i + 1]));
  const groups = groupChats(filteredSessions);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {online && (
        <div style={{ padding: '10px 12px', borderBottom: `1px solid ${C.divider}`, display: 'flex', alignItems: 'center', gap: 8 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Button variant="dashed" size="md" fullWidth onClick={createNew}
              leftIcon={
                <Plus size={15} strokeWidth={2.2} />
              }
            >
              Новый чат
            </Button>
          </div>
        </div>
      )}

      {/* Строка фильтров — всегда видна (для консистентности) */}
      <FilterBar
        visibleOrigins={visibleOrigins}
        onChangeVisibleOrigins={v => patch({ origins: [...v] })}
        activeOnly={filters.activeOnly}
        onChangeActiveOnly={v => patch({ activeOnly: v })}
        filterPersonaId={filters.personaId}
        onChangeFilterPersona={id => patch({ personaId: id })}
        personaIdsInList={personaIdsInList}
        allPersonas={personas}
        hiddenCount={hiddenCount}
        isMobile={isMobile}
      />

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 8px' }}>
        {filteredSessions.length === 0 && sessions.length > 0 && (
          <div style={{ padding: '24px 8px', textAlign: 'center', color: C.textMuted, fontSize: 13 }}>
            Все чаты скрыты фильтрами
          </div>
        )}
        {groups.map(g => (
          <div key={g.title} style={{ marginBottom: 6 }}>
            <ListDateDivider title={g.title} />
            {g.items.map(s => (
              <ChatCard
                key={s.id}
                session={s}
                isActive={activeSession?.id === s.id}
                isMobile={isMobile}
                fallbackName={`Чат #${numberById.get(s.id) ?? 1}`}
                online={online}
                hovered={hoveredId === s.id}
                workflowRunning={workflowRunningFor === s.id}
                onSelect={() => onSelect(s)}
                onHover={h => setHoveredId(h ? s.id : null)}
                onEdit={() => setEditTarget(s)}
                onDelete={() => setDeleteTarget(s)}
              />
            ))}
          </div>
        ))}
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
