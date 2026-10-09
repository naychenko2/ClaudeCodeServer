// Мобильный переключатель чатов: шторка «Недавние» по тапу на имя чата в шапке.
// Закреплённые сверху, затем недавние чаты ВСЕХ проектов по времени — переход в чат
// другого проекта за два тапа, без похода через список проектов.
//
// Системный «назад» закрывает шторку, а не уводит со страницы: на открытии кладём
// в историю запись-дубль текущего снимка с флагом, «назад» её снимает. Выбор чата
// сперва снимает эту запись и только потом открывает чат — иначе в истории между
// прошлым и новым чатом остался бы дубль, и «назад» из нового чата тратил бы лишнее
// нажатие.
import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { ChevronDown, MessageCircle } from 'lucide-react';
import type { HomeSessionInfo, Project } from '../../types';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { STATUS_COLOR, STATUS_PULSE, foldChatActivity, useChatActivity, type ActivityStatus } from '../../lib/projectActivity';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Modal } from '../../components/ui';
import { ProjectIcon } from '../projects/ProjectIcon';
import { useAllProjects } from '../projects/useAllProjects';
import { useHomeSummary } from './useHomeSummary';
import { openSession, rowTitle } from './SessionRow';
import { switcherSections } from './chatSwitcher';

const ICON_SLOT = 20;
// Флаг записи истории, которую шторка кладёт на открытии
const HISTORY_FLAG = 'chatSwitcher';

const hasFlag = () => !!(window.history.state as Record<string, unknown> | null)?.[HISTORY_FLAG];

// Подписи статуса — про ЧАТ (как в доке стены)
const CHAT_STATUS_TITLE: Record<ActivityStatus, string> = {
  waiting: 'ждет ответа',
  working: 'работает',
  unread: 'непрочитанное',
};

function SwitcherRow({ s, project, status, current, onPick }: {
  s: HomeSessionInfo;
  project: Project | undefined;
  status: ActivityStatus | undefined;
  current: boolean;
  onPick: (s: HomeSessionInfo) => void;
}) {
  // У чата есть projectId, а проекта в списке нет — удалён или недоступен:
  // назвать его «вне проекта» было бы неправдой
  const projectLabel = s.projectId ? (s.projectName ?? project?.name ?? 'Проект недоступен') : 'Чат вне проекта';
  return (
    <button
      type="button"
      onClick={() => onPick(s)}
      aria-current={current ? 'true' : undefined}
      aria-label={`${rowTitle(s)} — ${projectLabel}${status ? ` — ${CHAT_STATUS_TITLE[status]}` : ''}${current ? ' — открыт сейчас' : ''}`}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.md, width: '100%', minHeight: 48,
        textAlign: 'left', border: 'none', borderRadius: R.lg, padding: `${SP.xs}px ${SP.sm}px`,
        // Текущий чат — подложкой строки, без акцента: акцент в списке зарезервирован
        // за статусом, и выбранная строка не должна спорить с точкой «работает»
        background: current ? C.bgSelected : 'none', cursor: 'pointer', minWidth: 0,
        fontFamily: FONT.sans,
      }}
    >
      <span style={{
        width: ICON_SLOT, height: ICON_SLOT, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted,
      }}>
        {project
          ? <ProjectIcon project={project} size={ICON_SLOT} radius={R.sm} />
          : <MessageCircle size={14} strokeWidth={2} />}
      </span>
      <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
        <span style={{
          fontSize: FS.md, color: C.textPrimary, fontWeight: current ? 600 : 400,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {rowTitle(s)}
        </span>
        <span style={{
          fontSize: FS.xs, color: C.textMuted,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {projectLabel}
        </span>
      </span>
      {/* Слот точки — всегда, чтобы правый край строк не разъезжался. Вид точки —
          общий с рельсами (STATUS_COLOR/STATUS_PULSE). position: relative обязателен:
          заливку рисует .cc-dot::after с inset: 0 */}
      <span style={{ width: 8, height: 8, flexShrink: 0, display: 'flex' }}>
        {status && (
          <span
            className={STATUS_PULSE[status].trim()}
            style={{
              width: 8, height: 8, borderRadius: R.full, position: 'relative',
              '--cc-dot-c': STATUS_COLOR[status],
              pointerEvents: 'none',
            } as CSSProperties}
          />
        )}
      </span>
    </button>
  );
}

function SectionTitle({ children }: { children: string }) {
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, color: C.textMuted,
      textTransform: 'uppercase', letterSpacing: '0.04em', padding: `0 ${SP.sm}px`,
    }}>
      {children}
    </div>
  );
}

