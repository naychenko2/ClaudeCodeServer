// Док «Стены» — капсула ПОД доком проектов, в обоих режимах на одном месте:
// номера чатов стены, лупа (пикер чатов) и приёмник перетаскивания — перетащи
// карточку чата из панели «Чаты», и чат встанет колонкой. Кнопок входа и выхода
// тут НЕТ: на стену уводит клик по номерку чата, обратно — пилюля «Проекты» в
// шапке (на стене она подсвечена как активный раздел и гасит режим).
//
// При маунте лениво поднимает состав стены (initWall): addChat шлёт PUT полного
// состава, и без загруженного снимка дроп затирал бы чужие монеты.
import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { AlarmClock, Plus, Search } from 'lucide-react';
import { C, FONT, R } from '../../lib/design';
import { RailCapsule, RailHat, RailIconButton, RailSep } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { useWatchdogPresence } from '../../lib/watchdogPresence';
import { showToast } from '../../lib/toast';
import type { Session } from '../../types';
import { useChatActivity, STATUS_COLOR, STATUS_PULSE, type ActivityStatus } from '../../lib/projectActivity';
import { projectMainColor } from '../projects/projectUtil';
import { useWallState, initWall, addChatSafe, focusChat, swapChats, startOrderDrag, MAX_CHATS } from './wallStore';
import { WallPicker } from './WallPicker';

// Тип данных перетаскивания карточки чата (кладёт SessionList в плоском режиме)
export const WALL_DRAG_TYPE = 'cc-wall-chat';

// Подписи статуса — про ЧАТ (у дока проектов те же статусы говорят про проект)
const CHAT_STATUS_TITLE: Record<ActivityStatus, string> = {
  waiting: 'ждет ответа',
  working: 'работает',
  unread: 'непрочитанное',
};

