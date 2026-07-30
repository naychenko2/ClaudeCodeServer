// Панель «Документы» рельсы проекта: документация (README.md + docs/**) как связный корпус —
// дерево документов, превью с оглавлением, поиск, переходы по ссылкам и обратные ссылки.
//
// Разграничение с соседями: «Файлы» — дерево репозитория для работы с кодом, «Заметки» —
// личный vault вне репы, «Знания» — семантический поиск через Dify. Здесь — структура и
// связность репозиторной документации.
//
// Колонка узкая, поэтому превью тут для чтения «по месту», а крупное чтение — кнопкой
// «развернуть» в центральной области (тот же FileViewer, что и для остальных файлов).

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { DndContext, MouseSensor, TouchSensor, closestCenter, useSensor, useSensors } from '@dnd-kit/core';
import { SortableContext, arrayMove, useSortable, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
// ChevronsDownUp/ChevronsUpDown вернутся вместе с кнопками уровней папок (см. controls)
import { BookOpenText, BookText, ChevronDown, ChevronRight, FileQuestion, Home, Link2, List, Maximize2, MessageSquarePlus, PanelBottom, Pin, PinOff, Search, SlidersHorizontal, X } from 'lucide-react';
import type { Project, DocEntry, DocDetail, DocSearchHit, DocsScopeInfo } from '../../types';
import { api } from '../../lib/api';
import { onFilesChanged } from '../../lib/signalr';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, EmptyState, IconButton, IconSegmented, Menu, PanelHeaderSlot, TextField, useHasPanelHeader } from '../../components/ui';
import { DocsScopeDialog } from './DocsScopeDialog';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { ListDateDivider, LIST_FLASH_CLASS, LIST_FLASH_MS } from '../../components/ListDateDivider';
import { getExtMeta as extMeta } from '../../components/FileExplorer';
import { useHeadings, scrollToHeading } from '../../hooks/useHeadings';
import { resolveDocImage, resolveDocLink, sliceSection, slugify } from '../../lib/docsLinks';
import { DRAG_MOUSE_ACTIVATION, DRAG_TOUCH_ACTIVATION } from '../../lib/dnd';

interface Props {
  project: Project;
  // Открыть файл в центральной области: «развернуть» документ и переходы на код
  onOpenFile: (path: string) => void;
  // Прикрепить путь к сообщению чата (документ целиком — вложением, не текстом)
  onAttachToChat: (path: string) => void;
  // Что открыто в центральной области. Панель сама этого не знает, а выделение в списке
  // обязано за этим следовать: закрыли файл в центре — строка перестаёт быть выбранной
  activeFilePath?: string | null;
  // Закрыть файл в центре (тот же обработчик, что у крестика просмотрщика)
  onCloseFile?: () => void;
}

// Высота зоны дерева документов: тянется хендлом, переживает перезагрузку.
// Приём тот же, что у зоны скоупов в «Изменениях» (GitChangesRail) — одинаковое
// поведение ресайза в панелях рельсы.
// Высота строки списка: список длинный (десятки документов), поэтому плотный
const ROW_H = 22;

// Порог, в пределах которого второй клик считается двойным (и отменяет одиночный)
const DOUBLE_CLICK_MS = 220;


// Тумблер нижней зоны. По умолчанию выключена: панель открывают ради списка, а превью —
// осознанный режим. Решение пользователя, поэтому переживает перезагрузку
const PREVIEW_KEY = 'cc_docs_preview';

const TREE_H_KEY = 'cc_docs_tree_h';
const TREE_H_DEFAULT = 220;
const TREE_H_MIN = 80;
const TREE_H_MAX = 700;

// Закреплённый список папок над документами (свой блок с прокруткой и высотой) снят:
// список папок открывается поповером по кнопке. Ключи и размеры нужны только вместе
// с ним — вернуть их можно одним движением:
// const FOLDERS_PIN_KEY = 'cc_docs_folders_pin';
// const FOLDERS_H_KEY = 'cc_docs_folders_h';
// const FOLDERS_H_DEFAULT = 110;
// const FOLDERS_H_MIN = ROW_H * 2;
// const FOLDERS_H_MAX = 400;

// Свёрнутые папки — привязаны к проекту: в разных репозиториях папки разные, и общий
// список сворачивал бы в одном то, чего в другом нет
const COLLAPSED_KEY = 'cc_docs_collapsed';

// Закреплённые документы — тоже по проекту
const PINNED_KEY = 'cc_docs_pinned';

// Ключ группы закреплённых. С нулевым символом: имя папки таким быть не может,
// а значит группа не столкнётся с настоящей папкой в состоянии свёрнутых
const PINNED_GROUP = '\u0000pinned';

// Подстраховка на случай, если браузер не знает scrollend (он есть не везде): дольше
// этого плавная прокрутка списка всё равно не длится
const SCROLL_SETTLE_MS = 420;

// Разворачивание группы. Тем же числом задержан прыжок к свёрнутой папке: пока высота
// едет, координаты строк меняются, и прокрутка приехала бы не туда
const EXPAND_MS = 180;

// Расширения дефолтной области (группа «Markdown»), по которым панель узнаёт документ,
// пока не спросила настройку у сервера. Полный каталог живёт там (DocsIndexService.
// TypeGroups) — здесь нужно лишь решить, перечитывать ли индекс после правок на диске
const DEFAULT_DOC_EXTS = ['.md'];

// Начальный документ панели («Начало»): назначенный в настройке либо README корня.
// Кто именно — решает бэкенд; панель только помечает его домиком и открывает на всю высоту
const HOME_KEY = 'cc_docs_home';

// Папка пути («docs/adr/x.md» → «docs/adr»); файл в корне — пустая строка
function folderOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

// Папка как подпись группы: слеш читается как «путь к файлу», а здесь это ветка
// оглавления — точка-разделитель ведёт взгляд по уровням и не спорит с путями документов
function folderLabel(folder: string): string {
  return folder.split('/').join(' · ');
}

// Подпись группы: у закреплённых своя, у папок — путь через точку
function groupLabel(folder: string): string {
  return folder === PINNED_GROUP ? 'Закреплённые' : folderLabel(folder);
}

// Настоящая папка: у корневой группы подписи нет, а у закреплённых она своя — ни ту,
// ни другую кнопки уровней не трогают и в глубину не считают. Нужна вместе с ними
// (см. закомментированные collapseLevel/expandLevel).
// function isFolderGroup(folder: string): boolean {
//   return !!folder && folder !== PINNED_GROUP;
// }

// Бейдж расширения в строке документа; у начального — домик вместо него
function DocBadge({ path, home }: { path: string; home?: boolean }) {
  if (home)
    return <Home size={13} strokeWidth={2.2} style={{ flexShrink: 0, color: C.accent }} />;
  const m = extMeta(path);
  return (
    <span style={{
      width: 16, height: 16, borderRadius: 4, flexShrink: 0,
      background: m.bg, color: m.fg,
      fontFamily: FONT.mono, fontSize: 7.5, fontWeight: 700,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      letterSpacing: '-0.02em',
    }}>{m.label}</span>
  );
}

