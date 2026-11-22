// Х-W4-78 (дефект fab7e418, факт 2): детская панель уроков — содержимое панели «Чаты»
// на детском экране проекта (И11, ADR-041 ред. 8).
//
// Два списка, один источник на каждый:
// • текущие уроки — штатная ручка GET /api/projects/{id}/sessions ([ChildAllowed] своему
//   проекту-предмета — SessionsController.cs:37), через listProjectSessions — той же
//   точки чтения, что штатный SessionList;
// • «Старые уроки» — детская ручка GET /api/child/spaces/lessons/chats (childApi.spaceChats),
//   только subjectClosed=true (ChildChatsController.cs:176): уроки выключенного или
//   удалённого предмета; их подписывает subjectLabel (тот же файл, :180) — даже когда
//   предмета уже нет в GET /api/child/subjects (ХВ2-Б1).
// Разделение по источнику: текущий урок не перечитывается из детской ручки, закрытый
// не выводится из списка проекта.
//
// Действия по И11: открыть урок и «Новый урок». Ни переименования, ни архива, ни удаления,
// ни ветки, ни видимости, ни переноса — ручки ребёнку закрыты (И1 п.1), и панель их не
// рисует.
// «Уроки видят: …» — решение 86: строка стоит в панели чатов и в шапке урока
// (ChildLessonHeader); источник тот же — viewers предмета.
//
// «Старые уроки» — группа ниже обычных, свёрнутая по умолчанию (Д2, решение Андрея по
// дефекту QA-1, живший в удалённом ChildWorkspace). Её строки — не кнопки: проект
// закрытого предмета ребёнку не виден (ProjectsController.cs:192 — выключенный предмет
// → 404), открывать его нечем.

import { useEffect, useState, type CSSProperties } from 'react';
import { BookOpen, ChevronDown, ChevronRight, Eye, Plus } from 'lucide-react';
import type { Session } from '../../types';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { Badge, Button, EmptyState, handlePressableKey } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { ListDateDivider } from '../../components/ListDateDivider';
import { groupChats } from '../../lib/chatGroups';
import { isArchivedChat } from '../../lib/chatFilters';
import { listProjectSessions } from '../../lib/sessionsList';
import { onMessage, onReconnected } from '../../lib/signalr';
import { showToast } from '../../lib/toast';
import { relTime } from '../home/WidgetCard';
import { childApi } from './api';
import { curatorsLine } from './ChildLessonHeader';
import type { ChildChatListItem } from './types';

// Единственное пространство режима (та же константа в api.ts): панель не спрашивает
// вызывающего, какой space подставить, — это свойство режима.
const LESSONS_SPACE = 'lessons';

// Единый focus-visible ring (клавиатура) для строк-кнопок списка — проектный паттерн
// M1/F3.3: один инжект <style> на модуль, не inline onFocus.
const FOCUS_CLASS = 'cc-child-lessons-focus';
if (typeof document !== 'undefined' && !document.getElementById('cc-child-lessons-focus-style')) {
  const el = document.createElement('style');
  el.id = 'cc-child-lessons-focus-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  // head ?? body: fake-DOM тестов (vi.hoisted) не всегда несёт head — не роняем импорт (ChatCard.tsx:51)
  (document.head ?? document.body).appendChild(el);
}

interface Props {
  // Проект-предмета, уроки которого показывает панель: текущий список — его сессии
  projectId: string;
  // Предмет открытого проекта; null — не предмет: «Нового урока» нет
  subjectId: string | null;
  // Имена кураторов для строки «Уроки видят: …» (решение 86); пустые — строки нет
  // вовсе (записка Майи: пустого «Эту папку видят:» не бывает)
  viewers?: readonly string[];
  // Активный урок — акцентная заливка строки; его нет в списке — добавляется сразу
  // (урок, созданный доком или центром, не должен ждать перечитывания)
  activeSession?: Session | null;
  // Открыть урок — ведёт владелец (панель сама маршрут не меняет)
  onSelectSession: (session: Session) => void;
  // Урок создан — его открывает вызывающий (тот же контракт, что onLessonCreated дока
  // и ChildCenterEmpty)
  onLessonCreated: (sessionId: string) => void;
  isMobile?: boolean;
}

