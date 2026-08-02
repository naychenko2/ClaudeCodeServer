// Колонка стены: остров с тонкой полосой-ярлыком (проект + статус + переход в
// проект) и ChatPanel в режиме embedded. Шапка ЧАТА — штатная (ChatHeaderBar
// внутри ChatPanel): колонка выглядит как настоящий чат, а не панель; ярлык
// сверху — не дубль шапки, а ответ на «чей это столбец» (на стене чаты РАЗНЫХ
// проектов, в самой шапке чата проекта нет). Фокус — акцентная рамка острова,
// ставится кликом по любому месту колонки.
//
// Ярлык — ещё и ручка перетаскивания: тянешь колонку — меняется порядок набора
// (общий протокол с монетами рельсы). Иконка проекта под курсором ярлыка
// превращается в кнопку «убрать со стены» — ровно как иконка панели в PanelShell.
//
// Осознанные срезы (не баги): в колонку не проброшены skills/agents (пикеры
// навыков и агентов в композере пусты); onOpenFile дают только колонки С
// проектом (FileViewer требует project). Полная работа с проектом — переход.
import { useEffect, useRef, useState } from 'react';
import { SquareArrowOutUpRight, X } from 'lucide-react';
import type { Project, Session } from '../../types';
import { C, FONT, FS, R, SHADOW, Z } from '../../lib/design';
import { Island, IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { ChatPanel } from '../../components/ChatPanel';
import { ProjectIcon } from '../projects/ProjectIcon';
import { chatStatus, focusChat, updateChat, removeChat, startOrderDrag, isOrderDrag, dropOrder } from './wallStore';

// Слот иконки в ярлыке — как у шапки панели (PanelShell): место в потоке ровно
// под значок, кнопка под курсором крупнее слота и выступает симметрично
const ICON_SLOT = 15;

export function WallColumn({ session, project, index, focused, onZoom, onOpenFile }: {
  session: Session;
  // undefined — чат вне проекта (это норма); null — проект чата не нашёлся (ошибка)
  project: Project | undefined | null;
  // Позиция в наборе — для перестановки перетаскиванием
  index: number;
  focused: boolean;
  onZoom: () => void;
  // Клик по файлу в ленте → оверлей стены; передаётся только колонкам с проектом
  onOpenFile?: (path: string) => void;
}) {
  const status = chatStatus(session);
  const busy = status === 'working' || status === 'waiting';
  // Вложения композера — per-колонка: загрузка кладёт файл в рабочую папку сессии
  // и возвращает путь сюда; заглушка-[] превращала бы скрепку в молчаливую потерю
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const [labelHover, setLabelHover] = useState(false);
  const [zoomTip, setZoomTip] = useState(false);
  const [dropTarget, setDropTarget] = useState(false);
  // Курсор над колонкой возвращает неактивной полную плотность: читать её надо ДО
  // того, как кликнешь — иначе, чтобы разглядеть соседний чат, пришлось бы менять фокус
  const [hover, setHover] = useState(false);
  const dimmed = !focused && !hover;

  // Колонка стала активной — просим композер взять фокус (счётчик, а не флаг:
  // возврат к той же колонке должен срабатывать снова). Растим только на переходе
  // «не активна → активна», иначе любой ре-рендер воровал бы курсор из ленты.
  const [focusSignal, setFocusSignal] = useState(0);
  const wasFocused = useRef(focused);
  useEffect(() => {
    if (focused && !wasFocused.current) setFocusSignal(n => n + 1);
    wasFocused.current = focused;
  }, [focused]);

  return (
    <Island
      // Сильное стекло вместо плотной заливки: ряд колонок иначе закрывает холст
      // целиком, и дудл-фон продукта перестаёт читаться
      bg={C.glassStrong}
      borderColor={dropTarget || focused ? C.accent : undefined}
      style={{
        flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column',
        // Неактивные колонки чуть приглушены — глаз сразу находит рабочую. Именно
        // прозрачность, а не размытие: текст остаётся читаемым, подсказка мягкая.
        opacity: dimmed ? 0.72 : 1,
        transition: 'opacity 0.18s ease-out',
      }}
      rootProps={{
        // Фокус — капчур-фазой: клики внутри ленты/композера не должны глотаться по пути
        onMouseDownCapture: () => focusChat(session.id),
        onMouseEnter: () => setHover(true),
        onMouseLeave: () => setHover(false),
        // Колонка — цель перестановки: сюда бросают монету рельсы или другую колонку
        onDragOver: (e: React.DragEvent) => { if (isOrderDrag(e)) { e.preventDefault(); setDropTarget(true); } },
        onDragLeave: () => setDropTarget(false),
        onDrop: (e: React.DragEvent) => { dropOrder(e, index); setDropTarget(false); },
      }}
    >
      {/* Полоса-ярлык (~26px): проект, статус, переход. Она же ручка перетаскивания */}
      <div
        draggable
        onDragStart={e => startOrderDrag(e, index)}
        onMouseEnter={() => setLabelHover(true)}
        onMouseLeave={() => setLabelHover(false)}
        style={{
          flexShrink: 0, display: 'flex', alignItems: 'center', gap: 6,
          padding: '4px 8px', minHeight: 26, boxSizing: 'border-box', cursor: 'grab',
        }}
      >
        {/* Слот иконки: в покое — значок проекта, под курсором ярлыка — кнопка
            «убрать со стены» (та же механика, что у иконки панели в PanelShell) */}
        <span style={{
          position: 'relative', width: ICON_SLOT, height: ICON_SLOT, flexShrink: 0,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          {labelHover ? (
            <span
              draggable={false}
              onDragStart={e => e.preventDefault()}
              style={{ position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)', display: 'flex' }}
            >
              <IconButton size="xs" variant="soft" title="Убрать со стены" onClick={() => removeChat(session.id)}>
                <X size={14} strokeWidth={ICON_STROKE} />
              </IconButton>
            </span>
          ) : project ? (
            <ProjectIcon project={project} size={ICON_SLOT} radius={R.sm} />
          ) : null}
        </span>
        <span style={{
          flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {project === null ? 'проект недоступен' : project ? project.name : 'Чат вне проекта'}
          {busy && <span style={{ color: status === 'waiting' ? C.danger : C.warning }}> · {status === 'working' ? 'идёт ход' : 'ждёт вас'}</span>}
        </span>
        {/* Переход к чату в полном виде. Тултип свой, а не нативный: у нативного
            секундная задержка, и на плотной полосе он читается как «подсказки нет» */}
        <span
          style={{ position: 'relative', display: 'flex', flexShrink: 0 }}
          onMouseEnter={() => setZoomTip(true)}
          onMouseLeave={() => setZoomTip(false)}
        >
          <IconButton size="xs" ariaLabel="Открыть чат в проекте" onClick={onZoom}>
            <SquareArrowOutUpRight size={ICON_SIZE.xs} strokeWidth={2} />
          </IconButton>
          {zoomTip && (
            <span style={{
              position: 'absolute', top: 'calc(100% + 6px)', right: 0, zIndex: Z.dropdown,
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md,
              boxShadow: SHADOW.dropdown, padding: '4px 9px',
              fontSize: FS.sm, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap',
              fontFamily: FONT.sans, pointerEvents: 'none',
            }}>
              Открыть чат в проекте
            </span>
          )}
        </span>
      </div>

      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {project === null ? (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 20, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, textAlign: 'center' }}>
            Проект этого чата недоступен — откройте его в разделе «Проекты» или уберите чат со стены.
          </div>
        ) : (
          <ChatPanel
            session={session}
            project={project ?? undefined}
            embedded
            attachedFiles={attachedFiles}
            onAttachedFilesChange={setAttachedFiles}
            onOpenFile={onOpenFile}
            // Поле ввода видно у всех колонок; активная лишь забирает в него курсор
            // (сигнал растёт на переходе «стала активной») — кликнул и пишешь
            composerFocusSignal={focusSignal}
            // Шапка чата — вторая ручка перетаскивания колонки (первая — ярлык выше)
            headerDragProps={{ draggable: true, onDragStart: e => startOrderDrag(e, index) }}
            // Смена модели/режима/цикла из колонки — снимок в сторе стены обязан обновиться
            onSessionUpdated={updateChat}
          />
        )}
      </div>
    </Island>
  );
}