export function WallDock({ onOpenWall, slots = 0 }: {
  // Признак режима: задан — мы в воркспейсе (клик по номерку уводит на стену),
  // не задан — мы уже на стене
  onOpenWall?: () => void;
  // Сколько колонок помещается на экране: чаты сверх этого числа получают кнопки
  slots?: number;
}) {
  const { chats, projects, focusId } = useWallState();
  const activity = useChatActivity();
  // Сторожа чатов: у номерка чата с живым сторожем — будильник в нижнем углу
  // (верхний занят точкой статуса)
  const watchdogs = useWatchdogPresence();
  // Тащат чат по экрану (мишень видна) и курсор именно над доком (мишень «горит»).
  // dragging слушаем на документе: dragover самой капсулы не срабатывает, пока
  // курсор не дойдёт до неё, а знать «сюда можно» надо заранее.
  const [dragging, setDragging] = useState(false);
  const [over, setOver] = useState(false);
  // Курсор над капсулой — номерки просыпаются цветом (как иконки в доке проектов):
  // в покое ряд спит, нейтральный контур кроме фокусного; навели — все встали цветом
  // своего проекта. Фокусный ВСЕГДА цветной, как активный проект в спящей рельсе
  const [railHover, setRailHover] = useState(false);
  // Счётчик enter/leave: переход курсора между ДОЧЕРНИМИ элементами капсулы шлёт
  // dragleave, и без счётчика мишень мигала и гасла на полпути
  const overDepth = useRef(0);

  useEffect(() => {
    const onStart = (e: DragEvent) => {
      if (e.dataTransfer?.types.includes(WALL_DRAG_TYPE)) setDragging(true);
    };
    const onEnd = () => { setDragging(false); setOver(false); overDepth.current = 0; setRailHover(false); };
    document.addEventListener('dragstart', onStart);
    document.addEventListener('dragend', onEnd);
    document.addEventListener('drop', onEnd);
    return () => {
      document.removeEventListener('dragstart', onStart);
      document.removeEventListener('dragend', onEnd);
      document.removeEventListener('drop', onEnd);
    };
  }, []);
  // Пикер живёт в самом доке: лупа есть в обоих режимах, и держать её состояние
  // в двух экранах-владельцах было бы дублем
  const [picker, setPicker] = useState(false);

  useEffect(() => { initWall(undefined); }, []);

  const handleDrop = async (e: React.DragEvent) => {
    e.preventDefault();
    setOver(false);
    setDragging(false);
    overDepth.current = 0;
    const raw = e.dataTransfer.getData(WALL_DRAG_TYPE);
    if (!raw) return;
    try {
      const s = JSON.parse(raw) as Session;
      const name = s.name?.trim() || 'Чат';
      const res = await addChatSafe(s);
      showToast('Стена',
        res === 'added' ? `«${name}» на стене`
        : res === 'duplicate' ? `«${name}» уже на стене`
        : `На стене уже ${MAX_CHATS} чатов — уберите лишний`);
    } catch { /* битые данные перетаскивания — игнорируем */ }
  };

  const onWall = !onOpenWall;

  return (
    <RailCapsule
      side="left"
      style={{ marginTop: 8 }}
      onMouseEnter={() => setRailHover(true)}
      onMouseLeave={() => setRailHover(false)}
      // Мишень как у рельсы панелей: пока чат тащат — пунктирная обводка, под
      // курсором сплошная акцентная с подложкой (PanelRail.railBorder)
      border={dragging ? (over ? `1px solid ${C.accent}` : `1px dashed ${C.textSecondary}`) : undefined}
      background={dragging && over ? C.accentMuted : undefined}
      onDragEnter={e => {
        if (!e.dataTransfer.types.includes(WALL_DRAG_TYPE)) return;
        overDepth.current++;
        setOver(true);
      }}
      onDragOver={e => { if (e.dataTransfer.types.includes(WALL_DRAG_TYPE)) e.preventDefault(); }}
      onDragLeave={() => { overDepth.current = Math.max(0, overDepth.current - 1); if (overDepth.current === 0) setOver(false); }}
      onDrop={handleDrop}
    >
      {/* Пока чат тащат, капсула ЦЕЛИКОМ становится мишенью: кнопки уступают место
          одной иконке — рельса не должна расти на лишний слот и дёргать раскладку */}
      {dragging ? (
        <span style={{
          width: 32, height: 32, borderRadius: R.md, flexShrink: 0, boxSizing: 'border-box',
          border: over ? `1px solid ${C.accent}` : `1px dashed ${C.textSecondary}`,
          background: over ? C.accentLight : 'transparent',
          color: over ? C.accent : C.textSecondary,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <Plus size={16} strokeWidth={ICON_STROKE} />
        </span>
      ) : (
      <>
      {/* Шляпка — только в покое: пока чат тащат, капсула ужимается до одной
          мишени, и ярлык рос бы ровно там, куда человек целится */}
      <RailHat side="left" label="Стена" title="Стена чатов" />

      {/* ВЕСЬ набор стены номерками — карта состава в ОБОИХ режимах: цвет номерка =
          цвет проекта (стена мульти-проектная, по цвету видно, из чего она собрана),
          точка над номерком — статус чата, тем же знаком, что в доке проектов.
          Главное тут невлезшие чаты: без точки ждущий за бортом чат был бы немым.
          • На стене: видимые (idx < slots) — полной яркости, клик фокусирует их
            колонку; невлезшие приглушены, клик ставит чат вместо ПРАВОЙ колонки,
            перетаскивание на конкретную колонку — вместо неё.
          • В воркспейсе: колонок нет, slots не играет — все номерки полной яркости,
            клик ставит фокус на чат и уводит на стену (focusChat + onOpenWall).
            Перетаскивание порядка здесь НЕ включено: бросать некуда (колонок нет),
            и draggable номерок позволил бы начать движение, которое нечем закончить. */}
      {chats.map((s, idx) => {
        const proj = s.projectId ? projects.get(s.projectId) : undefined;
        const projColor = proj ? projectMainColor(proj) : C.textMuted;
        // Цветной номерок: курсор в рельсе (ряд проснулся) ИЛИ фокусный. В покое —
        // нейтральный контур (текст/рамка в тон остальной рельсе), как у спящих
        // иконок проектов; навели на капсулу — каждый встал цветом своего проекта
        // Цветной номерок: курсор в рельсе (ряд проснулся) ИЛИ фокусный — НО фокусный
        // цветной только на стене: в воркспейсе стена не показана, и подсвечивать
        // «какой откроется первым» там не к чему (как и active ниже)
        const colored = railHover || (onWall && s.id === focusId);
        const color = colored ? projColor : C.textSecondary;
        const borderColor = colored ? projColor : C.border;
        const st = activity.get(s.id);
        const visible = onWall && idx < slots;
        // У чата на стене живой сторож: значок в нижнем углу номерка, подпись в плашке
        const watchdog = watchdogs.sessions.has(s.id);
        return (
          // Обёртка — якорь точки статуса: точка живёт НАД кнопкой (как в доке
          // проектов), внутри кнопки её съедало бы приглушение невлезших
          <span key={s.id} style={{ position: 'relative', display: 'flex', flexShrink: 0 }}>
            <RailIconButton
              side="left"
              variant="media"
              label={`${idx + 1}. ${s.name?.trim() || 'Без названия'}${st ? ` — ${CHAT_STATUS_TITLE[st]}` : ''}${watchdog ? ' — сторож ждёт' : ''}`}
              active={onWall && visible && s.id === focusId}
              wrapper={onWall ? {
                draggable: true,
                onDragStart: (e: React.DragEvent) => startOrderDrag(e, idx, 'swap'),
              } : undefined}
              onClick={() => {
                if (onWall) {
                  if (visible) focusChat(s.id);
                  else swapChats(idx, Math.max(0, slots - 1));
                } else {
                  // Фокус в общий стор — стена при монтировании подхватит его
                  // (focusId эфемерен, но переживает переключение хаба: initWall
                  // уже поднят воркспейсом, refresh фокус не сбрасывает)
                  focusChat(s.id);
                  onOpenWall?.();
                }
              }}
            >
              <span style={{
                width: 32, height: 32, borderRadius: R.md, boxSizing: 'border-box',
                border: `1px solid ${borderColor}`, color, background: 'transparent',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontFamily: FONT.sans, fontWeight: 700, fontSize: 13, lineHeight: 1,
                flexShrink: 0, userSelect: 'none',
                opacity: onWall ? (visible ? 1 : 0.55) : 1,
              }}>
                {idx + 1}
              </span>
            </RailIconButton>
            {st && (
              <span className={STATUS_PULSE[st]} style={{
                position: 'absolute', right: -2, top: -2, width: 8, height: 8, borderRadius: R.full,
                // Подложка = цвет холста: непрозрачна, закрывает номерок под точкой.
                // Цветная заливка — в ::after (классы cc-dot-* читают переменную)
                background: C.bgMain, border: `2px solid ${C.bgMain}`,
                '--cc-dot-c': STATUS_COLOR[st],
                boxSizing: 'content-box', pointerEvents: 'none',
              } as CSSProperties} />
            )}
            {/* Будильник сторожа — в нижнем углу (верхний занят точкой статуса), тот же
                знак, что у иконки проекта в доке проектов: accent-глиф на подложке цвета
                холста, читаемый и поверх приглушённого невлезшего номерка */}
            {watchdog && (
              <span style={{
                position: 'absolute', right: -2, bottom: -2, width: 10, height: 10,
                borderRadius: R.full, background: C.bgMain, border: `2px solid ${C.bgMain}`,
                boxSizing: 'content-box', pointerEvents: 'none',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}>
                <AlarmClock size={10} strokeWidth={2.4} color={C.accent} />
              </span>
            )}
          </span>
        );
      })}
      {chats.length > 0 && <RailSep />}

      {/* Поиск чата для стены — в ОБОИХ режимах: собрать стену можно, не покидая
          проект. Лупа, а не «плюс»: за кнопкой пикер с поиском по всем чатам, а
          «плюс» в этой капсуле занят мишенью перетаскивания выше */}
      <RailIconButton side="left" label="Найти чат для стены" onClick={() => setPicker(true)}>
        <Search size={16} strokeWidth={ICON_STROKE} />
      </RailIconButton>

      </>
      )}

      {picker && <WallPicker onClose={() => setPicker(false)} />}
    </RailCapsule>
  );
}
