// Карточка доклада о завершении делегированной задачи (F1-F5 спеки
// docs/features/task-completion-report.md). Единственный носитель факта «задача
// выполнена»: статус, название задачи, подпись со счётчиком приложенных файлов,
// итог в Markdown и ряд из двух действий — «Открыть задачу» и «Чат исполнителя».
//
// «Открыть задачу» на десктопе воркспейса открывает её СПРАВА от ленты
// (ChatOpenTaskContext → split чат|задача), и разговор остаётся перед глазами;
// где места рядом нет (мобила, планшет, чат вне воркспейса, задача чужого проекта) —
// детали показываются модалкой поверх ленты.
//
// Задача берётся из стора задач (новых запросов нет). Пока стор грузится, карточка
// стоит на своём месте без действий — подменять её другим блоком нельзя, иначе лента
// прыгает на каждом открытии чата с докладом. Задачи нет вовсе (удалили после доклада) —
// карточка остаётся, но действий не предлагает.

import { useContext, useEffect, useState } from 'react';
import { Bot, CheckCircle2, MessageCircle, Paperclip } from 'lucide-react';
import type { Persona, Project, Task } from '../../types';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Button } from '../ui/Button';
import { api } from '../../lib/api';
import { showToast } from '../../lib/toast';
import { useIsMobile } from '../../lib/breakpoints';
import { ensureTasksLoaded, tasksLoaded, useTasks } from '../../lib/tasks';
import { ensurePersonasLoaded, personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { MessageOriginChip } from '../MessageOriginChip';
import { TaskDetailsModal } from '../../features/tasks/TaskDetailsModal';
import { MarkdownContent } from './MarkdownContent';
import { ChatOpenTaskContext, ChatProjectContext } from './contexts';
import type { DelegationReport } from '../../lib/delegationReport';
import { filesCountLabel, stripTruncatedTail } from '../../lib/delegationReport';

interface Props {
  report: DelegationReport;
  // Id задачи — структурное поле сообщения (StoredTextMessage/StoredUserMessage.DelegationTaskId)
  taskId: string;
  // Лицо исполнителя: персона либо (её нет) имя чата-исполнителя
  persona: Persona | null;
  neutralTitle?: string;
  // Чип «пришло из другого проекта» — как у обычного входящего сообщения
  origin?: string;
  onOpenFile?: (path: string) => void;
}

export function DelegationReportCard({ report, taskId, persona, neutralTitle, origin, onOpenFile }: Props) {
  // Лента живёт и вне раздела задач — стор мог быть не загружен вовсе
  useEffect(() => { void ensureTasksLoaded(); void ensurePersonasLoaded(); }, []);
  const tasks = useTasks();
  const isMobile = useIsMobile();
  const chatProject = useContext(ChatProjectContext);
  const openAside = useContext(ChatOpenTaskContext);
  const [detailsOpen, setDetailsOpen] = useState(false);
  // Проект задачи нужен модалке деталей (название и секция файлов). Грузим лениво,
  // по клику: на каждый доклад в ленте список проектов дёргать незачем
  const [paneProject, setPaneProject] = useState<Project | null>(null);

  const task = tasks.find(t => t.id === taskId);
  // Стор ещё не наполнен из сети — «задачи нет» тут означает «пока не знаем».
  // Пересчитывается на каждом эмите стора: useTasks выше на него подписан.
  const loading = !task && !tasksLoaded();
  const gone = !task && !loading;
  const taskProjectId = task?.projectId;
  useEffect(() => {
    if (!detailsOpen || !taskProjectId) return;
    let cancelled = false;
    api.projects.list()
      .then(list => { if (!cancelled) setPaneProject(list.find(p => p.id === taskProjectId) ?? null); })
      .catch(() => { /* без проекта модалка откроется как для личной задачи */ });
    return () => { cancelled = true; };
  }, [detailsOpen, taskProjectId]);

  // Рядом с лентой задача открывается только в воркспейсе ЕЁ проекта: центральная зона
  // чужого проекта такую задачу не покажет (она отфильтрована по projectId) — там модалка
  const openTask = (t: Task) => {
    if (openAside && !isMobile && t.projectId && t.projectId === chatProject?.id) openAside(t);
    else setDetailsOpen(true);
  };

  const openExecutorChat = async () => {
    if (!task?.linkedSessionId) return;
    try {
      const chat = await api.chats.get(task.linkedSessionId);
      if (chat) window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
      // Чат исполнителя удалён: без отклика человек жмёт кнопку ещё и ещё
      else showToast('Чат исполнителя', 'Чат исполнителя удалён', 'info');
    } catch { showToast('Чат исполнителя', 'Чат исполнителя удалён', 'info'); }
  };

  const files = task?.linkedFiles?.length ?? 0;
  const title = persona ? personaLabel(persona) : (neutralTitle ?? 'Исполнитель');
  // Хвост «Итог показан не полностью — открыть задачу» зовёт к действию, которого у
  // удалённой задачи уже нет. Но сам факт обрезки терять нельзя — иначе обрезок
  // читается как полный итог; он уезжает в плашку про удалённую задачу
  const body = gone ? stripTruncatedTail(report.body) : report.body;
  const truncated = body !== report.body;

  return (
    <div style={{
      // Рамка/фон — как у соседей по ленте (AgentMessageView, PersonaTaskView,
      // TaskCreatedView): карточка доклада заменяет их собой и не должна выпадать
      // из семейства; своё у неё только зелёное ребро статуса
      border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${C.success}`,
      borderRadius: R.xl, background: C.bgWhite, boxShadow: SHADOW.card,
      overflow: 'hidden', maxWidth: '100%',
    }}>
      {/* Шапка: лицо исполнителя слева, статус доклада справа. На узком экране
          пилюля статуса переносится под имя, а не отжимает его в многоточие */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', rowGap: SP.xs,
        padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.divider}`,
      }}>
        {persona ? (
          <PersonaAvatar persona={persona} size={28} />
        ) : (
          <span aria-hidden style={{
            width: 28, height: 28, borderRadius: R.full, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            background: C.bgSelected, color: C.textMuted,
          }}>
            <Bot size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </span>
        )}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
          <span style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '100%',
          }}>
            {title}
          </span>
          {persona?.handle && (
            <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>@{persona.handle}</span>
          )}
          {origin && <MessageOriginChip origin={origin} />}
        </div>
        {/* Статус — единственный зелёный акцент карточки: доклад приходит по факту
            завершения (провал в чат не докладывается, см. «Вне скоупа» спеки) */}
        <span style={{
          display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
          padding: `${SP.xxs}px ${SP.sm}px`, borderRadius: R.max,
          background: C.successBg, color: C.successText,
          fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
        }}>
          <CheckCircle2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          Задача выполнена
        </span>
      </div>

      {/* Тело: название задачи (свежее — из стора) и итог исполнителя */}
      <div style={{ padding: `${SP.md}px`, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.lg, fontWeight: 700, lineHeight: 1.35, color: C.textHeading, wordBreak: 'break-word' }}>
          {task?.title || report.title}
        </div>
        {/* F4: файлов нет — подписи нет. Не кнопка: вела бы ровно туда же, куда
            «Открыть задачу», — это факт о задаче, а не отдельное действие */}
        {task && files > 0 && (
          <div style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textMuted }}>
            <Paperclip size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            {filesCountLabel(files)}
          </div>
        )}
        {body.trim() && (
          <div style={{
            paddingTop: SP.sm, borderTop: `1px solid ${C.divider}`,
            fontSize: FS.base, color: C.textPrimary, wordBreak: 'break-word',
          }}>
            <MarkdownContent text={body} />
          </div>
        )}
      </div>

      {/* Действия: путь к результату прямо из ленты. Ряд есть всегда — и пока задача
          грузится, и когда её уже нет: карточка не меняет высоту под ногами */}
      <div style={{
        display: 'flex', flexDirection: isMobile ? 'column' : 'row',
        alignItems: isMobile ? 'stretch' : 'center', gap: SP.sm, flexWrap: 'wrap',
        // Резерв высоты ряда = высота кнопки соответствующего размера (sm 32 / md 40):
        // пока стор задач грузится, кнопок нет, и без резерва лента прыгнет
        minHeight: isMobile ? 40 : 32,
        padding: `${SP.sm}px ${SP.md}px`, borderTop: `1px solid ${C.divider}`,
      }}>
        {gone ? (
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>
            {truncated
              ? 'Задача удалена — остался только этот доклад, и итог показан не полностью'
              : 'Задача удалена — остался только этот доклад'}
          </span>
        ) : (
          <>
            {/* Не accent: главное действие чата — композер, а докладов в ленте много
                (accent-дисциплина гайда). Первичность внутри карточки держит белая
                заливка ghostFilled против прозрачного ghost у второго действия —
                форма читается в обеих темах */}
            <Button size={isMobile ? 'md' : 'sm'} variant="ghostFilled" fullWidth={isMobile}
              disabled={loading} title={loading ? 'Загружаем задачу…' : undefined}
              onClick={() => { if (task) openTask(task); }}>
              Открыть задачу
            </Button>
            {task?.linkedSessionId && (
              <Button size={isMobile ? 'md' : 'sm'} variant="ghost" fullWidth={isMobile}
                onClick={() => { void openExecutorChat(); }}
                leftIcon={<MessageCircle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
                Чат исполнителя
              </Button>
            )}
          </>
        )}
      </div>

      {/* Детали задачи поверх чата — запасной путь: рядом с лентой места нет
          (мобила, планшет, чат вне воркспейса). Лента остаётся под модалкой */}
      {detailsOpen && task && (
        <TaskDetailsModal
          task={task}
          project={paneProject}
          isMobile={isMobile}
          onOpenFile={onOpenFile}
          onClose={() => setDetailsOpen(false)}
        />
      )}
    </div>
  );
}