// Подпись папки в списке документов: липкая, чтобы при прокрутке было видно, в какой
// папке находишься. Подложка постоянная и всего на тон теплее полотна — в потоке её
// почти не видно, а прилипнув она перекрывает уезжающие строки.
// Отличать «прилипла / не прилипла» пробовали наблюдателем, но в панели несколько
// вложенных скроллеров (список, превью, закреплённые папки), и корень наблюдения
// приходилось угадывать — постоянный фон делает то же самое без единого условия.
function FolderSticky({ folder, flash, collapsed, hidden, onToggle }: {
  folder: string;
  flash: boolean;
  collapsed: boolean;
  // Сколько документов скрыто — показываем только у свёрнутой: у развёрнутой они и так видны
  hidden: number;
  onToggle: () => void;
}) {
  return (
    // Фон — ровно полотно панели: в потоке подпись выглядит как раньше, а прилипнув
    // перекрывает уезжающие строки. Отрицательные поля тянут подложку на всю ширину
    // списка (у него свой боковой отступ), иначе строки просвечивали бы по краям
    <div style={{
      // top отрицательный: плашка прилипает вплотную к краю списка, а не ниже него.
      // Компенсируем и верхний отступ списка, и собственный отступ разделителя —
      // иначе между краем и подписью остаётся полоска, сквозь которую видно строки
      // Левый отступ на 4px больше прочих: с ним шеврон встаёт ровно в колонку бейджей
      // расширений (строка документа сдвинута на SP.sm, а разделитель тянет подложку
      // за край списка отрицательным полем — эти сдвиги и складываются)
      position: 'sticky', top: -(SP.xs + 5), zIndex: 1,
      background: C.bgWhite, margin: `0 -${SP.xs}px`, padding: `${SP.xs}px ${SP.xs}px 0 ${SP.sm}px`,
    }}>
      <ListDateDivider
        title={groupLabel(folder)}
        align="left" dense flash={flash}
        onClick={onToggle}
        titleAttr={`${groupLabel(folder)} — ${collapsed ? 'показать' : 'скрыть'} документы`}
        leading={
          // Ширина как у бейджа расширения: шеврон встаёт с документами в одну колонку,
          // и левый край списка читается одной линией сверху вниз
          <span style={{
            width: 16, flexShrink: 0, display: 'flex',
            alignItems: 'center', justifyContent: 'center',
          }}>
            <ChevronRight
              size={12} strokeWidth={2.4}
              style={{
                color: flash ? C.accent : C.textMuted,
                // Поворотом, а не второй иконкой: состояние читается как продолжение
                // движения, и подпись не дёргается на кадр при смене
                transform: collapsed ? 'none' : 'rotate(90deg)', transition: 'transform .15s ease',
              }}
            />
          </span>
        }
        trailing={collapsed
          ? <span style={{ flexShrink: 0, fontSize: 10, color: C.textMuted }}>{hidden}</span>
          : undefined}
      />
    </div>
  );
}

// Строка папки в списке переходов (поповер и закреплённый блок — один и тот же список).
// Отдельным компонентом ради собственного состояния наведения: в списке из десятка папок
// без подсветки не видно, куда попадёт клик.
function FolderRow({ folder, count, current, onJump }: {
  folder: string;
  count: number;
  current: boolean;
  onJump: () => void;
}) {
  const [hover, setHover] = useState(false);
  const label = folder ? groupLabel(folder) : 'корень проекта';
  return (
    <button
      onClick={onJump}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={label}
      style={{
        ...rowStyle, minHeight: ROW_H,
        // Текущая папка — тем же выделением, что выбранный документ (список постоянно
        // на виду, одной жирности мало); наведение мягче, чтобы эти два состояния
        // не спорили между собой
        background: current ? C.bgSelected : hover ? C.bgInset : 'transparent',
        color: current || hover ? C.textHeading : C.textSecondary,
        fontWeight: current ? 600 : 400,
      }}
    >
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
      <span style={{ marginLeft: 'auto', color: C.textMuted, fontSize: FS.xs }}>{count}</span>
    </button>
  );
}

// Строка оглавления документа. Тот же вид, что у строки папки в списке переходов:
// это одна и та же роль — «прыгнуть к месту», и разной плотностью они бы спорили
function TocRow({ text, indent, onJump, onQuote }: {
  text: string;
  indent: number;
  onJump: () => void;
  onQuote: () => void;
}) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onJump}
      // Раздел в чат — правым кликом. Раньше для этого у каждой строки висела стрелка,
      // но в узком поповере ряд иконок забирал больше внимания, чем сами заголовки
      onContextMenu={e => { e.preventDefault(); onQuote(); }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={`${text}\nПравый клик — раздел в чат цитатой`}
      style={{
        ...rowStyle, minHeight: ROW_H,
        paddingLeft: SP.sm + indent,
        background: hover ? C.bgInset : 'transparent',
        color: hover ? C.textHeading : C.textSecondary,
      }}
    >
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{text}</span>
    </button>
  );
}

// Строка документа. Отдельным компонентом ради состояния наведения: список длинный,
// и держать его в панели значило бы перерисовывать все строки на каждое движение мыши.
// Бейдж расширения под курсором превращается в булавку — отдельной кнопки закрепления
// в строке нет места, а место иконки всё равно занято состоянием документа
function DocRow({ doc, selected, home, pinned, onOpen, onExpand, onTogglePin }: {
  doc: DocEntry;
  selected: boolean;
  home: boolean;
  pinned: boolean;
  onOpen: () => void;
  onExpand: () => void;
  onTogglePin: () => void;
}) {
  const [hover, setHover] = useState(false);
  const [pinHover, setPinHover] = useState(false);
  const showPin = hover || pinned;
  return (
    <div
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => { setHover(false); setPinHover(false); }}
      style={{
        display: 'flex', alignItems: 'center', borderRadius: R.md,
        background: selected ? C.bgSelected : 'transparent',
        minHeight: ROW_H, paddingLeft: SP.sm,
      }}
    >
      <button
        onClick={onTogglePin}
        onMouseEnter={() => setPinHover(true)}
        onMouseLeave={() => setPinHover(false)}
        title={pinned ? 'Открепить — вернуть в свою папку' : 'Закрепить внизу списка'}
        style={{
          width: 16, height: 16, flexShrink: 0, padding: 0, border: 'none',
          background: 'transparent', cursor: 'pointer',
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}
      >
        {/* Иконка показывает, что даст клик: булавка под курсором у обычного документа,
            перечёркнутая — у закреплённого. В покое у закреплённого булавка остаётся:
            иначе он ничем не отличается от соседей по группе */}
        {showPin
          ? (pinned && pinHover
            ? <PinOff size={12} strokeWidth={2.2} style={{ color: C.textSecondary }} />
            : <Pin size={12} strokeWidth={2.2} style={{ color: pinned ? C.accent : C.textMuted }} />)
          : <DocBadge path={doc.path} home={home} />}
      </button>
      <button
        onClick={onOpen}
        onDoubleClick={onExpand}
        // Только путь: подсказка про двойной клик висела над каждой строкой и мешала
        // читать сам путь, ради которого её и открывают
        title={doc.path}
        style={{
          ...rowStyle,
          flex: 1, minWidth: 0,
          // Без отступа под вложенность: группу уже обозначает разделитель сверху,
          // а сдвиг ломал общую левую линию списка
          paddingLeft: SP.xs,
          color: selected ? C.textHeading : C.textSecondary,
          fontWeight: selected ? 600 : 400,
        }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{doc.title}</span>
      </button>
    </div>
  );
}

// Закреплённая строка: та же строка документа, но перетаскиваемая — порядок в блоке
// задаёт пользователь. Жест и пороги общие с доской задач и деревом чатов (lib/dnd),
// поэтому клик по строке от перетаскивания отличается сдвигом, а не отдельной ручкой
function SortablePinnedRow({ doc, children }: { doc: DocEntry; children: ReactNode }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } =
    useSortable({ id: doc.path });
  return (
    <div
      ref={setNodeRef}
      style={{
        transform: CSS.Transform.toString(transform),
        transition,
        opacity: isDragging ? 0.4 : 1,
      }}
      {...attributes}
      {...listeners}
    >
      {children}
    </div>
  );
}

// Цитата раздела в композер: тем же механизмом, что «Про файл …» в FileViewer —
// текст ложится в ПУСТОЕ поле композера, набранный черновик важнее
function prefillComposer(text: string): void {
  sessionStorage.setItem('cc_pending_chat_prompt', text);
  window.dispatchEvent(new Event('cc-compose-prefill'));
}