// Архивных уроков в детском списке нет: архивация — взрослое действие (куратор видит
// тот же проект штатным списком), у ребёнка архива нет (И11).
const liveOnly = (list: Session[]): Session[] => list.filter(s => !isArchivedChat(s));

export function ChildLessonsPanel({ projectId, subjectId, viewers, activeSession, onSelectSession, onLessonCreated, isMobile = false }: Props) {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [closed, setClosed] = useState<ChildChatListItem[]>([]);
  // Текущий список приехал с сервера хотя бы раз: пустой стартовый массив — не «уроков
  // нет», empty-state ждёт ответа (тот же смысл, что loaded у штатного SessionList)
  const [loaded, setLoaded] = useState(false);
  // «Старые уроки» свёрнуты по умолчанию (Д2)
  const [closedOpen, setClosedOpen] = useState(false);
  const [creating, setCreating] = useState(false);

  // Оба списка — один раз на проект. Отказа с техническим текстом здесь нет: ребёнку
  // не о чем показывать серверную ошибку, списки дочитаются на реконнекте.
  useEffect(() => {
    let cancelled = false;
    const ac = new AbortController();
    listProjectSessions(projectId)
      .then(list => { if (cancelled) return; setSessions(liveOnly(list)); setLoaded(true); })
      .catch(() => { /* офлайн/отказ — дочитается на реконнекте */ });
    childApi.spaceChats(LESSONS_SPACE, { signal: ac.signal })
      .then(r => { if (!cancelled) setClosed(r.chats.filter(c => c.subjectClosed)); })
      .catch(() => { /* то же: старый список дочится на реконнекте */ });
    return () => { cancelled = true; ac.abort(); };
  }, [projectId]);

  // Realtime: удаление — сразу (урока, которого нет, в списке быть не должно);
  // реконнект — перечитать оба списка. Строк статуса панель не рисует, поэтому
  // status_changed её не поднимает (в отличие от штатного списка).
  useEffect(() => {
    const off = onMessage(msg => {
      if (msg.type !== 'chat_deleted') return;
      setSessions(prev => prev.some(s => s.id === msg.sessionId) ? prev.filter(s => s.id !== msg.sessionId) : prev);
      setClosed(prev => prev.some(c => c.sessionId === msg.sessionId) ? prev.filter(c => c.sessionId !== msg.sessionId) : prev);
    });
    const offRec = onReconnected(() => {
      listProjectSessions(projectId).then(list => setSessions(liveOnly(list))).catch(() => {});
      childApi.spaceChats(LESSONS_SPACE).then(r => setClosed(r.chats.filter(c => c.subjectClosed))).catch(() => {});
    });
    return () => { off(); offRec(); };
  }, [projectId]);

  // Созданный или открытый урок (доковый pending-канал, центр) не ждёт перечитывания:
  // добавляется сразу (тот же приём, что эффект активной сессии в SessionList).
  // Только сессии ЭТОГО проекта: детский чат другого проекта в этом списке не живет.
  useEffect(() => {
    if (!activeSession || activeSession.projectId !== projectId || isArchivedChat(activeSession)) return;
    setSessions(prev => (prev.some(s => s.id === activeSession.id) ? prev : [activeSession, ...prev]));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- сужение до id/проекта/имени осознанно: весь объект менялся бы на каждый тик статуса
  }, [activeSession?.id, activeSession?.projectId, activeSession?.name, projectId]);

  async function newLesson() {
    if (!subjectId || creating) return;   // двойной тап: второй игнорируется, пока первый в полёте
    setCreating(true);
    try {
      const r = await childApi.createLesson(subjectId);
      onLessonCreated(r.sessionId);
    } catch (e) {
      // Отказ по предмету или по состоянию (unknown_subject / subject_disabled / time_up…) —
      // спокойный серверный текст как есть (ADR-041, «Общие правила»); кнопка возвращается
      const msg = (e as { body?: { message?: string } }).body?.message ?? 'Сейчас урок не получится начать. Попробуй чуть позже.';
      showToast('Новый урок', msg);
    } finally {
      setCreating(false);
    }
  }

  const seen = curatorsLine(viewers ?? []);
  // Секции текущего списка — общие с штатным списком («Сегодня», «Вчера (пн)», дни);
  // закрепленных у ребёнка нет (панель их не рисует, а чат ребёнка закрепляет только
  // автор — которого у неё нет), поэтому группы ровно по дням.
  const groups = groupChats(sessions, 'newest');

  const rowStyle = (active: boolean): CSSProperties => ({
    display: 'flex', alignItems: 'center', gap: SP.sm, boxSizing: 'border-box',
    // Тач-цель строки (L5 аудита 21.11, макет kids-tutors.html .krow min-height:40px):
    // без minHeight строка была 35px — под детскую поверхность это мало.
    padding: `${SP.sm}px`, borderRadius: R.md, cursor: 'pointer', minHeight: 40,
    // Что урок выбран, видно по акцентной заливке — тот же признак, что у взрослой карточки
    background: active ? C.accentMuted : 'transparent',
  });
  const nameStyle: CSSProperties = {
    flex: 1, minWidth: 0, fontSize: FS.md, fontWeight: 600, color: C.textHeading,
    whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
  };
  const metaStyle: CSSProperties = {
    fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0,
  };

  const curatorsBlock = seen && (
    <span
      data-testid="child-curators-line"
      style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0, color: C.textSecondary, fontSize: FS.sm }}
    >
      <Eye size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} aria-hidden />
      <span title={seen} style={{ minWidth: 0, overflow: 'hidden', whiteSpace: 'nowrap', textOverflow: 'ellipsis' }}>
        {seen}
      </span>
    </span>
  );

  const newBtn = (
    <Button
      variant="primary" size="md" fullWidth loading={creating} disabled={!subjectId}
      dataTestId="child-new-lesson" leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      onClick={() => { void newLesson(); }}
    >
      Новый урок
    </Button>
  );

  const lessonRow = (s: Session) => {
    const active = activeSession?.id === s.id;
    return (
      <div
        key={s.id}
        data-testid="child-lesson-row"
        className={FOCUS_CLASS}
        role="button"
        tabIndex={0}
        aria-current={active ? 'true' : undefined}
        style={rowStyle(active)}
        onClick={() => onSelectSession(s)}
        onKeyDown={e => handlePressableKey(e, () => onSelectSession(s))}
      >
        <span style={nameStyle}>{s.name?.trim() || 'Новый урок'}</span>
        <span style={metaStyle}>{relTime(s.updatedAt)}</span>
      </div>
    );
  };

  const closedRow = (c: ChildChatListItem) => (
    <div
      key={c.sessionId}
      data-testid="child-lesson-closed-row"
      style={{ ...rowStyle(false), cursor: 'default' }}
    >
      <span style={{ ...nameStyle, fontWeight: 400, color: C.textSecondary }}>{c.name?.trim() || 'Новый урок'}</span>
      {c.subjectLabel && <Badge tone="neutral" size="xs">{c.subjectLabel}</Badge>}
      <span style={metaStyle}>{relTime(c.updatedAt)}</span>
    </div>
  );

  const closedBlock = closed.length > 0 && (
    <>
      <Button
        variant="ghost" size="xs"
        dataTestId="child-closed-toggle"
        ariaExpanded={closedOpen}
        onClick={() => setClosedOpen(v => !v)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%', justifyContent: 'flex-start',
          border: 'none', padding: `${SP.xs}px ${SP.xs}px`, color: C.textMuted,
          fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em',
          ...(isMobile ? { height: 44, minHeight: 44 } : null),
        }}
      >
        <span>Старые уроки · <span style={{ fontFamily: FONT.mono, letterSpacing: 0 }}>{closed.length}</span></span>
        {closedOpen
          ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      </Button>
      {closedOpen && closed.map(closedRow)}
    </>
  );

  return (
    <div
      data-testid="child-lessons-panel"
      style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, background: C.bgWhite }}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `${SP.sm}px ${SP.sm}px 0` }}>
        {curatorsBlock}
        {newBtn}
      </div>
      <div style={{ flex: 1, overflowY: 'auto', padding: SP.sm }}>
        {loaded && sessions.length === 0 && !closedBlock && (
          <EmptyState compact inline icon={<BookOpen size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />} title="Уроков пока нет" subtitle="Начни первый урок кнопкой выше." />
        )}
        {groups.map(g => (
          <div key={g.title}>
            <ListDateDivider title={g.title} plain />
            {g.items.map(lessonRow)}
          </div>
        ))}
        {closedBlock && <div style={{ marginTop: SP.xs }}>{closedBlock}</div>}
      </div>
    </div>
  );
}
