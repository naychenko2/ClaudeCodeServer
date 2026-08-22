// Виджет «Стена» на дашборде: состав набора (до MAX_CHATS чатов из разных проектов),
// вход в режим и автосбор из активных чатов.
//
// Зачем он есть: сама стена живёт доком воркспейса, то есть попасть на неё можно было
// только через открытый проект. С дашборда набор не был виден вовсе — а это ровно та
// сводка, ради которой на дашборд и заходят: что я поставил рядом и кто там шевелится.
//
// Статусы берём из wallStore (initWall поднимает состав И подписывает на status_changed
// по группам проектов набора), а НЕ из useChatActivity: тот завёл бы второй поллинг
// /api/home/summary рядом с уже работающим useHomeSummary дашборда.
import { useEffect, useState } from 'react';
import { Columns3, MessageCircle } from 'lucide-react';
import type { Project, Session } from '../../types';
import { C, FONT, FS, R } from '../../lib/design';
import { hasUnread } from '../../lib/chatReadState';
import { STATUS_COLOR, STATUS_PULSE } from '../../lib/projectActivity';
import { getPersonaById, personaLabel } from '../../lib/personas';
import { showToast } from '../../lib/toast';
import { plural } from '../../lib/spend';
import { Button } from '../../components/ui';
import { ProjectIcon } from '../projects/ProjectIcon';
import { useWallState, initWall, chatStatus } from '../wall/wallStore';
import { loadChatsForWall, addChatsToWall } from '../wall/wallSuggest';
import { WidgetCard, WidgetAction, WidgetEmpty, relTime } from './WidgetCard';
import { wallWidgetView, wallRowStatus } from './wallWidgetView';

const ICON_SLOT = 18;

// Заголовок строки: имя чата > персона > последнее сообщение > заглушка —
// та же лесенка, что в SessionRow дашборда
function rowTitle(s: Session): string {
  if (s.name?.trim()) return s.name;
  if (s.personaId) {
    const p = getPersonaById(s.personaId);
    if (p) return personaLabel(p);
  }
  if (s.lastMessage?.trim()) return s.lastMessage;
  return 'Новый чат';
}

function WallRow({ chat, project, onOpen }: {
  chat: Session;
  // Проект чата; undefined — либо чат внепроектный, либо проект недоступен
  // (различаем по chat.projectId, а не по наличию Project)
  project: Project | undefined;
  onOpen: () => void;
}) {
  const st = wallRowStatus(chatStatus(chat), hasUnread(chat.updatedAt, chat.id, chat.lastReadAt));
  // Подпись проекта: у чата есть projectId, но проекта нет — он удалён или закрыт,
  // и назвать такой чат «вне проекта» было бы неправдой (так же честна WallColumn)
  const projectLabel = project ? project.name : (chat.projectId ? 'Проект недоступен' : null);
  return (
    <button
      onClick={onOpen}
      // Геометрия ряда (gap, padding, отрицательный margin) взята из SessionRow:
      // строки соседних виджетов колонки обязаны совпадать по выравниванию
      style={{
        display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
        background: 'none', border: 'none', borderRadius: R.md, padding: '7px 8px',
        margin: '0 -8px', cursor: 'pointer', minWidth: 0,
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
    >
      {/* Слот точки статуса — всегда: у тихого чата точки нет, но строки колонки
          не должны разъезжаться. Слева, как у всех строк дашборда: статусы
          сканируют по левой кромке.
          position: relative обязателен — заливку рисует .cc-dot::after с inset: 0,
          и без него она растянулась бы до ближайшего позиционированного предка
          (в рельсах сам span абсолютный, поэтому там правило не нужно) */}
      <span style={{ width: 8, height: 8, flexShrink: 0, display: 'flex' }}>
        {st && (
          <span
            className={STATUS_PULSE[st].trim()}
            style={{
              width: 8, height: 8, borderRadius: R.full, position: 'relative',
              '--cc-dot-c': STATUS_COLOR[st],
              pointerEvents: 'none',
            } as React.CSSProperties}
          />
        )}
      </span>
      <span style={{
        width: ICON_SLOT, height: ICON_SLOT, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted,
      }}>
        {project
          ? <ProjectIcon project={project} size={ICON_SLOT} radius={R.sm} />
          : <MessageCircle size={13} strokeWidth={2} />}
      </span>
      <span style={{
        flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        {rowTitle(chat)}
      </span>
      {projectLabel && (
        <span style={{
          flexShrink: 0, fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted,
          maxWidth: 110, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {projectLabel}
        </span>
      )}
      <span style={{ flexShrink: 0, fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>
        {relTime(chat.updatedAt)}
      </span>
    </button>
  );
}

export function WallWidget({ ownerId, onOpenWall }: {
  // AuthState.id объявлен опциональным — принимаем таким же
  ownerId?: string;
  onOpenWall: (focusId?: string) => void;
}) {
  const { loaded, chats, projects } = useWallState();
  const [candidates, setCandidates] = useState(0);
  const [adding, setAdding] = useState(false);

  useEffect(() => { initWall(ownerId); }, [ownerId]);

  // Кандидатов считаем ТОЛЬКО по загруженному составу: loadChatsForWall берёт занятые
  // места из стора, и на пустом сторе кнопка обещала бы «Добавить (3)», а автосбор
  // вернул бы 0 — все кандидаты уже стоят колонками
  useEffect(() => {
    if (!loaded) return;
    let alive = true;
    void loadChatsForWall().then(list => { if (alive) setCandidates(list.length); });
    return () => { alive = false; };
  }, [loaded, chats.length]);

  const view = wallWidgetView(chats.length, candidates);

  const suggest = async () => {
    if (adding) return;
    setAdding(true);
    try {
      const n = await addChatsToWall();
      showToast(
        'Стена',
        n > 0
          ? `Добавлено ${n} ${plural(n, 'чат', 'чата', 'чатов')}`
          : 'Добавить нечего: недавних чатов нет',
      );
    } finally {
      setAdding(false);
    }
  };

  return (
    <WidgetCard
      icon={<Columns3 size={16} strokeWidth={2} />}
      title="Стена"
      action={
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 10 }}>
          {!view.empty && (
            <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}>
              {view.counterText}
            </span>
          )}
          <WidgetAction label="Открыть →" onClick={() => onOpenWall()} />
        </span>
      }
    >
      {/* Пустое состояние показываем только по загруженному составу: иначе на секунду
          мелькает длинный абзац, который потом схлопывается в список, и карточка прыгает */}
      {loaded && view.empty
        ? <WidgetEmpty text="На стене чаты из разных проектов стоят колонками рядом — удобно вести несколько дел разом." />
        : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {chats.map(c => (
              <WallRow
                key={c.id}
                chat={c}
                project={c.projectId ? projects.get(c.projectId) : undefined}
                onOpen={() => onOpenWall(c.id)}
              />
            ))}
          </div>
        )}

      {/* «Недавние», а не «активные»: chatsForWall берёт и живые чаты, и просто
          тронутые за сутки — вторых в подсказке обычно большинство, а слово
          «активные» в продукте уже значит «сейчас идёт ход» */}
      {view.showSuggest && (
        <Button variant="ghost" size="sm" fullWidth loading={adding} onClick={() => void suggest()}>
          Добавить недавние ({view.suggestCount})
        </Button>
      )}
    </WidgetCard>
  );
}