export function DocsPanel({ project, onOpenFile, onAttachToChat, activeFilePath, onCloseFile }: Props) {
  // Есть ли у панели шапка (PanelShell). Нет — мобильный стек: контролы рисуем рядом
  const hasPanelHeader = useHasPanelHeader();
  const [index, setIndex] = useState<DocEntry[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [doc, setDoc] = useState<DocDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  // Строка поиска разворачивается по кнопке и закрывается крестиком, Esc либо выбором
  // найденного документа — держать её открытой после перехода незачем
  const [searchOpen, setSearchOpen] = useState(false);
  const [hits, setHits] = useState<DocSearchHit[] | null>(null);
  const [previewEnabled, setPreviewEnabled] = useState<boolean>(() => {
    try { return localStorage.getItem(PREVIEW_KEY) === '1'; } catch { return false; }
  });
  const [treeH, setTreeH] = useState<number>(() => {
    try {
      const n = Number(localStorage.getItem(TREE_H_KEY));
      return Number.isFinite(n) && n >= TREE_H_MIN ? n : TREE_H_DEFAULT;
    } catch { return TREE_H_DEFAULT; }
  });
  // Пин списка папок и высота закреплённого блока сняты вместе с ним самим
  // (см. закомментированную разметку ниже):
  // const [foldersPinned, setFoldersPinned] = useState(…);
  // const [foldersH, setFoldersH] = useState(…);
  const [backlinksOpen, setBacklinksOpen] = useState(false);
  const [tocAnchor, setTocAnchor] = useState<DOMRect | null>(null);
  // Домашний режим: README на всю панель. Он же стартовый — панель чаще открывают
  // «почитать про проект», чем искать конкретный документ в списке
  const [homeOpen, setHomeOpen] = useState<boolean>(() => {
    try { return localStorage.getItem(HOME_KEY) !== '0'; } catch { return true; }
  });
  const [homeDoc, setHomeDoc] = useState<DocDetail | null>(null);
  // Область документации (папки, файлы корня, типы). null — панель её не спрашивала:
  // диалог грузит настройку сам, а до его открытия хватает эвристики по индексу
  // (см. isDocPath ниже)
  // Настройка области целиком: из неё панель узнаёт начальный документ, а после правок
  // на диске — надо ли перечитывать индекс (isDocPath ниже)
  const [scopeInfo, setScopeInfo] = useState<DocsScopeInfo | null>(null);
  const [scopeOpen, setScopeOpen] = useState(false);
  // Список папок — то же, что оглавление для документа, только для самого списка:
  // групп до десятка, а документов десятки, и мотать до нужной надоедает
  // Прямоугольник кнопки-якоря: поповер рисуется fixed по нему (Menu), иначе
  // absolute внутри панели обрезался её краем, когда места мало
  const [foldersAnchor, setFoldersAnchor] = useState<DOMRect | null>(null);

  const folderRefs = useRef(new Map<string, HTMLDivElement>());
  // Папка, к которой только что прокрутили: подсвечиваем на секунду, иначе после
  // прыжка непонятно, куда смотреть. Тот же язык, что у подсветки панелей рельсы —
  // акцентная рамка (PanelShell flash)
  const [flashFolder, setFlashFolder] = useState<string | null>(null);
  // Отметка в списке папок: где пользователь сейчас. Одно состояние на два способа туда
  // попасть — прыжок по папке и открытие документа. Вычислять её из выбранного документа
  // было неверно: после прыжка в другую папку отметка оставалась на старой, потому что
  // выбор документа никуда не делся
  const [activeFolder, setActiveFolder] = useState<string | null>(null);
  // Свёрнутые папки: в корпусе с десятком разделов половина обычно не нужна, а список
  // длинный. Храним по проекту — папки у репозиториев разные
  const [collapsed, setCollapsed] = useState<Set<string>>(() => {
    try {
      const raw = localStorage.getItem(`${COLLAPSED_KEY}:${project.id}`);
      return new Set<string>(raw ? JSON.parse(raw) as string[] : []);
    } catch { return new Set<string>(); }
  });
  // Закреплённые — СПИСОК, а не множество: их порядок задаёт пользователь перетаскиванием,
  // и «как пришло из индекса» тут не годится
  const [pinnedOrder, setPinnedOrder] = useState<string[]>(() => {
    try {
      const raw = localStorage.getItem(`${PINNED_KEY}:${project.id}`);
      return raw ? JSON.parse(raw) as string[] : [];
    } catch { return []; }
  });
  const pinned = useMemo(() => new Set(pinnedOrder), [pinnedOrder]);
  // Пороги старта перетаскивания — общие для всех списков продукта (см. lib/dnd):
  // мышь по сдвигу, палец по долгому нажатию, иначе жест забрал бы прокрутку списка
  const pinSensors = useSensors(
    useSensor(MouseSensor, { activationConstraint: DRAG_MOUSE_ACTIVATION }),
    useSensor(TouchSensor, { activationConstraint: DRAG_TOUCH_ACTIVATION }),
  );
  const listRef = useRef<HTMLDivElement>(null);
  const flashTimer = useRef<number | null>(null);
  const settleTimer = useRef<number | null>(null);
  useEffect(() => () => {
    if (flashTimer.current) window.clearTimeout(flashTimer.current);
    if (settleTimer.current) window.clearTimeout(settleTimer.current);
  }, []);

  const saveCollapsed = (next: Set<string>) => {
    setCollapsed(next);
    try {
      localStorage.setItem(`${COLLAPSED_KEY}:${project.id}`, JSON.stringify([...next]));
    } catch { /* приватный режим — обойдёмся без запоминания */ }
  };

  const toggleFolder = (folder: string) => {
    const next = new Set(collapsed);
    if (!next.delete(folder)) next.add(folder);
    saveCollapsed(next);
  };

  const savePinned = (next: string[]) => {
    setPinnedOrder(next);
    try {
      localStorage.setItem(`${PINNED_KEY}:${project.id}`, JSON.stringify(next));
    } catch { /* приватный режим — обойдёмся без запоминания */ }
  };

  const togglePin = (path: string) => {
    savePinned(pinned.has(path)
      ? pinnedOrder.filter(p => p !== path)
      // Новый — в конец: он присоединяется к уже расставленным, а не лезет им в начало
      : [...pinnedOrder, path]);
  };

  const movePinned = (from: string, to: string) => {
    const a = pinnedOrder.indexOf(from);
    const b = pinnedOrder.indexOf(to);
    if (a < 0 || b < 0 || a === b) return;
    savePinned(arrayMove(pinnedOrder, a, b));
  };

  const flashNow = (folder: string) => {
    setFlashFolder(folder);
    if (flashTimer.current) window.clearTimeout(flashTimer.current);
    flashTimer.current = window.setTimeout(() => setFlashFolder(null), LIST_FLASH_MS);
  };

  // Прыжок к папке. Мигание — ПОСЛЕ остановки прокрутки: затухание длится 300 мс, и
  // запущенное вместе со смуз-скроллом оно отыгрывало вхолостую — до места доезжала
  // уже погасшая подпись. Если прокрутка не нужна (папка на месте, или список упёрся
  // в конец), мигаем сразу — ждать нечего.
  const scrollToFolder = (folder: string) => {
    const el = folderRefs.current.get(folder);
    const box = listRef.current;
    if (!el || !box) { flashNow(folder); return; }
    const delta = el.getBoundingClientRect().top - box.getBoundingClientRect().top;
    const limit = Math.max(box.scrollHeight - box.clientHeight, 0);
    const target = Math.min(Math.max(box.scrollTop + delta, 0), limit);
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    if (Math.abs(target - box.scrollTop) < 2) { flashNow(folder); return; }
    let done = false;
    const onEnd = () => {
      if (done) return;
      done = true;
      box.removeEventListener('scrollend', onEnd);
      flashNow(folder);
    };
    box.addEventListener('scrollend', onEnd);
    if (settleTimer.current) window.clearTimeout(settleTimer.current);
    settleTimer.current = window.setTimeout(onEnd, SCROLL_SETTLE_MS);
  };

  const jumpToFolder = (folder: string) => {
    setActiveFolder(folder);
    setFoldersAnchor(null);
    // Прыжок в свёрнутую папку показывал бы одну подпись — разворачиваем её, но
    // прокручиваем следующим кадром: до перерисовки геометрия ещё от свёрнутого списка
    if (collapsed.has(folder)) {
      toggleFolder(folder);
      if (settleTimer.current) window.clearTimeout(settleTimer.current);
      settleTimer.current = window.setTimeout(() => scrollToFolder(folder), EXPAND_MS + 20);
      return;
    }
    scrollToFolder(folder);
  };
  // Якорь, к которому нужно проскроллить после перехода по ссылке или из поиска.
  // Хранится ВМЕСТЕ с путём документа: между сменой документа и пересбором оглавления
  // есть кадр, где doc уже новый, а headings ещё от прежнего — без привязки к пути
  // якорь искался в чужом оглавлении, не находился и терялся.
  // В ref, а не в состоянии: значение нужно эффекту скролла, а не рендеру.
  const pendingAnchorRef = useRef<{ path: string; anchor: string } | null>(null);
  // Поиск активен от двух символов — результаты замещают список, пока запрос набран
  const searching = query.trim().length >= 2;

  const contentRef = useRef<HTMLDivElement>(null);
  const headings = useHeadings(contentRef, doc?.content);

  // Пути документов нижним регистром — по ним отличаем переход внутри панели
  // от открытия файла кода в центре
  const knownDocs = useMemo(
    () => new Set((index ?? []).map(d => d.path.toLowerCase())),
    [index]);

  // Документы по папкам: README и прочий корень — в безымянной группе сверху,
  // дальше подписанные группы («docs», «docs/adr», …) в алфавитном порядке
  const groups = useMemo<[string, DocEntry[]][]>(() => {
    const byFolder = new Map<string, DocEntry[]>();
    for (const d of index ?? []) {
      const slash = d.path.lastIndexOf('/');
      const folder = slash < 0 ? '' : d.path.slice(0, slash);
      const list = byFolder.get(folder);
      if (list) list.push(d); else byFolder.set(folder, [d]);
    }
    return [...byFolder.entries()].sort(([a], [b]) =>
      a === '' ? -1 : b === '' ? 1 : a.localeCompare(b));
  }, [index]);

  // Закреплённые дублируются ОТДЕЛЬНЫМ блоком у нижнего края списка: их закрепляют,
  // чтобы держать под рукой, а до нужного места длинного списка ещё надо домотать.
  // Из своей папки документ при этом никуда не девается — там он помечен булавкой
  const pinnedDocs = useMemo(() => {
    const byPath = new Map((index ?? []).map(d => [d.path, d]));
    // Порядок берём из списка закреплений, а не из индекса; пропавшие с диска молча
    // выпадают — чистить хранилище на каждое исчезновение файла не нужно
    return pinnedOrder.map(p => byPath.get(p)).filter((d): d is DocEntry => !!d);
  }, [index, pinnedOrder]);


  // «Наш ли это путь» после правок на диске. Область настраивается, поэтому судим по
  // текущему корпусу: файл уже в индексе либо лежит в папке, где документы есть. После
  // настройки в диалоге появляется точный список папок — тогда решает он.
  const docFolders = useMemo(
    () => new Set((index ?? []).map(d => folderOf(d.path))),
    [index]);

  const isDocPath = useCallback((raw: string) => {
    const p = raw.replace(/\\/g, '/');
    const lower = p.toLowerCase();
    if (knownDocs.has(lower)) return true;
    if (scopeInfo) {
      const { folders, rootFiles, types } = scopeInfo.selected;
      // Файл корня — только поимённо: там же лежит код, и расширение ни о чём не говорит
      if (!p.includes('/')) return rootFiles.some(f => f.toLowerCase() === lower);
      const exts = scopeInfo.typeGroups.filter(g => types.includes(g.key)).flatMap(g => g.extensions);
      return exts.some(e => lower.endsWith(e))
        && folders.some(f => lower.startsWith(`${f.toLowerCase()}/`));
    }
    // Настройка ещё не приехала: судим по текущему корпусу — тот же тип файла
    // в папке, где документы уже есть
    return DEFAULT_DOC_EXTS.some(e => lower.endsWith(e)) && docFolders.has(folderOf(p));
  }, [knownDocs, docFolders, scopeInfo]);

  const loadIndex = useCallback(() => {
    api.docs.index(project.id)
      .then(list => { setIndex(list); setError(null); })
      .catch(() => setError('Не удалось загрузить документацию'));
    // Настройка приезжает вместе с индексом: из неё берётся начальный документ, а без
    // неё панель не знает, показывать ли «Начало» вообще
    api.docs.scope(project.id).then(setScopeInfo).catch(() => { /* останется эвристика */ });
  }, [project.id]);

  useEffect(() => { loadIndex(); }, [loadIndex]);

  // Основной сценарий правок — Claude меняет docs/ прямо в чате; без подписки корпус
  // (дерево, превью, обратные ссылки) устаревал бы до перезагрузки страницы
  useEffect(() => onFilesChanged(({ projectId, paths }) => {
    if (projectId !== project.id || !paths.some(isDocPath)) return;
    // Достаточно перечитать индекс: открытый документ висит на нём зависимостью
    // эффекта ниже и перезагрузится следом
    loadIndex();
  }), [project.id, loadIndex, isDocPath]);

  // Документ сам не открывается: панель начинается со списка на всю высоту, превью
  // появляется по клику и закрывается крестиком — так список виден целиком, пока он и нужен

  // Содержимое выбранного документа. Сброс doc делает closeDoc — здесь только загрузка,
  // чтобы не дёргать setState синхронно в эффекте
  useEffect(() => {
    if (!selected) return;
    let alive = true;
    api.docs.doc(project.id, selected)
      .then(d => { if (alive) { setDoc(d); setError(null); } })
      .catch(() => { if (alive) setError('Документ не открывается'); });
    return () => { alive = false; };
  }, [project.id, selected, index]);

  // Скролл к разделу после того, как документ отрисован и оглавление собрано.
  // Пока цель не найдена — ждём следующего прохода (оглавление ещё пересобирается);
  // «висящий» якорь безопасен: следующий переход перезапишет его своим.
  useEffect(() => {
    const pending = pendingAnchorRef.current;
    if (!pending || !doc || doc.path !== pending.path) return;
    const target = headings.find(h => slugify(h.text) === pending.anchor);
    if (!target) return;
    scrollToHeading(target);
    pendingAnchorRef.current = null;
  }, [doc, headings]);

  // Поиск с задержкой: панель узкая, дёргать сервер на каждый символ незачем
  useEffect(() => {
    if (!searching) return;
    const timer = window.setTimeout(() => {
      api.docs.search(project.id, query.trim()).then(setHits).catch(() => setHits([]));
    }, 250);
    return () => window.clearTimeout(timer);
  }, [project.id, query, searching]);

  // Закрытие поиска гасит и запрос: строка исчезла бы, а список остался бы отфильтрованным
  const closeSearch = useCallback(() => { setSearchOpen(false); setQuery(''); }, []);

  // Режим превью: решение пользователя, поэтому переживает перезагрузку.
  // Объявлен до обработчиков кликов — они его вызывают
  const setPreview = (next: boolean) => {
    setPreviewEnabled(next);
    try { localStorage.setItem(PREVIEW_KEY, next ? '1' : '0'); } catch { /* квота */ }
  };

  const openDoc = useCallback((path: string, anchor: string | null = null) => {
    setSelected(path);
    // Переход по ссылке, из поиска или из обратных ссылок тоже переносит «где я»:
    // документ может лежать в другой папке, и отметка обязана уехать за ним
    setActiveFolder(folderOf(path));
    pendingAnchorRef.current = anchor ? { path, anchor } : null;
    // Документ выбран — поиск своё отработал: строка закрывается вместе с запросом,
    // и на месте результатов снова список
    closeSearch();
  }, [closeSearch]);

  // Клик по строке списка откладывается на порог двойного: иначе двойной клик успевал
  // открыть превью до того, как документ уходил в центр, и панель дёргалась зря
  const clickTimer = useRef<number | null>(null);
  useEffect(() => () => { if (clickTimer.current) window.clearTimeout(clickTimer.current); }, []);

  // Показ документа снят: выделение уходит вместе с ним, иначе список продолжал бы
  // показывать выбранным то, что уже закрыто
  const closeDoc = () => {
    if (clickTimer.current) { window.clearTimeout(clickTimer.current); clickTimer.current = null; }
    setSelected(null);
    setDoc(null);
    setTocAnchor(null);
    setBacklinksOpen(false);
  };

  // Строка выделена, пока документ реально ПОКАЗАН: в центральной области — пока открыт
  // там, в превью — пока зона включена и документ загружен. Проверка `previewEnabled`
  // обязательна: содержимое грузится на любой выбор (им живут переходы по ссылкам и
  // поиск), поэтому без неё выделение переживало бы закрытие файла в центре
  const isShown = (path: string) =>
    path === activeFilePath || (previewEnabled && !!doc && path === selected);

  const handleRowClick = (path: string) => {
    // Повторный клик по показанному документу закрывает его — той же строкой, что открыла
    if (path === activeFilePath) { closeDoc(); onCloseFile?.(); return; }
    // Только когда превью показано: иначе загруженный «про запас» документ съедал бы
    // первый клик после закрытия файла в центре — вместо открытия ничего бы не произошло
    if (previewEnabled && path === selected && doc) { closeDoc(); return; }
    // Выделение — сразу: откладывается загрузка документа, а не отклик на клик,
    // иначе строка подсвечивалась через порог двойного клика и это выглядело поломкой
    setSelected(path);
    setActiveFolder(folderOf(path));
    if (clickTimer.current) window.clearTimeout(clickTimer.current);
    clickTimer.current = window.setTimeout(() => {
      clickTimer.current = null;
      // Одиночный клик — способ чтения ТЕКУЩЕГО режима: с превью показываем по месту,
      // без него открываем в центре
      if (previewEnabled) openDoc(path);
      else onOpenFile(path);
    }, DOUBLE_CLICK_MS);
  };

  // Двойной клик — второй способ, тот, которого сейчас нет под одиночным. В режиме превью
  // это разворот в центре, в режиме списка — наоборот, открыть по месту (и включить зону,
  // иначе показывать документ негде)
  const handleRowDoubleClick = (path: string) => {
    if (clickTimer.current) { window.clearTimeout(clickTimer.current); clickTimer.current = null; }
    if (previewEnabled) { onOpenFile(path); return; }
    openDoc(path);
    setPreview(true);
  };

  // Картинка документа: путь в src относителен самого документа (как и ссылки), а
  // грузить её надо через файловый эндпоинт проекта. Внешние и data: оставляем как есть
  const resolveImage = useCallback((src: string) => {
    const target = doc ? resolveDocImage(doc.path, src) : null;
    return target ? api.files.fileUrl(project.id, target) : undefined;
  }, [doc, project.id]);

  // Начальный документ выбирает бэкенд: явно назначенный либо README корня. Панели
  // незачем знать это правило — она лишь показывает то, что ей назвали
  const homePath = scopeInfo?.home ?? null;

  const setHome = (next: boolean) => {
    setHomeOpen(next);
    try { localStorage.setItem(HOME_KEY, next ? '1' : '0'); } catch { /* квота */ }
  };

  // Содержимое README грузится отдельно от выбранного документа: домашний режим не
  // должен сбивать то, что читали в превью, — закрыл домик и вернулся ровно туда же
  useEffect(() => {
    if (!homeOpen || !homePath) return;
    let alive = true;
    api.docs.doc(project.id, homePath)
      .then(d => { if (alive) setHomeDoc(d); })
      .catch(() => { if (alive) setHomeDoc(null); });
    return () => { alive = false; };
  }, [project.id, homeOpen, homePath, index]);

  // Первый документ проекта: создаём README с заголовком-именем проекта и сразу
  // открываем его домашним видом. Заодно чиним область — README мог быть снят из
  // файлов корня, и созданный файл просто не попал бы в панель
  const [creating, setCreating] = useState(false);
  const createReadme = async () => {
    setCreating(true);
    try {
      // Файл может существовать и просто не входить в область (его сняли в настройке).
      // Тогда создавать нечего — иначе кнопка «создать» затирала бы чужой README
      const existing = await api.files.getContent(project.id, 'README.md').catch(() => null);
      if (existing?.content == null) {
        await api.files.createFile(project.id, 'README.md');
        // Заготовка без «воды»: заголовок, строка под описание и место под план
        await api.files.saveContent(project.id, 'README.md',
          `# ${project.name}\n\nКороткое описание проекта\n\n## С чего начать\n\nTo Do\n`);
      }
      const scopeInfo = await api.docs.scope(project.id);
      if (!scopeInfo.selected.rootFiles.some(f => f.toLowerCase() === 'readme.md')) {
        await api.docs.setScope(project.id, {
          folders: scopeInfo.selected.folders,
          rootFiles: [...scopeInfo.selected.rootFiles, 'README.md'],
          types: scopeInfo.selected.types.includes('markdown')
            ? scopeInfo.selected.types
            : [...scopeInfo.selected.types, 'markdown'],
        });
      }
      loadIndex();
      setHome(true);
      onOpenFile('README.md');   // и сразу в центре — его пойдут наполнять
    } catch { setError('Не удалось создать README.md'); }
    finally { setCreating(false); }
  };

  // Ссылка из README: документ области открывается в превью (включая зону, если она была
  // выключена — иначе переход некуда показать), файл кода уходит в центр
  const handleHomeLink = useCallback((href: string) => {
    if (!homePath) return;
    const link = resolveDocLink(homePath, href, knownDocs);
    if (!link) return;
    if (link.kind === 'repo') { onOpenFile(link.target); return; }
    if (link.kind !== 'doc') return;
    openDoc(link.target, link.anchor);
    setPreview(true);
  }, [homePath, knownDocs, onOpenFile, openDoc]);   // setPreview стабилен по составу

  // Клик по ссылке внутри превью: документ области — переход в панели,
  // файл проекта — открытие в центре, внешняя — ушла в новую вкладку без нас
  const handleDocLink = useCallback((href: string) => {
    if (!doc) return;
    const link = resolveDocLink(doc.path, href, knownDocs);
    if (!link) return;
    if (link.kind === 'doc') openDoc(link.target, link.anchor);
    else if (link.kind === 'repo') onOpenFile(link.target);
  }, [doc, knownDocs, onOpenFile, openDoc]);

  // Esc — из поля, а не только крестиком: поиск открыт с фокусом в нём
  useEffect(() => {
    if (!searchOpen) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeSearch(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [searchOpen, closeSearch]);

  // Вертикальный ресайз зоны: тянем хендл вниз — зона над ним растёт. Один обработчик
  // на обе границы («список / превью» и «папки / документы») — поведение должно совпадать
  const startResize = (
    e: React.PointerEvent,
    opts: { from: number; set: (h: number) => void; key: string; min: number; max: number },
  ) => {
    e.preventDefault();
    const startY = e.clientY;
    const startH = opts.from;
    let latest = startH;
    const onMove = (ev: PointerEvent) => {
      latest = Math.max(opts.min, Math.min(opts.max, startH + (ev.clientY - startY)));
      opts.set(latest);
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      try { localStorage.setItem(opts.key, String(Math.round(latest))); } catch { /* квота */ }
    };
    document.body.style.cursor = 'row-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  // Строка папки — одна на поповер и на закреплённый блок: это один и тот же список,
  // и расходиться в поведении они не должны
  const folderRow = (folder: string, count: number, current: boolean) => (
    <FolderRow
      key={folder || '__root'}
      folder={folder}
      count={count}
      current={current}
      onJump={() => jumpToFolder(folder)}
    />
  );

  // Кнопка и блок нужны, только когда есть что выбирать: с единственной папкой
  // список папок вёл бы сам в себя
  const hasFolderNav = groups.filter(([f]) => f).length > 1;

  // Уровни папок. Кнопки «свернуть/развернуть уровень» в шапку не переехали, поэтому
  // и обработчики лежат закомментированными — вернуть можно вместе с кнопками (см.
  // controls ниже). Сворачивание при этом никуда не делось: клик по подписи группы.
  //
  // const foldableFolders = groups.map(([f]) => f).filter(isFolderGroup);
  // // Глубина папки: «docs» — 1, «docs/design/mockups» — 3. По ней кнопки ходят
  // // ПО ОДНОМУ уровню за нажатие: «свернуть всё» одним махом прячет и нужное
  // const depthOf = (folder: string) => folder.split('/').length;
  //
  // const collapseLevel = () => {
  //   const open = foldableFolders.filter(f => !collapsed.has(f));
  //   if (!open.length) return;
  //   // Сворачиваем самый глубокий открытый уровень — дальше подъём к корню
  //   const deepest = Math.max(...open.map(depthOf));
  //   const next = new Set(collapsed);
  //   open.filter(f => depthOf(f) === deepest).forEach(f => next.add(f));
  //   saveCollapsed(next);
  // };
  //
  // const expandLevel = () => {
  //   const shut = foldableFolders.filter(f => collapsed.has(f));
  //   if (!shut.length) return;
  //   // Разворачиваем самый мелкий свёрнутый уровень: сначала верхние ветки
  //   const shallowest = Math.min(...shut.map(depthOf));
  //   const next = new Set(collapsed);
  //   shut.filter(f => depthOf(f) === shallowest).forEach(f => next.delete(f));
  //   saveCollapsed(next);
  // };

  // Домашний вид показывается, только когда README реально есть. Без этой проверки
  // сохранённый флаг прятал кнопки в проекте без README — панель оставалась с пустой
  // полосой сверху и списком, которым нечем управлять
  const homeView = homeOpen && homePath != null;
  // Поиск, папки и превью управляют списком: без документов им нечего делать
  const hasDocs = (index?.length ?? 0) > 0;
  // README лежит в проекте, но выпал из области (сняли в настройке). Кандидаты корня
  // считаются по всем поддерживаемым типам, поэтому знают о нём, даже когда он не выбран
  const readmeOnDisk = !!scopeInfo?.rootFileCandidates.some(c => c.exists && /^readme\./i.test(c.name));

  // Закрепление списка папок отдельным блоком над документами убрано: поповер
  // открывается кликом и закрывается им же, а постоянный блок съедал высоту списка.
  // Вернуть можно вместе с закомментированным блоком в разметке ниже.
  //
  // const pinFolders = (next: boolean) => {
  //   setFoldersPinned(next);
  //   setFoldersOpen(false);
  //   try { localStorage.setItem(FOLDERS_PIN_KEY, next ? '1' : '0'); } catch { /* квота */ }
  // };
  //
  // Открытие и закрытие по наведению убраны совсем: список папок и оглавление —
  // это Menu, он закрывается кликом вне (своя подложка), и таймеры ни к чему.

  const quoteSection = (slug: string, title: string) => {
    if (!doc) return;
    const section = sliceSection(doc.content, slug);
    if (!section) return;
    prefillComposer(`Вопрос по разделу «${title}» документа ${doc.path}:\n\n${section}\n\n`);
    setTocAnchor(null);
  };


  // Область настраивается, поэтому пустой список — не тупик: диалог открывается прямо
  // отсюда. Ранний return тут был бы вреден — вместе со списком исчезала бы и шапка,
  // а с ней единственная кнопка, которой это чинится
  const scopeDialog = scopeOpen && (
    <DocsScopeDialog
      projectId={project.id}
      onClose={() => setScopeOpen(false)}
      onSaved={info => { setScopeInfo(info); loadIndex(); }}
    />
  );

  // Контролы панели: штатное место для них — шапка карточки (PanelHeaderSlot),
  // а не собственный ряд под ней. Раньше ряд занимал целую полосу высоты в узкой
  // колонке ради иконок, которые прекрасно живут рядом с заголовком.
  // Что показывает панель: README или корпус. IconSegmented — тот же примитив
  // и размер, что у видов в «Задачах»
  const viewSwitch = homePath ? (
    <IconSegmented<'home' | 'list'>
      value={homeOpen ? 'home' : 'list'}
      options={[
        { value: 'home', label: 'Начало', icon: <Home size={14} strokeWidth={ICON_STROKE} /> },
        { value: 'list', label: 'Документы', icon: <BookText size={14} strokeWidth={ICON_STROKE} /> },
      ]}
      onChange={v => setHome(v === 'home')}
    />
  ) : null;

  const controls = (
    <>
      {/* Без шапки переключатель идёт первым в общем ряду — своего места слева там нет */}
      {!hasPanelHeader && viewSwitch}
      {/* Остальные — только в режиме «Документы»: в «Начале» ими нечем управлять */}
      {!homeView && <>
        {/* Поиск кнопкой, а не полем: колонка узкая, а поле занимало её почти
            целиком ради действия, которое нужно изредка */}
        <IconButton
          title={searchOpen ? 'Закрыть поиск' : 'Поиск по документам'}
          active={searchOpen || query.length > 0}
          onClick={() => searchOpen ? closeSearch() : setSearchOpen(true)}
          size="sm"
        >
          <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        {/* Папки списка — оглавление для самого списка. Появляется, только когда групп
            больше одной: с единственной папкой кнопка вела бы в никуда */}
        {hasFolderNav && (
          <IconButton
            title={foldersAnchor ? 'Скрыть список папок' : 'Список папок'}
            // Прямоугольник снимаем СРАЗУ: внутри функционального апдейта React уже
            // обнулил currentTarget, и обращение к нему роняло панель
            onClick={e => {
              const rect = e.currentTarget.getBoundingClientRect();
              setFoldersAnchor(a => (a ? null : rect));
            }}
            active={!!foldersAnchor}
            size="sm"
          >
            <List size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
        {/* Свернуть/развернуть уровень папок — в шапку не переехали: ряд там и так
            плотный, а сворачивание доступно кликом по подписи самой группы.
            Оставлено закомментированным до решения, нужны ли кнопки вообще.
        {foldableFolders.length > 0 && <>
          <IconButton title="Свернуть уровень папок" onClick={collapseLevel}
            disabled={!foldableFolders.some(f => !collapsed.has(f))} size="sm">
            <ChevronsDownUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
          <IconButton title="Развернуть уровень папок" onClick={expandLevel}
            disabled={!foldableFolders.some(f => collapsed.has(f))} size="sm">
            <ChevronsUpDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </>}
        */}
        {/* Режим работы панели: со встроенным превью или только список (тогда документ
            открывается сразу в центральной области) */}
        <IconButton
          title={previewEnabled ? 'Превью снизу включено — выключить' : 'Превью снизу выключено — включить'}
          active={previewEnabled}
          onClick={() => setPreview(!previewEnabled)}
          size="sm"
        >
          <PanelBottom size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        {/* Область документации: дефолт docs/, но соглашение о папке в проектах разное */}
        <IconButton title="Папки документации" onClick={() => setScopeOpen(true)} size="sm">
          <SlidersHorizontal size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </>}
    </>
  );

  if (error && !index)
    return <div style={emptyStyle}>{error}</div>;

  return (
    // position: relative — точка отсчёта для поповера папок: его кнопка уехала
    // порталом в шапку карточки, и без якоря он считал координаты от чужого предка
    <div style={{ position: 'relative', display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Контролы панели — в шапке карточки (PanelHeaderSlot). Свой ряд остаётся
          только там, где шапки нет; пустой панели он не нужен вовсе — управлять
          нечем, а всё нужное предлагает само пустое состояние */}
      {/* Переключатель вида — у самого названия панели: он отвечает на вопрос
          «что показываем», а не «что сделать», и в правой группе кнопок терялся */}
      {hasDocs && hasPanelHeader && homePath && (
        <PanelHeaderSlot side="left">{viewSwitch}</PanelHeaderSlot>
      )}
      {hasDocs && (hasPanelHeader
        ? <PanelHeaderSlot>{controls}</PanelHeaderSlot>
        // Шапки нет (мобильный стек) — рисуем те же контролы своим рядом
        : <div style={{
            flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.xs,
            padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.border}`,
          }}>{controls}</div>
      )}

      {/* Список папок — общим Menu в anchor-режиме: fixed по кнопке, с выбором
          направления и клампом в окно. Свой absolute обрезался краем панели,
          стоило ей стать пониже */}
      {foldersAnchor && (
        <Menu anchor={foldersAnchor} minWidth={260} maxHeight={320} onClose={() => setFoldersAnchor(null)}>
          {groups.map(([folder, docs]) => folderRow(folder, docs.length, folder === activeFolder))}
        </Menu>
      )}

      {/* Строка поиска — отдельным рядом СВЕРХУ, сразу под кнопками: результаты
          появляются ниже, и поле стоит над тем, что оно фильтрует */}
      {searchOpen && (
        <div style={{
          flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.xs,
          padding: `${SP.xs}px ${SP.md}px ${SP.sm}px`, borderBottom: `1px solid ${C.border}`,
        }}>
          <div style={{ position: 'relative', flex: 1, minWidth: 0 }}>
            <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
              style={{ position: 'absolute', left: SP.sm, top: '50%', transform: 'translateY(-50%)', color: C.textMuted, pointerEvents: 'none' }} />
            <TextField value={query} onChange={setQuery} placeholder="Поиск по документам" autoFocus
              style={{ height: 30, fontSize: FS.sm, paddingLeft: 28 }} />
          </div>
          <IconButton title="Закрыть поиск (Esc)" onClick={closeSearch} size="sm">
            <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </div>
      )}

      {/* Домашний режим: README на всю панель, поверх списка. Список не размонтируется —
          закрыл домик и он на прежнем месте, с прежней прокруткой */}
      {homeView ? (
        // Без своей шапки: заголовок и так первой строкой документа, а переключиться
        // и настроить область можно в ряду выше — вторая полоса кнопок была бы лишней
        <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: `${SP.md}px ${SP.md}px ${SP.xl}px` }}>
            {!homeDoc && <div style={emptyStyle}>Загружаем…</div>}
            {homeDoc && (
              <MarkdownViewer
                content={homeDoc.content}
                // Переходы по ссылкам ведут из README в остальную документацию, поэтому
                // клик закрывает домашний режим и открывает документ обычным путём
                onDocLink={href => { setHome(false); handleHomeLink(href); }}
                resolveImageSrc={src => {
                  const target = resolveDocImage(homePath, src);
                  return target ? api.files.fileUrl(project.id, target) : undefined;
                }}
              />
            )}
          </div>
        </div>
      ) : searching ? (
        <div style={{ flex: 1, overflowY: 'auto', padding: `${SP.xs}px 0` }}>
          {/* null — ответ ещё не пришёл (запрос уходит через 250 мс после ввода) */}
          {hits === null && <div style={emptyStyle}>Ищем…</div>}
          {hits?.length === 0 && <div style={emptyStyle}>Ничего не найдено</div>}
          {(hits ?? []).map((h, i) => (
            <button key={`${h.path}-${i}`} onClick={() => openDoc(h.path, h.slug)} style={hitStyle}>
              <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{h.title}</div>
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>{h.path}</div>
              <div style={{ fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xxs, lineHeight: 1.5 }}>{h.snippet}</div>
            </button>
          ))}
        </div>
      ) : (
        <>
          {/* Дерево документов. С выключенной нижней зоной занимает всю панель;
              с включённой — высоту, заданную хендлом ресайза */}
          <div style={previewEnabled
            ? { flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0, height: treeH }
            : { flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }
          }>
            {/* Закреплённый блок папок над списком убран: теперь список папок живёт
                только поповером по кнопке. Постоянный блок съедал высоту документов,
                а своя прокрутка и хендл высоты делали панель из двух списков.
                Вернуть можно вместе с pinFolders (см. закомментированное выше):

            {foldersPinned && hasFolderNav && (
              <>
                <div style={{
                  flexShrink: 0, height: foldersH, overflowY: 'auto',
                  padding: `${SP.xs}px ${SP.xs}px`,
                }}>
                  {groups.map(([folder, docs]) => folderRow(folder, docs.length, folder === activeFolder))}
                </div>
                <div
                  onPointerDown={e => startResize(e, {
                    from: foldersH, set: setFoldersH, key: FOLDERS_H_KEY,
                    min: FOLDERS_H_MIN, max: FOLDERS_H_MAX,
                  })}
                  title="Потяните, чтобы изменить высоту списка папок"
                  style={{
                    flexShrink: 0, height: 7, cursor: 'row-resize', background: C.bgMain,
                    borderTop: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}`,
                    display: 'flex', alignItems: 'center', justifyContent: 'center',
                  }}
                >
                  <div style={{ width: 28, height: 2, borderRadius: R.max, background: C.border }} />
                </div>
              </>
            )}
            */}

            <div ref={listRef} style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: `${SP.xs}px ${SP.xs}px` }}>
                {index?.length === 0 && (
                  // Общий примитив, а не своя вёрстка: пустые состояния в продукте
                  // выглядят одинаково, и это одно из них
                  <EmptyState
                    compact
                    icon={<BookOpenText size={20} strokeWidth={ICON_STROKE} />}
                    title="Документация пуста"
                    subtitle={readmeOnDisk
                      // Файл на месте, просто снят в настройке — «создать» было бы ложью
                      ? 'README есть в проекте, но не входит в область документации'
                      : 'Создайте начальный файл — или проверьте, что считается документацией'}
                    action={
                      <div style={{ display: 'flex', gap: SP.xs, justifyContent: 'center' }}>
                        <Button variant="primary" size="sm" loading={creating} onClick={createReadme}>
                          {readmeOnDisk ? 'Вернуть README в область' : 'Создать начальный файл'}
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => setScopeOpen(true)}>
                          Настроить область
                        </Button>
                      </div>
                    }
                  />
                )}
                {groups.map(([folder, docs]) => {
                  // Корневая группа без подписи — сворачивать её нечем и незачем
                  const isCollapsed = !!folder && collapsed.has(folder);
                  return (
                  <div
                    key={folder}
                    // Мигает вся секция целиком — подпись и её документы, чтобы после
                    // прыжка было видно границы группы, а не только её заголовок
                    className={flashFolder === folder ? LIST_FLASH_CLASS : undefined}
                    ref={el => {
                      if (el) folderRefs.current.set(folder, el);
                      else folderRefs.current.delete(folder);
                    }}
                  >
                    {/* Подпись папки тем же разделителем, что группирует чаты по дням:
                        общий приём для «границы группы» в списках — и никакой подложки,
                        которая спорила бы с выделением строки */}
                    {folder && (
                      <FolderSticky
                        folder={folder}
                        flash={flashFolder === folder}
                        collapsed={isCollapsed}
                        hidden={docs.length}
                        onToggle={() => toggleFolder(folder)}
                      />
                    )}
                    {/* Сворачивание высотой grid-строки: она анимируется от 0fr к 1fr, и
                        замерять высоту содержимого не нужно. visibility гасится ПОСЛЕ
                        анимации — иначе скрытые строки остаются в порядке обхода табом */}
                    <div style={{
                      display: 'grid',
                      gridTemplateRows: isCollapsed ? '0fr' : '1fr',
                      transition: isCollapsed
                        ? `grid-template-rows ${EXPAND_MS}ms ease, visibility 0s linear ${EXPAND_MS}ms`
                        : `grid-template-rows ${EXPAND_MS}ms ease`,
                      visibility: isCollapsed ? 'hidden' : 'visible',
                    }}>
                    <div style={{ overflow: 'hidden', minHeight: 0 }}>
                    {docs.map(d => (
                      <DocRow
                        key={d.path}
                        doc={d}
                        selected={isShown(d.path)}
                        home={d.path === homePath}
                        pinned={pinned.has(d.path)}
                        onOpen={() => handleRowClick(d.path)}
                        onExpand={() => handleRowDoubleClick(d.path)}
                        onTogglePin={() => togglePin(d.path)}
                      />
                    ))}
                    </div>
                    </div>
                  </div>
                  );
                })}
            </div>

            {/* Закреплённые — всегда на виду, у нижнего края списка. Своя прокрутка:
                закрепить можно и десяток, а вытеснять ими сам список нельзя.
                Свернуть их можно так же, как папку, — останется одна подпись */}
            {pinnedDocs.length > 0 && (
              <div style={{
                flexShrink: 0, maxHeight: '45%', overflowY: 'auto',
                padding: `0 ${SP.xs}px ${SP.xs}px`,
                borderTop: `1px solid ${C.border}`, background: C.bgWhite,
              }}>
                <FolderSticky
                  folder={PINNED_GROUP}
                  flash={false}
                  collapsed={collapsed.has(PINNED_GROUP)}
                  hidden={pinnedDocs.length}
                  onToggle={() => toggleFolder(PINNED_GROUP)}
                />
                <div style={{
                  display: 'grid',
                  gridTemplateRows: collapsed.has(PINNED_GROUP) ? '0fr' : '1fr',
                  transition: collapsed.has(PINNED_GROUP)
                    ? `grid-template-rows ${EXPAND_MS}ms ease, visibility 0s linear ${EXPAND_MS}ms`
                    : `grid-template-rows ${EXPAND_MS}ms ease`,
                  visibility: collapsed.has(PINNED_GROUP) ? 'hidden' : 'visible',
                }}>
                  <div style={{ overflow: 'hidden', minHeight: 0 }}>
                    <DndContext
                      sensors={pinSensors}
                      collisionDetection={closestCenter}
                      onDragEnd={e => {
                        if (e.over) movePinned(String(e.active.id), String(e.over.id));
                      }}
                    >
                      <SortableContext items={pinnedDocs.map(d => d.path)} strategy={verticalListSortingStrategy}>
                        {pinnedDocs.map(d => (
                          <SortablePinnedRow key={d.path} doc={d}>
                            <DocRow
                              doc={d}
                              selected={isShown(d.path)}
                              home={d.path === homePath}
                              pinned
                              onOpen={() => handleRowClick(d.path)}
                              onExpand={() => handleRowDoubleClick(d.path)}
                              onTogglePin={() => togglePin(d.path)}
                            />
                          </SortablePinnedRow>
                        ))}
                      </SortableContext>
                    </DndContext>
                  </div>
                </div>
              </div>
            )}
          </div>

          {/* Хендл ресайза границы «список / превью».
              Фон как у шапки панели: полоса читается частью её оформления, а не швом */}
          {previewEnabled && (
            <div
              onPointerDown={e => startResize(e, {
                from: treeH, set: setTreeH, key: TREE_H_KEY,
                min: TREE_H_MIN, max: TREE_H_MAX,
              })}
              title="Потяните, чтобы изменить высоту списка"
              style={{
                flexShrink: 0, height: 9, cursor: 'row-resize', background: C.bgMain,
                borderTop: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}`,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
            >
              <div style={{ width: 28, height: 2, borderRadius: R.max, background: C.border }} />
            </div>
          )}

          {/* Нижняя зона: живёт постоянно, пока включён тумблер. Без выбранного документа
              показывает подсказку — так граница зон не скачет при каждом открытии */}
          {previewEnabled && (
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            {doc && (
              <div style={{
                flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', gap: SP.xs,
                padding: `${SP.xs}px ${SP.sm}px`, borderBottom: `1px solid ${C.border}`,
              }}>
                <span style={{
                  fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>{doc.title}</span>
                <div style={{ flex: 1 }} />
                {headings.length > 0 && (
                  <IconButton
                    title="Оглавление"
                    onClick={e => {
                      const rect = e.currentTarget.getBoundingClientRect();
                      setTocAnchor(a => (a ? null : rect));
                    }}
                    active={!!tocAnchor}
                    size="sm"
                  >
                    <List size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                )}
                <IconButton title="Документ в чат — вложением" onClick={() => onAttachToChat(doc.path)} size="sm">
                  <MessageSquarePlus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
                <IconButton title="Развернуть в центре" onClick={() => onOpenFile(doc.path)} size="sm">
                  <Maximize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
                {/* Закрытие превью = выход в режим «только список»: зона не прячется на один
                    документ, чтобы вернуться от следующего же клика — режим и есть ответ */}
                <IconButton
                  title="Закрыть превью — остаться со списком"
                  onClick={() => { setPreview(false); closeDoc(); }}
                  size="sm"
                >
                  <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>

                {/* Оглавление — тем же Menu, что и список папок: строки говорят сами
                    за себя, закрывает клик вне. Свой absolute обрезался краем превью,
                    когда зона документа низкая */}
                {tocAnchor && headings.length > 0 && (
                  <Menu anchor={tocAnchor} minWidth={260} maxHeight={320} onClose={() => setTocAnchor(null)}>
                    {headings.map((h, i) => (
                      <TocRow
                        key={i}
                        text={h.text}
                        // Уровень заголовка — отступом: иначе плоский список не показывает
                        // вложенность разделов
                        indent={(h.level - 1) * SP.md}
                        onJump={() => { scrollToHeading(h); setTocAnchor(null); }}
                        onQuote={() => quoteSection(slugify(h.text), h.text)}
                      />
                    ))}
                  </Menu>
                )}
              </div>
            )}

            <div ref={contentRef} style={{ flex: 1, overflowY: 'auto', padding: `${SP.md}px ${SP.md}px ${SP.xl}px` }}>
              {error && <div style={emptyStyle}>{error}</div>}
              {!doc && !error && <div style={emptyStyle}>Выберите документ в списке</div>}
              {/* Файл без текста рендерить нечем — зато в центре его ждёт готовый
                  просмотрщик (pdf.js, OnlyOffice, картинки, плеер) */}
              {doc?.binary && (
                <div style={emptyStyle}>
                  <FileQuestion size={20} strokeWidth={ICON_STROKE} style={{ opacity: 0.5, marginBottom: SP.sm }} />
                  <div style={{ marginBottom: SP.md }}>Этот файл показывается только целиком</div>
                  <Button variant="ghost" size="sm" onClick={() => onOpenFile(doc.path)}>
                    Открыть в центре
                  </Button>
                </div>
              )}
              {doc && !doc.binary && (
                <MarkdownViewer content={doc.content} onDocLink={handleDocLink} resolveImageSrc={resolveImage} />
              )}
            </div>

            {/* Обратные ссылки: кто в документации ведёт на этот документ */}
            {doc && doc.backlinks.length > 0 && (
              <div style={{ flexShrink: 0, borderTop: `1px solid ${C.border}` }}>
                <button onClick={() => setBacklinksOpen(v => !v)} style={sectionHeadStyle}>
                  {backlinksOpen
                    ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                    : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  Ссылаются сюда
                  <span style={{ marginLeft: 'auto', color: C.textMuted, fontWeight: 400 }}>{doc.backlinks.length}</span>
                </button>
                {backlinksOpen && (
                  <div style={{ maxHeight: 140, overflowY: 'auto', padding: `0 ${SP.xs}px ${SP.xs}px` }}>
                    {doc.backlinks.map((b, i) => (
                      <button key={`${b.path}-${i}`} onClick={() => openDoc(b.path, b.anchor)}
                        title={b.path} style={{ ...rowStyle, color: C.textSecondary }}>
                        <Link2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, opacity: 0.6 }} />
                        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{b.title}</span>
                      </button>
                    ))}
                  </div>
                )}
              </div>
            )}
          </div>
          )}
        </>
      )}
      {scopeDialog}
    </div>
  );
}

const emptyStyle = {
  padding: `${SP.xl}px ${SP.md}px`, textAlign: 'center' as const,
  fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted,
};

const sectionHeadStyle = {
  display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%',
  padding: `${SP.sm}px ${SP.md}px`, border: 'none', background: 'transparent', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary,
  textTransform: 'uppercase' as const, letterSpacing: '0.03em',
};

const rowStyle = {
  display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%',
  padding: `1px ${SP.sm}px`, border: 'none', background: 'transparent',
  borderRadius: R.md, cursor: 'pointer', textAlign: 'left' as const,
  fontFamily: FONT.sans, fontSize: FS.sm, lineHeight: 1.35, minWidth: 0,
};

const hitStyle = {
  display: 'block', width: '100%', textAlign: 'left' as const,
  padding: `${SP.sm}px ${SP.md}px`, border: 'none', background: 'transparent', cursor: 'pointer',
  fontFamily: FONT.sans,
};

// Свой стиль поповера убран: оба списка рисует общий Menu в anchor-режиме — он
// сам выбирает направление и не даёт краю панели себя обрезать.
