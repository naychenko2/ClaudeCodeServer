// Мобильный переключатель чатов: шторка «Недавние» по тапу на имя чата в шапке.
// Закреплённые сверху, затем недавние чаты ВСЕХ проектов по времени — переход в чат
// другого проекта за два тапа, без похода через список проектов.
//
// Системный «назад» закрывает шторку, а не уводит со страницы — запись шторки в
// истории и её снятие живут в chatSwitcherHistory.ts.
import { useEffect, useMemo, useState, type CSSProperties, type ReactNode } from 'react';
import { ChevronDown, MessageCircle } from 'lucide-react';
import type { HomeSessionInfo, Project } from '../../types';
import { C, FONT, FS, R, SP, TB } from '../../lib/design';
import { STATUS_COLOR, STATUS_PULSE, foldChatActivity, useChatActivity, type ActivityStatus } from '../../lib/projectActivity';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Modal } from '../../components/ui';
import { ProjectIcon } from '../projects/ProjectIcon';
import { useAllProjects } from '../projects/useAllProjects';
import { useHomeSummary } from './useHomeSummary';
import { openSession, rowTitle } from './SessionRow';
import { switcherSections } from './chatSwitcher';
import { createSwitcherController } from './chatSwitcherHistory';

const ICON_SLOT = 20;
// Высота строки шторки — тач-цель с двумя строками текста
const ROW_MIN_H = 48;
// Минимум ширины тач-зоны имени в шапке: правый кластер не ужимает её до нуля
export const CHAT_SWITCHER_MIN_W = 96;

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
      // Подложка — классом: текущий чат (aria-current), нажатие и hover. Текущий чат
      // выделен подложкой, без акцента: акцент в списке зарезервирован за статусом
      className="cc-chat-switch"
      style={{
        display: 'flex', alignItems: 'center', gap: SP.md, width: '100%', minHeight: ROW_MIN_H,
        textAlign: 'left', border: 'none', borderRadius: R.lg, padding: `${SP.xs}px ${SP.sm}px`,
        cursor: 'pointer', minWidth: 0, fontFamily: FONT.sans,
      }}
    >
      {/* Слот точки — всегда, чтобы строки не разъезжались. Слева, как у строк стены
          (WallRow): статусы сканируют по левой кромке. Вид точки — общий с рельсами
          (STATUS_COLOR/STATUS_PULSE). position: relative обязателен: заливку рисует
          .cc-dot::after с inset: 0 */}
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
      <span style={{
        width: ICON_SLOT, height: ICON_SLOT, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted,
      }}>
        {project
          ? <ProjectIcon project={project} size={ICON_SLOT} radius={R.sm} />
          : <MessageCircle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
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
    </button>
  );
}

function SectionTitle({ children }: { children: string }) {
  return (
    <div style={{
      // Тот же вид, что у заголовка SidebarSection: заголовки списков чатов — одно правило
      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary,
      textTransform: 'uppercase', letterSpacing: '.03em', padding: `0 ${SP.sm}px`,
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

  // Один контроллер на всю жизнь шторки: перемонтаж StrictMode переживает его состояние.
  const [ctl] = useState(() => createSwitcherController<HomeSessionInfo>(window, { onClose, open: openSession }));
  useEffect(() => { ctl.setOnClose(onClose); }, [ctl, onClose]);
  useEffect(() => ctl.mount(), [ctl]);

  const close = ctl.close;
  const pick = (s: HomeSessionInfo) => ctl.pick(s, s.id === currentId);

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
        className="cc-chat-switch"
        onClick={show}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); show(); } }}
        style={{
          flex: 1, minWidth: CHAT_SWITCHER_MIN_W, minHeight: TB.iconHitMobile, display: 'flex', alignItems: 'center', gap: SP.xs,
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
              // Подложка и ободок цветом фона шапки (TB.bg) — точка «вырезана» из шеврона,
              // как у точки рельс; заливка живёт в ::after
              background: TB.bg, border: `2px solid ${TB.bg}`,
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
