import { useEffect, useState } from 'react';
import type { AuthState, Role, RoleMemoryContext, Session, AgentInfo } from '../types';
import { api } from '../lib/api';
import { C, FONT, R, SHADOW, MODAL_W } from '../lib/design';
import { Button, IconButton, Modal, ModalActions } from '../components/ui';
import type { HubTab } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { RoleAvatar } from '../components/RoleAvatar';
import { RoleEditorDialog } from '../components/RoleEditorDialog';
import { ChatPanel } from '../components/ChatPanel';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}

function useWindowWidth() {
  const [width, setWidth] = useState(window.innerWidth);
  useEffect(() => {
    const handler = () => setWidth(window.innerWidth);
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  }, []);
  return width;
}

// Вкладка «Сотрудники» верхнего хаба: глобальный пул ролей-собеседников списком-таблицей
// (имя, характер, компетенции, проекты, память). Разворот строки — факты из памяти
// по контекстам (проекты + внепроектные чаты текущего пользователя).
// Тык по строке = открыть чат с сотрудником прямо здесь (мессенджер: один непрерывный
// внепроектный диалог на сотрудника). Найм здесь — без привязки к проекту.
export function TeamPage({ auth, onLogout, onHubTab }: Props) {
  const [roles, setRoles] = useState<Role[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [creating, setCreating] = useState(false);
  const [editTarget, setEditTarget] = useState<Role | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Role | null>(null);
  const [starting, setStarting] = useState<string | null>(null);
  // Обзор памяти per role (для колонки «Память» и разворота строки)
  const [memory, setMemory] = useState<Record<string, RoleMemoryContext[]>>({});
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  // Каталог глобальных агентов: fileName → инфо (человеческое имя и описание компетенций)
  const [agentMap, setAgentMap] = useState<Map<string, AgentInfo>>(new Map());
  // Открытый чат с сотрудником (поверх таблицы, в этом же разделе)
  const [activeChat, setActiveChat] = useState<Session | null>(null);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const isMobile = useWindowWidth() < 768;

  // Вложения относятся к конкретному чату — сбрасываем при смене активного
  useEffect(() => { setAttachedFiles([]); }, [activeChat?.id]);

  useEffect(() => {
    api.team.list().then(list => {
      setRoles(list);
      // Обзор памяти — параллельно по всем (пул небольшой)
      list.forEach(r => {
        api.team.memoryOverview(r.id)
          .then(ov => setMemory(prev => ({ ...prev, [r.id]: ov })))
          .catch(() => {});
      });
    }).catch(() => {}).finally(() => setLoaded(true));
    api.team.agents()
      .then(list => setAgentMap(new Map(list.map(a => [a.fileName, a]))))
      .catch(() => {});
  }, []);

  const factCount = (roleId: string) =>
    (memory[roleId] ?? []).reduce((n, c) => n + c.facts.length, 0);

  // Человеческое имя компетенции (агента); фолбэк — сам fileName (например, проектный агент)
  const agentTitle = (fileName: string) => agentMap.get(fileName)?.name || fileName;
  const agentDesc = (fileName: string) => agentMap.get(fileName)?.description || '';

  const toggleExpand = (roleId: string) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(roleId)) next.delete(roleId); else next.add(roleId);
      return next;
    });
  };

  // Тык по сотруднику = открыть чат с ним (мессенджер-модель): один непрерывный
  // внепроектный диалог. Если чата с этим сотрудником ещё нет — создаём, иначе открываем.
  const openChat = async (role: Role) => {
    if (starting) return;
    setStarting(role.id);
    try {
      const chats = await api.chats.list();
      const existing = chats.find(c => c.roleId === role.id);   // список отсортирован по свежести
      setActiveChat(existing ?? await api.chats.create('auto', undefined, role.name, undefined, undefined, role.id));
    } catch {
      /* офлайн/сбой — остаёмся на таблице */
    } finally {
      setStarting(null);
    }
  };

  const handleSaved = (saved: Role) => {
    setRoles(prev => prev.some(r => r.id === saved.id)
      ? prev.map(r => (r.id === saved.id ? saved : r))
      : [...prev, saved]);
    // Память могли поправить в редакторе — обновляем обзор
    api.team.memoryOverview(saved.id)
      .then(ov => setMemory(prev => ({ ...prev, [saved.id]: ov })))
      .catch(() => {});
    setCreating(false);
    setEditTarget(null);
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    try {
      await api.team.delete(deleteTarget.id);
    } catch {
      setDeleteTarget(null);
      return;
    }
    setRoles(prev => prev.filter(r => r.id !== deleteTarget.id));
    setDeleteTarget(null);
  };

  const hireButton = (
    <Button
      variant="primary" size="md" glow
      onClick={() => setCreating(true)}
      leftIcon={
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
          <path d="M12 5v14M5 12h14" />
        </svg>
      }
    >
      Нанять сотрудника
    </Button>
  );

  // Ячейка-заголовок таблицы
  const th = (label: string, width?: number | string, hideOnMobile = false) =>
    (hideOnMobile && isMobile) ? null : (
      <div key={label} style={{
        width, flex: width ? undefined : 1, minWidth: 0, flexShrink: 0,
        fontSize: 10.5, fontWeight: 700, letterSpacing: '0.05em', textTransform: 'uppercase', color: C.textMuted,
      }}>
        {label}
      </div>
    );

  // Открытый чат с сотрудником — поверх раздела. На мобиле — полноэкранно (кнопка «назад»
  // в шапке чата); на десктопе — под общей шапкой хаба (можно переключить раздел),
  // с кнопкой «назад к таблице» слева в шапке чата.
  if (activeChat) {
    const chatPanel = (
      <ChatPanel
        key={activeChat.id}
        session={activeChat}
        isMobile={isMobile}
        onBack={() => setActiveChat(null)}
        attachedFiles={attachedFiles}
        onAttachedFilesChange={setAttachedFiles}
        onSessionUpdated={updated => setActiveChat(updated)}
      />
    );
    return isMobile ? (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {chatPanel}
      </div>
    ) : (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <HubHeader value="team" onTab={onHubTab} auth={auth} onLogout={onLogout} />
        <div style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          {chatPanel}
        </div>
      </div>
    );
  }

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="team" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{ maxWidth: 1080, margin: '0 auto', padding: isMobile ? '18px 16px 28px' : '28px 24px 40px' }}>

          {roles.length === 0 ? (
            /* Пустое состояние: приглашение нанять первого сотрудника */
            loaded && (
              <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', gap: 10, paddingTop: isMobile ? 48 : 88 }}>
                <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: 4 }}>
                  <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
                    <circle cx="9" cy="7" r="4" />
                    <path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" />
                  </svg>
                </div>
                <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 22, color: C.textHeading, letterSpacing: '-0.01em' }}>
                  Соберите свою команду
                </div>
                <div style={{ fontSize: 13.5, color: C.textSecondary, lineHeight: 1.55, maxWidth: 400 }}>
                  Наймите виртуальных сотрудников — с именем, характером и компетенциями.
                  Общайтесь с ними здесь или приглашайте в команды проектов.
                </div>
                <div style={{ marginTop: 10 }}>{hireButton}</div>
              </div>
            )
          ) : (
            <>
              {/* Шапка раздела: заголовок + найм */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16 }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 24, color: C.textHeading, letterSpacing: '-0.01em' }}>
                    Сотрудники
                  </div>
                  <div style={{ fontSize: 12.5, color: C.textMuted, marginTop: 2 }}>
                    Нажмите на сотрудника, чтобы открыть чат с ним
                  </div>
                </div>
                {hireButton}
              </div>

              {/* Заголовки колонок (десктоп) */}
              {!isMobile && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '0 14px 7px' }}>
                  {th('Сотрудник', 218)}
                  {th('Характер')}
                  {th('Компетенции', 170)}
                  {th('Проекты', 62)}
                  {th('Память', 72)}
                  <div style={{ width: 90, flexShrink: 0 }} />
                </div>
              )}

              {/* Строки сотрудников */}
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {roles.map(role => {
                  const isOpen = expanded.has(role.id);
                  const facts = factCount(role.id);
                  return (
                    <div key={role.id} style={{
                      background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                      borderRadius: R.xl, boxShadow: SHADOW.card, overflow: 'hidden',
                    }}>
                      {/* Основная строка */}
                      <div
                        onClick={() => openChat(role)}
                        title="Открыть чат с сотрудником"
                        style={{
                          display: 'flex', alignItems: 'center', gap: 12, padding: isMobile ? '12px 14px' : '11px 14px',
                          cursor: 'pointer', opacity: starting === role.id ? 0.6 : 1,
                        }}
                        onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; }}
                        onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
                      >
                        {/* Сотрудник: аватар + имя + должность */}
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10, width: isMobile ? undefined : 218, flex: isMobile ? 1 : undefined, minWidth: 0, flexShrink: 0 }}>
                          <RoleAvatar name={role.name} avatar={role.avatar} color={role.color} size={38} />
                          <div style={{ minWidth: 0 }}>
                            <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                              {role.name || 'Без имени'}
                            </div>
                            <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                              {role.title || 'Сотрудник'}
                            </div>
                          </div>
                        </div>

                        {/* Характер (только десктоп) */}
                        {!isMobile && (
                          <div style={{ flex: 1, minWidth: 0, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                            title={role.persona || undefined}>
                            {role.persona || <span style={{ color: C.textMuted }}>—</span>}
                          </div>
                        )}

                        {/* Компетенции (только десктоп): до 2 чипов с человеческими именами + счётчик */}
                        {!isMobile && (
                          <div style={{ width: 170, flexShrink: 0, display: 'flex', gap: 4, overflow: 'hidden', alignItems: 'center' }}
                            title={role.agentNames.map(agentTitle).join(', ') || undefined}>
                            {role.agentNames.length === 0 ? (
                              <span style={{ fontSize: 12, color: C.textMuted }}>—</span>
                            ) : (
                              <>
                                {role.agentNames.slice(0, 2).map(a => (
                                  <span key={a} title={agentDesc(a) || agentTitle(a)} style={{
                                    fontSize: 10.5, fontWeight: 600, color: C.textSecondary, background: C.bgPanel,
                                    borderRadius: 5, padding: '2px 7px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: 76,
                                  }}>
                                    {agentTitle(a)}
                                  </span>
                                ))}
                                {role.agentNames.length > 2 && (
                                  <span style={{ fontSize: 10.5, fontWeight: 700, color: C.textMuted, flexShrink: 0 }}>
                                    +{role.agentNames.length - 2}
                                  </span>
                                )}
                              </>
                            )}
                          </div>
                        )}

                        {/* Проекты (только десктоп) */}
                        {!isMobile && (
                          <div style={{ width: 62, flexShrink: 0, fontSize: 12.5, color: role.projectIds.length ? C.textSecondary : C.textMuted }}>
                            {role.projectIds.length || '—'}
                          </div>
                        )}

                        {/* Память: счётчик фактов */}
                        {!isMobile && (
                          <div style={{ width: 72, flexShrink: 0, fontSize: 12.5, color: facts ? C.textSecondary : C.textMuted }}>
                            {facts ? `${facts} факт.` : '—'}
                          </div>
                        )}

                        {/* Действия */}
                        <div style={{ display: 'flex', flexShrink: 0, gap: 2, width: isMobile ? undefined : 90, justifyContent: 'flex-end' }}>
                          <IconButton size="sm" title="Редактировать"
                            onClick={e => { e.stopPropagation(); setEditTarget(role); }}>
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                              <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                              <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                            </svg>
                          </IconButton>
                          <IconButton size="sm" title="Удалить из команды насовсем" tone="danger"
                            onClick={e => { e.stopPropagation(); setDeleteTarget(role); }}>
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                              <polyline points="3 6 5 6 21 6" />
                              <path d="M19 6l-1 14H6L5 6" />
                              <path d="M10 11v6M14 11v6" />
                              <path d="M9 6V4h6v2" />
                            </svg>
                          </IconButton>
                          <IconButton size="sm" title={isOpen ? 'Свернуть' : 'Подробнее (память, характер)'}
                            active={isOpen}
                            onClick={e => { e.stopPropagation(); toggleExpand(role.id); }}>
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"
                              style={{ transform: isOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>
                              <polyline points="6 9 12 15 18 9" />
                            </svg>
                          </IconButton>
                        </div>
                      </div>

                      {/* Разворот: характер (мобилка) + компетенции с описаниями + память по контекстам */}
                      {isOpen && (
                        <div style={{ borderTop: `1px solid ${C.divider}`, padding: '10px 14px 12px', display: 'flex', flexDirection: 'column', gap: 10 }}>
                          {isMobile && role.persona && (
                            <div>
                              <div style={{ fontSize: 10.5, fontWeight: 700, letterSpacing: '0.05em', textTransform: 'uppercase', color: C.textMuted, marginBottom: 3 }}>Характер</div>
                              <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.45 }}>{role.persona}</div>
                            </div>
                          )}
                          {/* Компетенции — что сотрудник умеет (описания агентов), на всех раскладках */}
                          {role.agentNames.length > 0 && (
                            <div>
                              <div style={{ fontSize: 10.5, fontWeight: 700, letterSpacing: '0.05em', textTransform: 'uppercase', color: C.textMuted, marginBottom: 3 }}>Компетенции</div>
                              <ul style={{ margin: 0, paddingLeft: 18, display: 'flex', flexDirection: 'column', gap: 2 }}>
                                {role.agentNames.map(a => (
                                  <li key={a} style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.45 }}>
                                    <strong style={{ fontWeight: 600, color: C.textHeading }}>{agentTitle(a)}</strong>
                                    {agentDesc(a) && <> — {agentDesc(a)}</>}
                                  </li>
                                ))}
                              </ul>
                            </div>
                          )}
                          {(memory[role.id] ?? []).length === 0 ? (
                            <div style={{ fontSize: 12.5, color: C.textMuted }}>
                              Память пока пуста — появится по мере общения с сотрудником.
                            </div>
                          ) : (
                            (memory[role.id] ?? []).map(ctx => (
                              <div key={ctx.context}>
                                <div style={{ fontSize: 10.5, fontWeight: 700, letterSpacing: '0.05em', textTransform: 'uppercase', color: C.textMuted, marginBottom: 3 }}>
                                  Память · {ctx.title}
                                </div>
                                <ul style={{ margin: 0, paddingLeft: 18, display: 'flex', flexDirection: 'column', gap: 2 }}>
                                  {ctx.facts.map((f, i) => (
                                    <li key={i} style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.45 }}>{f}</li>
                                  ))}
                                </ul>
                              </div>
                            ))
                          )}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </>
          )}
        </div>
      </div>

      {(creating || editTarget) && (
        <RoleEditorDialog
          role={editTarget ?? undefined}
          onSaved={handleSaved}
          onClose={() => { setCreating(false); setEditTarget(null); }}
        />
      )}

      {deleteTarget && (
        <Modal
          title="Удалить сотрудника насовсем?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Сотрудник «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name}</strong>» будет удалён
              из пула и из команд всех проектов вместе со всей его памятью. Существующие чаты с ним останутся.
              Это действие необратимо.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить насовсем"
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