export function ChatSwitcherSheet({ currentId, onClose }: {
  currentId: string;
  onClose: () => void;
}) {
  const { data, failed } = useHomeSummary();
  const activity = useChatActivity();
  const projects = useAllProjects();
  const projectById = useMemo(() => new Map(projects.map(p => [p.id, p])), [projects]);
  const sections = useMemo(() => data ? switcherSections(data) : null, [data]);

  // Чат, выбранный в шторке: открывается, когда «назад» снимет запись шторки
  const pendingRef = useRef<HomeSessionInfo | null>(null);
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; }, [onClose]);

  useEffect(() => {
    // Повторный монтаж (StrictMode) второй записи не кладёт
    if (!hasFlag()) {
      window.history.pushState({ ...(window.history.state ?? {}), [HISTORY_FLAG]: true }, '', window.location.href);
    }
    const onPop = () => {
      if (hasFlag()) return; // ушли «вперёд» на запись шторки — не наш случай
      const target = pendingRef.current;
      pendingRef.current = null;
      onCloseRef.current();
      // Переход — после того, как все слушатели popstate применят прежний снимок
      // (он тот же чат): иначе они перебили бы только что открытый чат старым
      if (target) setTimeout(() => openSession(target), 0);
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const close = () => {
    if (hasFlag()) window.history.back();
    else onClose();
  };
  const pick = (s: HomeSessionInfo) => {
    if (s.id === currentId) { close(); return; }
    if (hasFlag()) { pendingRef.current = s; window.history.back(); }
    else { onClose(); openSession(s); }
  };

  const renderRows = (list: HomeSessionInfo[]) => list.map(s => (
    <SwitcherRow
      key={s.id} s={s}
      project={s.projectId ? projectById.get(s.projectId) : undefined}
      status={activity.get(s.id)}
      current={s.id === currentId}
      onPick={pick}
    />
  ));

  const hint = (text: string) => (
    <div style={{ fontFamily: FONT.sans, fontSize: FS.base, color: C.textMuted, padding: `${SP.sm}px ${SP.sm}px` }}>
      {text}
    </div>
  );

  return (
    <Modal title="Чаты" onClose={close}>
      {!sections
        ? hint(failed ? 'Не удалось загрузить чаты' : 'Загрузка…')
        : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg }}>
            {sections.pinned.length > 0 && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
                <SectionTitle>Закреплённые</SectionTitle>
                {renderRows(sections.pinned)}
              </div>
            )}
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
              <SectionTitle>Недавние</SectionTitle>
              {sections.recent.length > 0 ? renderRows(sections.recent) : hint('Недавних чатов нет')}
            </div>
          </div>
        )}
    </Modal>
  );
}

// Подпись точки на шевроне — про ДРУГИЕ чаты (по образцу STATUS_TITLE рельсы проектов)
const OTHER_CHATS_STATUS_TITLE: Record<ActivityStatus, string> = {
  waiting: 'в другом чате ждут ответа',
  working: 'в другом чате идёт работа',
  unread: 'есть непрочитанные чаты',
};

// Тач-зона имени чата в мобильной шапке: имя + шеврон ▾, тап открывает шторку.
// Точка на шевроне — свёртка активности по всем чатам, КРОМЕ открытого, с тем же
// приоритетом и видом, что у точек рельс. Отдельный компонент — чтобы подписка на
// активность чатов жила только там, где зона нарисована (мобила), а не в каждой шапке
export function ChatSwitcherTrigger({ currentId, children }: { currentId: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const other = foldChatActivity(useChatActivity(), currentId);
  const title = `Переключить чат${other ? ` — ${OTHER_CHATS_STATUS_TITLE[other]}` : ''}`;
  const show = () => setOpen(true);
  return (
    <>
      <div
        role="button" tabIndex={0}
        aria-haspopup="dialog" aria-label={title} title={title}
        onClick={show}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); show(); } }}
        style={{
          flex: 1, minWidth: 0, minHeight: 40, display: 'flex', alignItems: 'center', gap: SP.xs,
          cursor: 'pointer', borderRadius: R.md,
        }}
      >
        {/* Имя ужимается по содержимому: шеврон стоит сразу за ним, а не у правого
            края, при этом тач-зона (flex: 1) по-прежнему тянется на всю ширину */}
        <span style={{ display: 'flex', flex: '0 1 auto', minWidth: 0 }}>{children}</span>
        <span style={{ position: 'relative', display: 'flex', flexShrink: 0, color: C.textMuted }}>
          <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          {other && (
            <span className={STATUS_PULSE[other]} style={{
              position: 'absolute', right: -3, top: -3, width: 8, height: 8, borderRadius: R.full,
              // Подложка и ободок цветом холста — как у точки рельс: заливка живёт в ::after
              background: C.bgMain, border: `2px solid ${C.bgMain}`,
              '--cc-dot-c': STATUS_COLOR[other],
              boxSizing: 'content-box', pointerEvents: 'none',
            } as CSSProperties} />
          )}
        </span>
      </div>
      {open && <ChatSwitcherSheet currentId={currentId} onClose={() => setOpen(false)} />}
    </>
  );
}
