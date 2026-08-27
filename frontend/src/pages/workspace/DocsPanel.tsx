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
import { BookOpenText, BookText, Check, ChevronDown, ChevronRight, ChevronsRight, FileQuestion, Folder, FolderCog, FolderTree, Home, Link2, List, Maximize2, MessageSquarePlus, PanelBottom, PenLine, Pin, PinOff, Plus, Search, SlidersHorizontal, Tags, Trash2, X } from 'lucide-react';
import type { Project, DocEntry, DocDetail, DocSearchHit, DocsScopeInfo } from '../../types';
import { api } from '../../lib/api';
import { onFilesChanged } from '../../lib/signalr';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, ConfirmDialog, Dot, EmptyState, FileTypeTile, IconButton, IconSegmented, Menu, MenuItem, MenuSep, PanelHeaderSlot, TextField, TocRow, useHasPanelHeader, usePanelHeaderHold } from '../../components/ui';
import { DocPropChip, docsSectionHeadStyle } from '../../features/docs/DocsProps';
import { badgeKeyOf, badgeOf, propDotColor, typeOf } from '../../lib/docsTypes';
import { DocsScopeDialog } from './DocsScopeDialog';
import { DocsTypesDialog } from './DocsTypesDialog';
import { DocsCreateDialog } from './DocsCreateDialog';
import { DocsRenameDialog } from './DocsRenameDialog';
import { DocsMoveDialog } from './DocsMoveDialog';
import { useRequestPanelFill } from './panelFill';
import { useIsTouch } from '../../lib/breakpoints';
import { useLongPress, type LongPressPoint } from '../../hooks/useLongPress';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { ListDateDivider, LIST_FLASH_CLASS, LIST_FLASH_MS } from '../../components/ListDateDivider';
import { useHeadings, scrollToHeading } from '../../hooks/useHeadings';
import { resolveDocImage, resolveDocLink, sliceSection, slugify } from '../../lib/docsLinks';
// Цитата раздела в композер: тем же каналом, что «Про файл …» в FileViewer и затравки
// AI-хаба — текст ложится в ПУСТОЕ поле композера, набранный черновик важнее
import { prefillComposer } from '../../lib/ai/startChat';
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
// Минимумы зон при нехватке высоты. Высота списка запомнена в пикселях, а панель
// переезжает между местами разной высоты — в низком месте запомненные 474 px съедали
// всё тело, превью схлопывалось в ноль вместе с хендлом ресайза, и вернуть его было
// уже нечем. Поэтому зоны сжимаются обе, но ни одна не пропадает совсем
const PREVIEW_MIN_H = 140;
const TREE_SQUEEZE_H = 64;

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
// Отдельно от COLLAPSED_KEY: «глубокое» сворачивание раздела прячет всё его поддерево
// (вложенные разделы целиком), а не только свои документы — это разные жесты и разные
// множества, иначе одиночный шеврон начал бы утаскивать подпапки
const DEEP_COLLAPSED_KEY = 'cc_docs_deep_collapsed';

// Закреплённые документы — тоже по проекту
const PINNED_KEY = 'cc_docs_pinned';

// Ключ группы закреплённых. С нулевым символом: имя папки таким быть не может,
// а значит группа не столкнётся с настоящей папкой в состоянии свёрнутых
const PINNED_GROUP = '\u0000pinned';

// Корень репозитория как цель создания. Тем же приёмом, что группа закреплённых: сам
// корень — пустая строка, а она в выборе означала бы «не выбрано», и отличить одно
// от другого было бы нечем
const ROOT_TARGET = '\u0000root';

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
//
// Вид панели — решение пользователя, поэтому переживает перезагрузку. Ключ прежний:
// раньше вид был тумблером «домашний да/нет», и старые значения '1'/'0' читаются как
// 'home'/'list' — иначе у всех, кто уже пользовался панелью, вид сбросился бы на дефолт
const VIEW_KEY = 'cc_docs_home';

// Что показывает панель: начальный документ целиком, только разделы корпуса или полное
// дерево документов. Порядок в переключателе тот же — от общего к частному
type DocsView = 'home' | 'sections' | 'list';

function readView(): DocsView {
  try {
    const raw = localStorage.getItem(VIEW_KEY);
    if (raw === 'list' || raw === '0') return 'list';
    if (raw === 'sections') return 'sections';
    return 'home';
  } catch { return 'home'; }
}

// Блок списка: подряд идущие документы одной папки. Не «папка целиком» — одна папка даёт
// столько блоков, сколько раз её документы прерываются разделом. Так порядок из .order
// (где документы и разделы вперемешку) переносится в плоский список без вложенности.
type DocBlock = {
  key: string;        // папка + номер блока: подписи одной папки повторяются
  folder: string;     // '' — корень проекта, у него подписи нет
  docs: DocEntry[];
};

// Папка пути («docs/adr/x.md» → «docs/adr»); файл в корне — пустая строка
function folderOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

// Слово «документ» в правильной форме для числа — как chatCountWord у списка чатов
function docCountWord(n: number): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'документ';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'документа';
  return 'документов';
}

// Имя строки .order: файл без папки и без расширения («docs/vision.md» → «vision»)
function orderName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1).replace(/\.[^.]+$/, '');
}

// Порядком в .order управляют только markdown-документы: строка «cover» без «cover.md»
// в wiki просто мусор, поэтому pdf и картинки идут в хвост по правилу индекса
function isMarkdown(path: string): boolean {
  return /\.md$/i.test(path);
}

// Переставить в плоском индексе ТЕ ЖЕ документы по занятым ими позициям. Строки одной
// группы не идут в индексе подряд (между ними стоят документы вложенных разделов),
// поэтому arrayMove по всему списку сдвинул бы чужие. Тот же приём, что на бэкенде
// со строками .order, — иначе оптимистичный порядок разошёлся бы с сохранённым
function reorderInPlace(list: DocEntry[], group: string[], next: string[]): DocEntry[] {
  const inGroup = new Set(group);
  const byPath = new Map(list.map(d => [d.path, d]));
  const result = [...list];
  let k = 0;
  for (let i = 0; i < result.length; i++) {
    if (!inGroup.has(result[i].path)) continue;
    const doc = byPath.get(next[k++]);
    if (doc) result[i] = doc;
  }
  return result;
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

// Ведущий эмодзи с заголовка. У родительской папки в линии папок он лишний: это
// приглушённый контекст, а не самостоятельная строка, и книжка/колба перед именем
// только зашумляют. Срезаем кластер целиком (эмодзи + variation selector + ZWJ-
// последовательности) и пробелы за ним. Заголовок из одного эмодзи откатываем к
// оригиналу — пустая подпись хуже эмодзи
function stripLeadingEmoji(title: string): string {
  const stripped = title
    .replace(/^(?:\p{Extended_Pictographic}|\p{M}|\p{Cf}|\s)+/u, '')
    .trim();
  return stripped || title;
}

// Настоящая папка: у корневой группы подписи нет, а у закреплённых она своя — ни ту,
// ни другую кнопки уровней не трогают и в глубину не считают. Нужна вместе с ними
// (см. закомментированные collapseLevel/expandLevel).
// function isFolderGroup(folder: string): boolean {
//   return !!folder && folder !== PINNED_GROUP;
// }

// Бейдж расширения в строке документа; у начального — домик вместо него.
// У markdown бейджа нет: в корпусе документации почти всё — md, и плитка перед каждым
// именем только зашумляет список. Слота под бейдж у md-строк тоже нет — булавка при
// наведении рисуется поверх текста (DocRow и подпись раздела в FolderSticky)
function DocBadge({ path, home }: { path: string; home?: boolean }) {
  if (home)
    return <Home size={13} strokeWidth={2.2} style={{ flexShrink: 0, color: C.accent }} />;
  if (isMarkdown(path)) return null;
  return <FileTypeTile name={path} />;
}

// Подпись папки в списке документов: липкая, чтобы при прокрутке было видно, в какой
// папке находишься. Подложка постоянная и всего на тон теплее полотна — в потоке её
// почти не видно, а прилипнув она перекрывает уезжающие строки.
// Отличать «прилипла / не прилипла» пробовали наблюдателем, но в панели несколько
// вложенных скроллеров (список, превью, закреплённые папки), и корень наблюдения
// приходилось угадывать — постоянный фон делает то же самое без единого условия.
function FolderSticky({ folder, title: titleProp, collapsed, hidden, onToggle, onOpenPage, onCollapseSubtree, subtreeCollapsed = false, pagePath, pinned = false, active = false, statusColor, statusTitle, onTogglePin, onContextMenu, press, pressing = false }: {
  folder: string;
  // Действия раздела правым кликом — те же, что у строки документа (переименование)
  onContextMenu?: (e: React.MouseEvent) => void;
  // На тач-раскладке правого клика нет — те же действия приходят долгим нажатием
  press?: React.DOMAttributes<HTMLDivElement>;
  pressing?: boolean;
  // Подпись группы: у раздела это заголовок его страницы («Расширения»), у обычной папки —
  // её путь (значение по умолчанию). Считает панель: она знает, есть ли у папки пара
  title?: string;
  collapsed: boolean;
  // Сколько документов скрыто — показываем только у свёрнутой: у развёрнутой они и так видны
  hidden: number;
  onToggle: () => void;
  // Есть у папки с парной страницей: клик по подписи открывает её, как узел дерева wiki.
  // Тогда шеврон выносится из подписи наружу — иначе кнопка сворачивания оказалась бы
  // вложена в кнопку открытия, а вложенные кнопки html не разрешает
  onOpenPage?: () => void;
  // Глубокое сворачивание (двойной шеврон + правая линия): прячет всё поддерево. Есть
  // только у раздела с вложенными подпапками — иначе прятать сверх своих документов нечего
  onCollapseSubtree?: () => void;
  subtreeCollapsed?: boolean;
  // Путь файла-страницы раздела: перед подписью рисуем бейдж типа (у markdown его нет,
  // как и у строк документов) либо булавку — раздел в списке читается как документ
  pagePath?: string;
  // Закрепление страницы раздела — как у документа: бейдж под курсором становится булавкой
  pinned?: boolean;
  // Страница раздела ПОКАЗАНА сейчас: выделяем подпись, как выделили бы строку документа.
  // Своей строки у страницы в дереве нет — она и есть эта подпись, поэтому без выделения
  // здесь открытого документа в списке не видно вообще
  active?: boolean;
  // Метка главного свойства страницы раздела — та же, что у строки документа. Своей
  // строки в дереве у страницы нет, поэтому без этого статус раздела не видно нигде,
  // хотя в файле он есть и в превью показывается
  statusColor?: string;
  statusTitle?: string;
  onTogglePin?: () => void;
}) {
  const title = titleProp ?? groupLabel(folder);
  // Наведение на любую часть зоны сворачивания (линия, левый или двойной шеврон)
  // подсвечивает оба шеврона — они управляют одним и тем же разделом
  const [foldHover, setFoldHover] = useState(false);
  const chevron = (
    <ChevronRight
      size={12} strokeWidth={2.4}
      style={{
        // Постоянно акцентом когда раздел свёрнут (как двойной у свёрнутого поддерева);
        // плюс под курсором на линии или шевронах
        color: (collapsed || foldHover) ? C.accent : C.textMuted,
        transform: collapsed ? 'none' : 'rotate(90deg)', transition: 'transform .15s ease',
      }}
    />
  );
  // Наведение на зону сворачивания вешаем одинаково на все её части
  const foldHoverProps = {
    onMouseEnter: () => setFoldHover(true),
    onMouseLeave: () => setFoldHover(false),
  };
  // Наведение на весь заголовок (для показа булавки) и на саму булавку — как у документа
  const [rowHover, setRowHover] = useState(false);
  const [pinHover, setPinHover] = useState(false);
  // Бейдж/булавка после левой линии: span, а не button — divider с onClick сам кнопка,
  // а button в button html не разрешает; клик не всплывает к открытию страницы
  // Отступ до заголовка — как у документов (SP.xs), а не общий gap divider'а (8): гасим
  // разницу отрицательным полем, иначе бейдж папки стоит дальше от подписи, чем у файлов
  const badgeGapFix = { marginRight: SP.xs - 8 };
  // У markdown-страницы раздела колонки бейджа нет (правило DocBadge): слот в покое
  // не держится, булавка при наведении ложится поверх заголовка (titleOverlay ниже)
  const pageIsMd = pagePath ? isMarkdown(pagePath) : false;
  const pinBadge = pagePath && !pageIsMd && (
    onTogglePin ? (
      <span
        onClick={e => { e.stopPropagation(); onTogglePin(); }}
        onMouseEnter={() => setPinHover(true)}
        onMouseLeave={() => setPinHover(false)}
        title={pinned ? 'Открепить — вернуть в свою папку' : 'Закрепить внизу списка'}
        style={{
          width: 16, height: 16, flexShrink: 0, cursor: 'pointer', ...badgeGapFix,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}
      >
        {rowHover || pinned
          ? (pinned && pinHover
            ? <PinOff size={12} strokeWidth={2.2} style={{ color: C.textSecondary }} />
            : <Pin size={12} strokeWidth={2.2} style={{ color: pinned ? C.accent : C.textMuted }} />)
          : <DocBadge path={pagePath} />}
      </span>
    ) : <span style={{ flexShrink: 0, display: 'flex', ...badgeGapFix }}><DocBadge path={pagePath} /></span>
  );
  if (onOpenPage) return (
    <div
      onContextMenu={onContextMenu}
      onMouseEnter={() => setRowHover(true)}
      onMouseLeave={() => setRowHover(false)}
      {...press}
      style={{
        position: 'sticky', top: -(SP.xs + 5), zIndex: 1,
        background: C.bgWhite, margin: `0 -${SP.xs}px`, padding: `${SP.xs}px ${SP.xs}px 0 ${SP.sm}px`,
        display: 'flex', alignItems: 'center',
        opacity: pressing ? 0.6 : 1, transition: 'opacity 0.1s',
      }}>
      <button
        onClick={onToggle}
        {...foldHoverProps}
        title={`${title} — ${collapsed ? 'показать' : 'скрыть'} документы раздела`}
        style={{
          // Своё поле у шеврона: шеврон вплотную к краю плашки смотрелся выпавшим
          // из колонки иконок документов
          width: 16, flexShrink: 0, height: 20, padding: 0, border: 'none',
          marginLeft: SP.xs,
          background: 'transparent', cursor: 'pointer',
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}
      >
        {chevron}
      </button>
      <div style={{ flex: 1, minWidth: 0 }}>
        <ListDateDivider
          title={title}
          dense
          onClick={onOpenPage}
          highlightOnHover
          active={active}
          // Бейдж типа/булавка раздела — сразу перед подписью, следом за шевроном
          beforeTitle={pinBadge}
          // У md-страницы раздела слота нет: булавка при наведении/у закреплённой
          // ложится поверх заголовка плашкой с фоном строки
          titleOverlay={pageIsMd && onTogglePin && (rowHover || pinned) ? (
            <span
              onClick={e => { e.stopPropagation(); onTogglePin(); }}
              onMouseEnter={() => setPinHover(true)}
              onMouseLeave={() => setPinHover(false)}
              title={pinned ? 'Открепить — вернуть в свою папку' : 'Закрепить внизу списка'}
              style={{
                position: 'absolute', left: -2, top: -3, bottom: -3, width: 16, zIndex: 1,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                // Фон — подложка заголовка раздела (bgWhite у sticky-плашки): гасит
                // буквы под булавкой, чтобы иконка читалась на тексте
                background: active ? C.accentMuted : rowHover ? C.bgSelected : C.bgWhite,
                borderRadius: R.md, cursor: 'pointer',
              }}
            >
              {pinned && pinHover
                ? <PinOff size={12} strokeWidth={2.2} style={{ color: C.textSecondary }} />
                : <Pin size={12} strokeWidth={2.2} style={{ color: pinned ? C.accent : C.textMuted }} />}
            </span>
          ) : undefined}
          // Правая линия делает то же, что двойной шеврон: у раздела с поддеревом —
          // глубокое сворачивание, у листового — обычное (прятать нечего сверх документов)
          onLineClick={onCollapseSubtree ?? onToggle}
          onLineHover={setFoldHover}
          lineTitleAttr={onCollapseSubtree
            ? `${title} — ${subtreeCollapsed ? 'показать' : 'скрыть'} весь раздел со вложенными`
            : `${title} — ${collapsed ? 'показать' : 'скрыть'} документы раздела`}
          titleAttr={`${title} — открыть страницу раздела`}
          // Точка статуса и счётчик скрытых — одним хвостом: точка ближе к подписи,
          // потому что она про саму страницу, а счётчик — про её содержимое
          trailing={(statusColor || collapsed) ? (
            <span style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.xs }}>
              {statusColor && (
                <span title={statusTitle} style={{ display: 'flex', pointerEvents: 'none' }}>
                  <Dot color={statusColor} size={6} />
                </span>
              )}
              {collapsed && <span style={{ fontSize: 10, color: C.textMuted }}>{hidden}</span>}
            </span>
          ) : undefined}
        />
      </div>
      {/* Двойной шеврон справа от линии — свернуть/развернуть всё поддерево. Отдельной
          кнопкой (а не внутри подписи): подпись — кнопка открытия, а button в button
          html не разрешает. Есть только у раздела с вложенными подпапками */}
      {onCollapseSubtree && (
        <button
          onClick={onCollapseSubtree}
          {...foldHoverProps}
          title={`${title} — ${subtreeCollapsed ? 'развернуть' : 'свернуть'} весь раздел со вложенными`}
          style={{
            width: 16, flexShrink: 0, height: 20, padding: 0, border: 'none',
            background: 'transparent', cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
        >
          <ChevronsRight
            size={12} strokeWidth={2.4}
            style={{
              // Постоянно акцентом когда поддерево свёрнуто; плюс под курсором на линии
              // или любом шевроне зоны
              color: (subtreeCollapsed || foldHover) ? C.accent : C.textMuted,
              transform: subtreeCollapsed ? 'none' : 'rotate(90deg)', transition: 'transform .15s ease',
            }}
          />
        </button>
      )}
    </div>
  );
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
        title={title}
        dense
        onClick={onToggle}
        titleAttr={`${title} — ${collapsed ? 'показать' : 'скрыть'} документы`}
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
                color: C.textMuted,
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
function FolderRow({ label, parent, count, current, onJump }: {
  // Готовая подпись: у раздела — заголовок его файла, у чистой папки — путь. Считает
  // владелец списка (ему доступны sectionPages), чтобы правило совпадало с деревом
  label: string;
  // Родитель приглушённо после названия, через ту же центральную точку, что в дереве
  parent?: string;
  count: number;
  current: boolean;
  onJump: () => void;
}) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onJump}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={parent ? `${label} · ${parent}` : label}
      style={{
        ...rowStyle, minHeight: ROW_H,
        // Текущая папка — тем же выделением, что выбранный документ (список постоянно
        // на виду, одной жирности мало); наведение мягче, чтобы эти два состояния
        // не спорили между собой. Полоски слева тут нет: это список переходов в
        // поповере, а не строка дерева — заливки хватает
        background: current ? C.accentMuted : hover ? C.bgSelected : 'transparent',
        color: current || hover ? C.textHeading : C.textSecondary,
        fontWeight: current ? 600 : 400,
      }}
    >
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flexShrink: 0 }}>{label}</span>
      {parent && (
        <>
          {/* Центральная точка перед родителем — как в подписи группы дерева */}
          <span aria-hidden style={{ fontSize: 10, color: C.textMuted, flexShrink: 0, margin: '0 -2px' }}>·</span>
          <span style={{
            fontSize: FS.xs, fontWeight: 400, color: C.textMuted,
            minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{parent}</span>
        </>
      )}
      <span style={{ marginLeft: 'auto', paddingLeft: SP.xs, color: C.textMuted, fontSize: FS.xs }}>{count}</span>
    </button>
  );
}

// Строка документа. Отдельным компонентом ради состояния наведения: список длинный,
// и держать его в панели значило бы перерисовывать все строки на каждое движение мыши.
// Бейдж расширения под курсором превращается в булавку — отдельной кнопки закрепления
// в строке нет места, а место иконки всё равно занято состоянием документа.
// У markdown-строк бейджа нет (см. DocBadge): булавка при наведении ложится поверх
// текста плашкой с фоном строки, в покое — только у закреплённых
function DocRow({ doc, selected, home, pinned, pinColumn = false, indent, count, statusColor, statusTitle, onJump, onOpen, onExpand, onTogglePin, onContextMenu, dropInto, press, pressing = false }: {
  doc: DocEntry;
  selected: boolean;
  home: boolean;
  pinned: boolean;
  // Метка главного свойства типа — ГОТОВЫМИ строками, а не схемой: строка списка остаётся
  // тупой, как с count и pinned, и не знает про типы документов
  statusColor?: string;
  statusTitle?: string;
  // Сдвиг строки вправо — вложенность раздела в виде «Разделы». В дереве его нет:
  // там вложенность обозначена подписью группы, а сдвиг ломал бы левую линию списка
  indent?: number;
  // Число документов внутри раздела — тем же приглушённым хвостом, что у строк списка
  // папок. У обычного документа считать нечего
  count?: number;
  // Переход к документам раздела в дереве. Есть только в виде «Разделы»: в самом дереве
  // прыгать некуда — группа уже на экране
  onJump?: () => void;
  onOpen: () => void;
  onExpand: () => void;
  onTogglePin: () => void;
  // Действия строки (переименование) — правым кликом, как в «Файлах»: в узкой колонке
  // постоянной кнопке «…» места нет, а жест у панелей должен быть один
  onContextMenu?: (e: React.MouseEvent) => void;
  // Строка — цель ВЛОЖЕНИЯ при перетаскивании: перетаскиваемый раздел уедет внутрь неё.
  // Рамкой, а не заливкой: заливка тут уже занята выделением и наведением, и третий
  // фон на том же месте читался бы как «выбрано», а не «сюда упадёт»
  dropInto?: boolean;
  // Тач-раскладка: действия строки приходят долгим нажатием, а обработчики удержания —
  // готовыми: таймер один на список, не на строку
  press?: React.DOMAttributes<HTMLDivElement>;
  pressing?: boolean;
  // Держать булавку в колонке слева, а не поверх текста. Включён в списке закреплённых:
  // там булавка видна у каждой строки ПОСТОЯННО, колонка ею занята всегда — прятать
  // её и рисовать поверх названий значило бы перекрывать текст без паузы
  pinColumn?: boolean;
}) {
  const [hover, setHover] = useState(false);
  const [pinHover, setPinHover] = useState(false);
  const [jumpHover, setJumpHover] = useState(false);
  const [openHover, setOpenHover] = useState(false);
  const showPin = hover || pinned;
  // Колонка бейджа есть только у не-markdown и начального документа (домик); у md
  // строки бейджа нет вовсе — булавка рисуется поверх текста, без пустого слота
  const overlayPin = !pinColumn && !home && isMarkdown(doc.path);
  // Строка с переходом (раздел в виде «Разделы») — это ДВЕ мишени: название открывает
  // страницу раздела, хвост уводит к его документам в дереве. Поэтому подсветка едет
  // не на строку целиком, а на каждую половину своим овалом — иначе одна общая заливка
  // обещала бы одно действие на всю ширину
  const split = !!onJump;
  return (
    <div
      onContextMenu={onContextMenu}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => { setHover(false); setPinHover(false); }}
      {...press}
      style={{
        display: 'flex', alignItems: 'center', borderRadius: R.md,
        // Наведение подсвечивает открываемую строку: документ откроется по клику,
        // и подложка под курсором это обещает. Выбранное — тем же способом, что
        // открытый файл в дереве «Файлов»: тёплая заливка плюс полоска у левого края.
        // Нейтральной подложкой оно не отличалось от наведения вовсе — bgSelected и
        // bgInset в светлой теме расходятся на пару единиц.
        // У строки на две мишени общей подложки под курсором нет: красится ровно та
        // половина, куда попадёт клик (ниже), иначе одна заливка обещала бы одно
        // действие на всю ширину
        background: selected ? C.accentMuted : hover && !split ? C.bgSelected : 'transparent',
        overflow: 'hidden',
        // Рамка цели вложения рисуется ВНУТРЬ (inset), иначе строка подпрыгивает
        // на пиксель и весь список дёргается под курсором. Пока строка — цель
        // перетаскивания, рамка важнее полоски выбранного: боксшэдоу у них общий
        boxShadow: dropInto ? `inset 0 0 0 1.5px ${C.accent}`
          : selected ? `inset 2px 0 0 ${C.accent}`
          : undefined,
        minHeight: ROW_H, paddingLeft: SP.sm + (indent ?? 0),
        // Отклик на удержание: строка притухает, пока палец держат
        opacity: pressing ? 0.6 : 1, transition: 'opacity 0.1s',
      }}
    >
      {!overlayPin && (
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
      )}
      <button
        onClick={onOpen}
        onDoubleClick={onExpand}
        onMouseEnter={() => setOpenHover(true)}
        onMouseLeave={() => setOpenHover(false)}
        // Только путь: подсказка про двойной клик висела над каждой строкой и мешала
        // читать сам путь, ради которого её и открывают
        title={doc.path}
        style={{
          ...rowStyle,
          flex: 1, minWidth: 0,
          // Точка отсчёта для булавки-оверлея md-строк (см. ниже)
          ...(overlayPin ? { position: 'relative' } : null),
          // Без отступа под вложенность: группу уже обозначает разделитель сверху,
          // а сдвиг ломал общую левую линию списка
          paddingLeft: SP.xs,
          // Под курсором именно на названии половина темнеет — видно, какая из двух
          // мишеней сработает. Радиуса у неё нет: овал общий, её край режет просвет
          ...(split ? {
            background: openHover && !selected ? C.bgSelected : 'transparent',
            borderRadius: 0, height: ROW_H, paddingRight: SP.sm,
          } : null),
          color: selected ? C.textHeading : C.textSecondary,
          fontWeight: selected ? 600 : 400,
        }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{doc.title}</span>
        {overlayPin && showPin && (
          // Булавка md-строки — поверх текста, в начале названия: колонки бейджа нет,
          // и пустой слот не держим. Плашка с фоном строки гасит буквы под собой,
          // клик уходит в булавку, а не в открытие документа
          <span
            onClick={e => { e.stopPropagation(); onTogglePin(); }}
            onMouseEnter={() => setPinHover(true)}
            onMouseLeave={() => setPinHover(false)}
            title={pinned ? 'Открепить — вернуть в свою папку' : 'Закрепить внизу списка'}
            style={{
              position: 'absolute', left: 0, top: 0, bottom: 0, width: 18, zIndex: 1,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              // Фон повторяет подложку ПОД булавкой (заливка строки либо заголовка в
              // split-строке), иначе на подсвеченной строке читался бы белый лоскут
              background: selected ? C.accentMuted
                : hover && !split ? C.bgSelected
                : split && openHover ? C.bgSelected
                : C.bgWhite,
              borderRadius: R.md, cursor: 'pointer',
            }}
          >
            {pinned && pinHover
              ? <PinOff size={12} strokeWidth={2.2} style={{ color: C.textSecondary }} />
              : <Pin size={12} strokeWidth={2.2} style={{ color: pinned ? C.accent : C.textMuted }} />}
          </span>
        )}
      </button>
      {/* Метка главного свойства типа — точкой: в строке высотой 22px плашка не помещается,
          а буква соврала бы («Предложено» и «Принято» начинаются одинаково). Стоит перед
          хвостом, то есть на «заголовочной» половине строки, и не перехватывает клик
          ни у одной из двух мишеней */}
      {statusColor && (
        <span title={statusTitle} style={{
          flexShrink: 0, display: 'flex', alignItems: 'center',
          paddingRight: SP.xs, pointerEvents: 'none',
        }}>
          <Dot color={statusColor} size={6} />
        </span>
      )}
      {/* Хвост строки раздела: сколько документов внутри. С onJump — кнопка перехода
          в дерево (вид «Документы» с прокруткой к этой группе): список разделов
          отвечает «куда идти», дерево — «что там лежит». Отдельной кнопкой, а не
          вторым жестом по строке: клик по названию открывает саму страницу раздела,
          и два разных перехода не должны делить одну мишень */}
      {count !== undefined && (onJump ? (
        <button
          onClick={onJump}
          onMouseEnter={() => setJumpHover(true)}
          onMouseLeave={() => setJumpHover(false)}
          title={`Показать документы раздела в дереве (${count})`}
          style={{
            flexShrink: 0, display: 'flex', alignItems: 'center', gap: 3,
            border: 'none', cursor: 'pointer',
            // Подложка своя только под курсором: общий фон строка уже дала, а вторая
            // мишень должна отзываться отдельно от названия
            background: jumpHover ? C.bgSelected : 'transparent',
            // Разделитель внутри общего овала — цветом приглушённого текста: рамочный
            // C.border на подсвеченной подложке всё ещё сливался, а здесь линия должна
            // читаться как граница двух мишеней. Только под курсором (в покое прозрачный,
            // но на месте — иначе от него дёргалась бы ширина)
            borderLeft: `1px solid ${hover ? C.textMuted : 'transparent'}`,
            padding: `0 ${SP.sm}px 0 ${SP.xs}px`, height: ROW_H,
            color: jumpHover ? C.accent : C.textMuted, fontFamily: FONT.sans, fontSize: FS.xs,
          }}
        >
          {/* Шеврон и подпись — на наведение всей СТРОКИ: увидеть, что у раздела есть
              второй переход, надо до того, как попал курсором именно в хвост.
              В покое остаётся тихая цифра */}
          {hover && <ChevronsRight size={12} strokeWidth={2.4} />}
          {hover && <span>документы</span>}
          {count}
        </button>
      ) : (
        <span style={{
          flexShrink: 0, padding: `0 ${SP.sm}px`, color: C.textMuted, fontSize: FS.xs,
        }}>{count}</span>
      ))}
    </div>
  );
}

// Перетаскиваемая строка документа: у закреплённых так задаётся их собственный порядок
// (живёт в localStorage), в группе — порядок страниц в .order репозитория. Жест и пороги
// общие с доской задач и деревом чатов (lib/dnd), поэтому клик по строке от
// перетаскивания отличается сдвигом, а не отдельной ручкой.
//
// На тач-раскладке перетаскивание строк выключено (disabled={touch}): пальцем оно
// начинается тем же удержанием, которым открываются действия строки, а два действия
// на один жест не повесить. Выбрано меню — переименовать и удалить нужно откуда
// угодно, а порядок документов меняют редко и обычно за компьютером
function SortableRow({ doc, disabled, children }: { doc: DocEntry; disabled?: boolean; children: ReactNode }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } =
    useSortable({ id: doc.path, disabled });
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
  // Всю высоту колонки просим ТОЛЬКО с нижней зоной: с ней панель — дерево плюс
  // чтение (превью в ладонь бессмысленно), без неё это список, и растягивать его до
  // нижней кромки не за чем — под ним лучше свободное место для соседней панели
  useRequestPanelFill(previewEnabled);
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
  const [savingProp, setSavingProp] = useState<string | null>(null);
  const [propsError, setPropsError] = useState<string | null>(null);
  const [typesOpen, setTypesOpen] = useState(false);
  // Вид панели. Стартовый — «Начало»: панель чаще открывают «почитать про проект»,
  // чем искать конкретный документ в списке
  const [view, setViewState] = useState<DocsView>(readView);
  const [homeDoc, setHomeDoc] = useState<DocDetail | null>(null);
  // Область документации (папки, файлы корня, типы). null — панель её не спрашивала:
  // диалог грузит настройку сам, а до его открытия хватает эвристики по индексу
  // (см. isDocPath ниже)
  // Настройка области целиком: из неё панель узнаёт начальный документ, а после правок
  // на диске — надо ли перечитывать индекс (isDocPath ниже)
  const [scopeInfo, setScopeInfo] = useState<DocsScopeInfo | null>(null);
  const [scopeOpen, setScopeOpen] = useState(false);
  // Тач-раскладка: строки выше, а действия строки (переименовать, удалить) приходят
  // долгим нажатием — правого клика на телефоне и планшете нет вовсе
  const touch = useIsTouch();
  const { pressingKey, pressProps } = useLongPress(touch);
  // Действия строки — правым кликом по документу или по подписи раздела, как в «Файлах».
  // Держим и сам документ, и точку клика: меню рисуется по курсору
  const [rowMenu, setRowMenu] = useState<{ doc: DocEntry; rect: DOMRect } | null>(null);
  const [renaming, setRenaming] = useState<DocEntry | null>(null);
  const [deleting, setDeleting] = useState<DocEntry | null>(null);
  // Перенос, ждущий подтверждения: жест перетаскивания двигает файлы на диске, и промах
  // мышью не должен уносить ветку молча
  const [moving, setMoving] = useState<{ doc: DocEntry; target: string } | null>(null);
  // Итог переименования строкой над списком: сколько ссылок починено и сколько осталось
  // битыми. Без него о пределе механизма («видно только область») никто бы не узнал
  const [renameNote, setRenameNote] = useState<string | null>(null);
  // Создание идёт двумя шагами, как в «Файлах»: меню видов по кнопке «Новый», затем
  // модалка с названием. Здесь — якорь меню и выбранный вид (null — модалка закрыта)
  const [createMenu, setCreateMenu] = useState<DOMRect | null>(null);
  const [createKind, setCreateKind] = useState<'doc' | 'section' | null>(null);
  // Папка, выбранная в меню создания явно. null — берётся та, в которой пользователь
  // сейчас находится: обычно создают рядом с тем, что читают, и лишний выбор ни к чему
  const [createInFolder, setCreateInFolder] = useState<string | null>(null);
  // Меню создания показывает список папок вместо видов — второй «страницей» того же
  // меню, а не вторым поповером: у него один якорь и одно закрытие кликом вне
  const [pickingFolder, setPickingFolder] = useState(false);
  // Меню настроек панели (шестерёнка в шапке): режим превью + область документации
  const [settingsAnchor, setSettingsAnchor] = useState<DOMRect | null>(null);
  // Пока открыт попап или поиск — контролы шапки не гаснут (общее решение,
  // как у FileExplorer/GitChangesRail): меню живёт порталом в body, курсор
  // уходит с карточки, и без удержания кнопка-триггер пропадала под попапом
  usePanelHeaderHold(!!settingsAnchor || !!createMenu || searchOpen);

  const folderRefs = useRef(new Map<string, HTMLDivElement>());
  // Папка, к которой только что прокрутили: подсвечиваем на секунду, иначе после
  // прыжка непонятно, куда смотреть. Тот же язык, что у подсветки панелей рельсы —
  // акцентная рамка (PanelShell flash)
  const [flashFolder, setFlashFolder] = useState<string | null>(null);
  // Где пользователь сейчас находится: папка последнего открытого документа или
  // раздела. Отсюда берётся цель создания — обычно создают рядом с тем, что читают
  const [activeFolder, setActiveFolder] = useState<string | null>(null);
  // Свёрнутые папки: в корпусе с десятком разделов половина обычно не нужна, а список
  // длинный. Храним по проекту — папки у репозиториев разные
  const [collapsed, setCollapsed] = useState<Set<string>>(() => {
    try {
      const raw = localStorage.getItem(`${COLLAPSED_KEY}:${project.id}`);
      return new Set<string>(raw ? JSON.parse(raw) as string[] : []);
    } catch { return new Set<string>(); }
  });
  // Разделы, у которых скрыто всё поддерево (глубокое сворачивание двойным шевроном)
  const [deepCollapsed, setDeepCollapsed] = useState<Set<string>>(() => {
    try {
      const raw = localStorage.getItem(`${DEEP_COLLAPSED_KEY}:${project.id}`);
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
  const dragSensors = useSensors(
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
    // Раздел глубоко свёрнут: одиночный шеврон (и правая линия листового раздела)
    // разворачивают его целиком. Иначе жест выглядит мёртвым — документы всё равно
    // скрыты глубоким сворачиванием, и toggle обычного collapsed ничего не меняет
    if (deepCollapsed.has(folder)) {
      const nd = new Set(deepCollapsed);
      nd.delete(folder);
      saveDeepCollapsed(nd);
      if (collapsed.has(folder)) {
        const nc = new Set(collapsed);
        nc.delete(folder);
        saveCollapsed(nc);
      }
      return;
    }
    const next = new Set(collapsed);
    if (!next.delete(folder)) next.add(folder);
    saveCollapsed(next);
  };

  const saveDeepCollapsed = (next: Set<string>) => {
    setDeepCollapsed(next);
    try {
      localStorage.setItem(`${DEEP_COLLAPSED_KEY}:${project.id}`, JSON.stringify([...next]));
    } catch { /* приватный режим — обойдёмся без запоминания */ }
  };

  // Глубокое сворачивание раздела: прячет всё его поддерево. Виден только заголовок самого
  // раздела, вложенные подпапки (заголовки и документы) уходят целиком — рендер не рисует
  // блоки под свёрнутым предком (см. isUnderDeepCollapsed)
  const collapseSubtree = (folder: string) => {
    const next = new Set(deepCollapsed);
    if (!next.delete(folder)) next.add(folder);
    saveDeepCollapsed(next);
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

  // Перестановка строк внутри группы — правка .order в репозитории. В отличие от
  // закреплённых (те живут в localStorage), это изменение рабочего дерева, и оно
  // попадёт в чей-то коммит — поэтому только по явному жесту, никогда фоном.
  //
  // Порядок применяется оптимистично: ответ сервера всё равно придёт свежим индексом,
  // но без локальной перестановки строка на кадр отскакивала бы назад
  const moveInFolder = (folder: string, docs: DocEntry[], from: string, to: string) => {
    const a = docs.findIndex(d => d.path === from);
    const b = docs.findIndex(d => d.path === to);
    if (a < 0 || b < 0 || a === b) return;
    const next = arrayMove(docs, a, b);
    setIndex(prev => prev
      ? reorderInPlace(prev, docs.map(d => d.path), next.map(d => d.path))
      : prev);
    // Не-markdown в .order не пишется: его место задаёт индекс, а не файл
    api.docs.setOrder(project.id, folder, next.filter(d => isMarkdown(d.path)).map(d => orderName(d.path)))
      .then(setIndex)
      // Порядок мог разойтись с диском (папку правили в git) — возвращаем то, что на нём
      .catch(() => { setError('Не удалось сохранить порядок страниц'); loadIndex(); });
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

  // Переход к разделу в дереве — жест вида «Разделы»: список разделов отвечает
  // «куда идти», дерево — «что там лежит», и одно должно уметь передать другому.
  // Вид переключаем здесь же: прокручивать невидимый список бессмысленно
  const jumpToFolder = (folder: string) => {
    setActiveFolder(folder);
    // Прыжок в свёрнутую папку показывал бы одну подпись — разворачиваем её
    const wasCollapsed = collapsed.has(folder);
    if (wasCollapsed) toggleFolder(folder);
    // Прокрутка — только после перерисовки: из «Разделов» дерева на экране ещё нет
    // (folderRefs пусты), а у только что развёрнутой папки геометрия едет анимацией.
    // Оба случая ждут одного — чтобы список принял свой окончательный вид
    const wasHidden = shownView !== 'list';
    if (wasHidden) setView('list');
    if (!wasCollapsed && !wasHidden) { scrollToFolder(folder); return; }
    if (settleTimer.current) window.clearTimeout(settleTimer.current);
    settleTimer.current = window.setTimeout(() => scrollToFolder(folder), EXPAND_MS + 20);
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
  // Зона списка: тянут её от ФАКТИЧЕСКОЙ высоты, а не от запомненной. В низкой панели
  // список ужат сжатием, и старт от запомненного значения давал бы скачок вниз
  const treeRef = useRef<HTMLDivElement>(null);
  const headings = useHeadings(contentRef, doc?.content);

  // Пути документов нижним регистром — по ним отличаем переход внутри панели
  // от открытия файла кода в центре
  const knownDocs = useMemo(
    () => new Set((index ?? []).map(d => d.path.toLowerCase())),
    [index]);

  // Страницы разделов: папка → документ, который её открывает («docs/decisions» →
  // «docs/decisions.md»). Пара приходит с бэкенда — там известен точный состав области
  const sectionPages = useMemo(() => {
    const m = new Map<string, DocEntry>();
    for (const d of index ?? []) if (d.sectionFolder) m.set(d.sectionFolder, d);
    return m;
  }, [index]);

  // Вид «Разделы»: только страницы разделов, в порядке индекса (то есть в порядке .order
  // своих папок). Вложенность показываем отступом, а не деревом с раскрытием: раздел
  // здесь — точка перехода, и сворачивать в нём нечего.
  //
  // Счётчик — все документы внутри папки раздела, включая подразделы: в этом виде
  // раздел отвечает за всё своё поддерево, а не за один свой уровень
  const sections = useMemo(() => {
    const all = (index ?? []).filter(d => d.sectionFolder);
    const folders = all.map(d => d.sectionFolder!);
    return all.map(doc => {
      const folder = doc.sectionFolder!;
      const prefix = `${folder.toLowerCase()}/`;
      return {
        doc,
        folder,
        depth: folders.filter(f => folder.toLowerCase().startsWith(`${f.toLowerCase()}/`)).length,
        count: (index ?? []).filter(d => d.path.toLowerCase().startsWith(prefix)).length,
      };
    });
  }, [index]);

  // Блоки списка: одна папка — один блок. Сначала документы, лежащие прямо в папке, следом
  // её разделы со своими дочерними — в том порядке, в каком разделы стоят в .order.
  // Порядок НЕ пересортировывается: индекс приходит упорядоченным (бэкенд читает .order
  // каждой папки), и localeCompare здесь затирал бы его.
  //
  // Страница раздела строкой не показывается: она и есть подпись своего блока
  // («Расширения» вместо «docs · extensions») и открывается кликом по ней.
  const blocks = useMemo<DocBlock[]>(() => {
    const docsOf = new Map<string, DocEntry[]>();     // папка → её собственные документы
    const subsOf = new Map<string, string[]>();       // папка → подпапки в порядке появления

    // Регистрируем ВСЮ цепочку папок до корня: папка-посредник может не иметь своих
    // документов («spikes/» с одной подпапкой внутри), и без цепочки обход до её
    // содержимого просто не дошёл бы
    const chain = (folder: string) => {
      let f = folder;
      while (f) {
        const parent = folderOf(f);
        const list = subsOf.get(parent) ?? [];
        if (list.includes(f)) break;    // выше по цепочке уже регистрировали
        list.push(f);
        subsOf.set(parent, list);
        f = parent;
      }
    };
    const own = (folder: string) => {
      const list = docsOf.get(folder) ?? [];
      docsOf.set(folder, list);
      return list;
    };

    for (const d of index ?? []) {
      const parent = folderOf(d.path);
      // Страница раздела задаёт МЕСТО раздела среди соседей — по своей строке в .order,
      // а не по первому дочернему документу: иначе пустой раздел не появился бы вовсе
      if (d.sectionFolder) { own(d.sectionFolder); chain(d.sectionFolder); continue; }
      own(parent).push(d);
      // Папка без парной страницы всё равно должна встать в список — местом ей служит
      // первый её документ
      chain(parent);
    }

    // Обход сверху вниз: сначала документы самой папки, следом её разделы со своими
    // дочерними. Так «Бизнес-описание» остаётся в docs, а не уезжает под раздел, стоящий
    // выше него в .order
    const out: DocBlock[] = [];
    const walk = (folder: string) => {
      if (docsOf.has(folder)) out.push({ key: folder, folder, docs: docsOf.get(folder)! });
      for (const child of subsOf.get(folder) ?? []) walk(child);
    };
    walk('');
    return out;
  }, [index]);

  // Папки, у которых есть вложенные подпапки-блоки: только им нужен двойной шеврон
  // «свернуть поддерево» — у листового раздела прятать сверх своих документов нечего
  const foldersWithSubtree = useMemo(() => {
    const s = new Set<string>();
    for (const b of blocks) {
      const parent = b.folder ? folderOf(b.folder) : '';
      if (parent) s.add(parent);
    }
    return s;
  }, [blocks]);

  // Блок под глубоко свёрнутым предком: его не рисуем вовсе — так поддерево исчезает
  // целиком, а не просто пустеет. Поднимаемся по цепочке папок до корня
  const isUnderDeepCollapsed = useCallback((folder: string) => {
    let p = folderOf(folder);
    while (p) {
      if (deepCollapsed.has(p)) return true;
      p = folderOf(p);
    }
    return false;
  }, [deepCollapsed]);

  // Подпись группы: у раздела — заголовок его страницы, у прочих папок — путь как раньше.
  // Родитель отдаётся отдельным полем и нужен только там, где папку выбирают вслепую
  // (диалог создания): в разделителе списка место группы и так видно по дереву
  const groupTitle = useCallback((folder: string): { title: string; subtitle?: string } => {
    const page = sectionPages.get(folder);
    if (!page) return { title: groupLabel(folder) };
    const parent = folderOf(folder);
    const parentPage = parent ? sectionPages.get(parent) : undefined;
    return { title: page.title, subtitle: parentPage ? stripLeadingEmoji(parentPage.title) : parent || undefined };
  }, [sectionPages]);

  // Документы папки. Блоки одной папки складываются в одну строку: в выборе папки ждут
  // папку, а не её куски. Нужны и списку целей создания, и его счётчикам
  const folderCountMap = useMemo(() => {
    const m = new Map<string, number>();
    for (const b of blocks) if (b.folder) m.set(b.folder, (m.get(b.folder) ?? 0) + b.docs.length);
    return m;
  }, [blocks]);
  const folderCounts = useMemo(() => [...folderCountMap.entries()], [folderCountMap]);
  // Файлы корня области (README.md и соседи) — счётчик для строки «Корень репозитория»
  const rootFileCount = useMemo(
    () => (index ?? []).filter(d => !d.path.includes('/')).length,
    [index]);

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
    // Служебные файлы документации: сами документами не являются, но меняют порядок
    // страниц и состав области. Без них правка .order в git не перечитывала бы индекс —
    // и порядок в панели оставался бы прежним, хотя на диске он уже другой
    if (lower === '.docs' || lower.endsWith('/.order') || lower === '.order') return true;
    if (scopeInfo) {
      const { folders, rootFiles, types, excludeFolders } = scopeInfo.selected;
      // Файл корня — только поимённо: там же лежит код, и расширение ни о чём не говорит
      if (!p.includes('/')) return rootFiles.some(f => f.toLowerCase() === lower);
      // Внутри исключённой подпапки документа нет — как и в невыбранной папке
      const inExcluded = (excludeFolders ?? []).some(e =>
        lower.startsWith(`${e.toLowerCase()}/`) || lower === e.toLowerCase());
      if (inExcluded) return false;
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

  // Свойства документа: тип берётся из схемы .docs, приехавшей вместе с настройкой области
  const docTypes = scopeInfo?.docTypes ?? null;
  const docType = typeOf(docTypes, doc);
  // Ключ главного свойства — общей функцией, а не своим выбором: иначе точка в дереве
  // и плашка в шапке разошлись бы на типе без явного badgeProperty
  const badgeKey = badgeKeyOf(docType);
  const badgeDef = docType?.properties.find(
    p => p.key.toLowerCase() === (badgeKey ?? '').toLowerCase()) ?? null;
  const badge = badgeOf(docTypes, doc);

  // Значение не подменяем оптимистично: правка уходит в файл репозитория, и откат
  // соврал бы про то, что лежит на диске. Пока летит запрос — контрол приглушён
  const saveProp = useCallback((path: string, key: string, value: string | null) => {
    setSavingProp(key);
    setPropsError(null);
    api.docs.setProperty(project.id, path, key, value)
      .then(res => {
        setIndex(res.index);
        setDoc(d => (d && d.path === path ? { ...d, properties: res.properties } : d));
      })
      .catch(() => setPropsError(`Не удалось сохранить «${key}»`))
      .finally(() => setSavingProp(null));
  }, [project.id]);

  // Ошибка правки принадлежит документу: без сброса она переезжала бы на следующий,
  // как будто это с ним что-то не так
  useEffect(() => { setPropsError(null); }, [selected]);

  // Метка типа для строки дерева: цвет и подсказка готовыми строками — DocRow остаётся
  // тупым, как с count и pinned
  const statusOf = useCallback((d: DocEntry) => {
    const b = badgeOf(docTypes, d);
    return b ? { statusColor: propDotColor(b.color), statusTitle: `${b.key}: ${b.label}` } : {};
  }, [docTypes]);

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
    // Не проскроллили (узлы оторваны — markdown перерисовывается) — якорь не гасим,
    // сработает на следующем пересборе оглавления
    if (!scrollToHeading(contentRef.current, target)) return;
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

  const setView = (next: DocsView) => {
    setViewState(next);
    try { localStorage.setItem(VIEW_KEY, next); } catch { /* квота */ }
  };

  // Содержимое README грузится отдельно от выбранного документа: домашний режим не
  // должен сбивать то, что читали в превью, — закрыл домик и вернулся ровно туда же
  useEffect(() => {
    if (view !== 'home' || !homePath) return;
    let alive = true;
    api.docs.doc(project.id, homePath)
      .then(d => { if (alive) setHomeDoc(d); })
      .catch(() => { if (alive) setHomeDoc(null); });
    return () => { alive = false; };
  }, [project.id, view, homePath, index]);

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
      setView('home');
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

  // Поповер «Список папок» убран: переходами по корпусу теперь занимается вид
  // «Разделы» — тот же список, только на всю панель, с постоянной подписью и
  // числом документов. Держать рядом два способа попасть в раздел незачем.

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
  const homeView = view === 'home' && homePath != null;
  // Вид переключателя: без начального документа «Начало» из него выпадает, и сохранённый
  // выбор надо отобразить на то, что панель показывает НА САМОМ ДЕЛЕ, — иначе сегмент
  // подсвечивал бы вид, которого на экране нет
  const shownView: DocsView = view === 'home' && !homeView ? 'list' : view;
  const sectionsView = shownView === 'sections';
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

  // Редактор типов правит секцию того же файла .docs, поэтому ему нужна уже загруженная
  // настройка области: из неё он берёт и кандидатов в папки, и источник хранения
  const typesDialog = typesOpen && scopeInfo && (
    <DocsTypesDialog
      projectId={project.id}
      info={scopeInfo}
      onClose={() => setTypesOpen(false)}
      onSaved={info => { setScopeInfo(info); loadIndex(); }}
    />
  );

  // Куда создавать: папка, в которой пользователь сейчас находится, иначе первая папка
  // области. Корень репозитория не годится — документ там попадёт в область, только если
  // его имя стоит в «файлах корня», и созданный файл просто не появился бы в списке
  // '' — корень репозитория: там живут файлы корня области (README.md, docs.md), и
  // создавать рядом с ними законно. Выбор корня — осознанный (ROOT_TARGET), а не
  // «ничего не выбрано», поэтому он отдельным значением, а не пустой строкой
  const createFolder = createInFolder === ROOT_TARGET ? ''
    : createInFolder
    || (activeFolder && activeFolder !== PINNED_GROUP ? activeFolder : '')
    || scopeInfo?.selected.folders[0]
    || blocks.find(b => b.folder)?.folder
    || '';

  // Куда можно создавать: папки области (в том числе пока пустые — их в индексе нет) и
  // все папки дерева, включая только что созданные разделы. Порядок как в списке папок:
  // сперва настроенные корни, дальше остальное в порядке обхода
  const createTargets = useMemo(() => {
    const seen = new Set<string>();
    const out: string[] = [];
    for (const f of [...(scopeInfo?.selected.folders ?? []), ...folderCounts.map(([f]) => f)])
      if (f && !seen.has(f)) { seen.add(f); out.push(f); }
    return out;
  }, [scopeInfo, folderCounts]);

  // Пустая папка — это корень репозитория, законная цель: проверять здесь нечего
  const createDialog = createKind && (
    <DocsCreateDialog
      projectId={project.id}
      folder={createFolder}
      kind={createKind}
      onClose={() => setCreateKind(null)}
      onCreated={path => {
        setCreateKind(null);
        loadIndex();
        // Созданный документ идут наполнять — открываем его в центре, как README
        // из пустого состояния. Вид при этом уступает место списку — но только
        // домашний: созданный раздел виден и в «Разделах», уводить оттуда незачем
        if (view === 'home') setView('list');
        onOpenFile(path);
      }}
    />
  );

  // Контролы панели: штатное место для них — шапка карточки (PanelHeaderSlot),
  // а не собственный ряд под ней. Раньше ряд занимал целую полосу высоты в узкой
  // колонке ради иконок, которые прекрасно живут рядом с заголовком.
  // Что показывает панель: начальный документ, разделы корпуса или всё дерево.
  // IconSegmented — тот же примитив и размер, что у видов в «Задачах».
  // Порядок от общего к частному: «Начало» → «Разделы» → «Документы».
  // Без начального документа его сегмент выпадает — переключать было бы не на что,
  // а сам переключатель остаётся: разделы и документы есть и без README
  const viewSwitch = (
    <IconSegmented<DocsView>
      value={shownView}
      options={[
        ...(homePath
          ? [{ value: 'home' as const, label: 'Начало', icon: <Home size={14} strokeWidth={ICON_STROKE} /> }]
          : []),
        { value: 'sections', label: 'Разделы', icon: <FolderTree size={14} strokeWidth={ICON_STROKE} /> },
        { value: 'list', label: 'Документы', icon: <BookText size={14} strokeWidth={ICON_STROKE} /> },
      ]}
      onChange={setView}
    />
  );

  const controls = (
    <>
      {/* Без шапки переключатель идёт первым в общем ряду — своего места слева там нет */}
      {!hasPanelHeader && viewSwitch}
      {/* «Развернуть в центре» в ряду контролов больше нет: кнопка относится к тексту
          под ней, а не к панели, и живёт теперь в правом верхнем углу самой области
          чтения — там же, где её ищут в превью */}
      {/* Поиск переехал в меню настроек: ряд в шапке узкий, а поиск по документам
          нужен изредка — держать под него постоянную кнопку дороже, чем один лишний
          клик. Открытая строка поиска закрывается крестиком и Esc.
          Кнопки «Список папок» здесь больше нет — её работу делает вид «Разделы» */}
      {/* Настройки — во ВСЕХ видах, но не одинаковые: меню собирается из того, чем
          в этом виде реально есть что настраивать. В «Начале» из трёх пунктов остаётся
          один (область документации: превью и поиск управляют списком, которого тут
          нет) — поэтому там кнопка сразу открывает диалог области и носит его иконку,
          а не прячет единственное действие за лишним кликом по попапу */}
      {homeView ? (
        <IconButton title="Папки документации" onClick={() => setScopeOpen(true)} size="sm">
          <FolderCog size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      ) : (
        <IconButton
          title="Настройки панели"
          active={!!settingsAnchor}
          onClick={e => {
            const rect = e.currentTarget.getBoundingClientRect();
            setSettingsAnchor(a => (a ? null : rect));
          }}
          size="sm"
        >
          <SlidersHorizontal size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      )}
    </>
  );

  // Главное действие панели — в ЗАКРЕПЛЁННОМ слоте шапки, как «Новый» в «Файлах»:
  // оно видно всегда, а не только под курсором. Жест тот же самый (кнопка → меню
  // видов → модалка с именем), и расходиться этим двум панелям нельзя.
  // Вид на кнопку не влияет: «создать документ или раздел» — действие над корпусом,
  // а не над тем, что панель сейчас показывает
  const createControl = (
    <Button
      size="xs"
      variant="primary"
      leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      // Прямоугольник снимаем СРАЗУ: внутри функционального апдейта React уже
      // обнулил currentTarget, и обращение к нему роняло панель
      onClick={e => {
        const rect = e.currentTarget.getBoundingClientRect();
        setCreateMenu(m => (m ? null : rect));
      }}
    >
      Новый
    </Button>
  );

  // Меню видов создания. Куда попадёт созданное — подписью в подвале, как в «Файлах»:
  // вопрос возникает ровно в момент создания. Целевая папка — та, в которой пользователь
  // сейчас находится (последний открытый документ или группа)
  // Правый клик по строке или подписи раздела: меню действий по курсору. Меню общее
  // (Menu в anchor-режиме), поэтому точку заворачиваем в вырожденный прямоугольник
  const openRowMenu = (doc: DocEntry, e: React.MouseEvent) => {
    e.preventDefault();
    setRowMenu({ doc, rect: new DOMRect(e.clientX, e.clientY, 0, 0) });
  };
  // То же меню на тач-раскладке: правого клика там нет, зато есть удержание строки.
  // Якорем служит точка касания — меню встаёт у пальца, как у курсора на мыши
  const openRowMenuAt = (doc: DocEntry, point: LongPressPoint) =>
    setRowMenu({ doc, rect: new DOMRect(point.x, point.y, 0, 0) });

  // Переименование прошло: индекс приезжает с ответом, а по карте переезда чиним всё,
  // что помнит СТАРЫЕ пути, — закреплённые, открытый документ и файл в центре. Иначе
  // строка молча пропадала бы из закреплённых, а превью показывало исчезнувший путь
  const applyRename = (res: {
    path: string; moved: Record<string, string>;
    updatedDocs: number; brokenLinks: number; index: DocEntry[];
  }, verb = 'Переименовано') => {
    const moved = res.moved ?? {};
    setRenaming(null);
    setIndex(res.index ?? null);
    loadIndex();

    const to = (p: string | null | undefined) => (p && moved[p]) || p;
    if (Object.keys(moved).length > 0) {
      savePinned(pinnedOrder.map(p => moved[p] ?? p));
      const nextSelected = to(selected);
      if (nextSelected && nextSelected !== selected) setSelected(nextSelected);
      const nextActive = to(activeFilePath);
      if (nextActive && nextActive !== activeFilePath) onOpenFile(nextActive);
    }
    // Что вышло по ссылкам — единственный способ узнать про оставшиеся битые: чинится
    // только область документации, а ссылки из кода механизму не видны
    setRenameNote(res.brokenLinks > 0
      ? `${verb}. Ссылок обновлено в ${res.updatedDocs} документах, осталось битых: ${res.brokenLinks}`
      : res.updatedDocs > 0
        ? `${verb}. Ссылки обновлены в ${res.updatedDocs} документах`
        : verb);
  };

  // Удаление: раздел уходит парой со всем содержимым, поэтому после ответа чистим всё,
  // что помнит исчезнувшие пути, — закреплённые, превью и файл в центре
  const applyDelete = async (doc: DocEntry) => {
    try {
      const res = await api.docs.remove(project.id, doc.path);
      const gone = new Set(res.removed ?? [doc.path]);
      setDeleting(null);
      setIndex(res.index ?? null);
      loadIndex();
      savePinned(pinnedOrder.filter(p => !gone.has(p)));
      if (selected && gone.has(selected)) closeDoc();
      if (activeFilePath && gone.has(activeFilePath)) onCloseFile?.();
      // Битые ссылки чинить нечем — цели больше нет; узнать о них надо здесь, а не
      // при публикации wiki
      const docs = res.removed?.length ?? 1;
      setRenameNote(
        `Удалено: ${docs} ${docCountWord(docs)}`
        + (res.removedFiles > 0 ? `, вместе с ними файлов вне области: ${res.removedFiles}` : '')
        + (res.brokenLinks > 0 ? `. Ссылок на удалённое осталось: ${res.brokenLinks}` : ''));
    } catch {
      setDeleting(null);
      setError('Не удалось удалить документ');
    }
  };

  // Куда упадёт строка в виде «Разделы»: тянешь ровно вверх-вниз — меняется порядок
  // среди соседей, уводишь ВПРАВО — раздел вкладывается в тот, над которым курсор.
  //
  // По горизонтали, а не по «середине строки»: при сортировке dnd-kit сам сдвигает
  // соседей по вертикали, и расчёт «центр цели» плавал вместе с ними — жест угадывался
  // через раз. Горизонталь сортировкой не занята, а сдвиг вправо и так читается как
  // «сделать дочерним» — тем же движением задают вложенность в аутлайнерах
  const NEST_SHIFT = 24;
  const dropIntent = (e: { over: unknown; delta: { x: number } }) =>
    !e.over ? null : e.delta.x > NEST_SHIFT ? 'nest' : 'order';

  // Раздел, внутрь которого сейчас упадёт перетаскиваемая строка (подсветка рамкой)
  const [nestTarget, setNestTarget] = useState<string | null>(null);

  const closeCreateMenu = () => { setCreateMenu(null); setPickingFolder(false); };

  const createMenuEl = createMenu && (
    <Menu anchor={createMenu} minWidth={pickingFolder ? 200 : 240} maxHeight={pickingFolder ? 260 : 320} onClose={closeCreateMenu}>
      {pickingFolder ? (
        // Выбор папки — плотным списком (строка папки, как в прежнем списке папок), а не
        // рядами меню: папок бывает под десяток, и пункты в полный рост меню превращали
        // выбор в длинную простыню. Выбранная отмечена той же заливкой, что текущая
        // строка списка, — галочки в узкой строке не нужно
        <div style={{ padding: '2px 4px' }}>
          {/* Корень репозитория — первым: там живут файлы корня области (README.md и
              соседи), и создавать рядом с ними законно. Имя нового файла продукт сам
              допишет в «файлы корня» — папкой корень не выбирают */}
          <FolderRow
            label="Корень репозитория"
            count={rootFileCount}
            current={createFolder === '' && createInFolder === ROOT_TARGET}
            onJump={() => { setCreateInFolder(ROOT_TARGET); setPickingFolder(false); }}
          />
          {/* Дальше — папки области и все папки дерева, включая только что созданные
              разделы. Названы своими заголовками, а не путями, — как в списке папок */}
          {createTargets.map(folder => {
            const { title, subtitle } = groupTitle(folder);
            return (
              <FolderRow
                  key={folder}
                label={title}
                parent={subtitle}
                count={folderCountMap.get(folder) ?? 0}
                current={folder === createFolder}
                onJump={() => { setCreateInFolder(folder); setPickingFolder(false); }}
              />
            );
          })}
        </div>
      ) : (
        <>
          <MenuItem
            icon={<BookText size={15} strokeWidth={ICON_STROKE} />}
            label="Документ"
            onClick={() => { closeCreateMenu(); setCreateKind('doc'); }}
          />
          {/* Раздел в корне не создаётся: это была бы новая папка документации, то есть
              правка области, а не создание страницы */}
          <MenuItem
            icon={<FolderTree size={15} strokeWidth={ICON_STROKE} />}
            label="Раздел"
            disabled={!createFolder}
            onClick={() => { closeCreateMenu(); setCreateKind('section'); }}
          />
          {/* Куда попадёт созданное — подписью в подвале, как в «Файлах»: вопрос
              возникает ровно в момент создания. По умолчанию это папка, в которой
              пользователь находится, а «сменить» открывает список папок */}
          <div style={{
            borderTop: `1px solid ${C.border}`, margin: '4px 0 0', padding: '6px 10px 2px',
            display: 'flex', alignItems: 'center', gap: 6,
          }}>
            <span style={{
              display: 'flex', alignItems: 'center', gap: 4, minWidth: 0, flex: 1,
              fontSize: 11, color: C.textMuted, fontFamily: FONT.mono,
            }}>
              {createFolder
                ? <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                : <Home size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              {/* Путь режем СЛЕВА: у вложенного раздела важен хвост («…/decisions/»),
                  а не общее для всех начало */}
              <span title={createFolder || 'корень репозитория'} style={{
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                direction: 'rtl', textAlign: 'left',
              }}>
                {createFolder ? `${createFolder}/` : 'корень репозитория'}
              </span>
            </span>
            {(createTargets.length > 0 || createFolder) && (
              <button
                onClick={() => setPickingFolder(true)}
                title="Выбрать другую папку"
                style={{
                  flexShrink: 0, border: 'none', background: 'none', cursor: 'pointer',
                  padding: '2px 4px', fontSize: 11, color: C.accent, fontFamily: FONT.sans,
                }}
              >
                сменить
              </button>
            )}
          </div>
        </>
      )}
    </Menu>
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
      {hasDocs && hasPanelHeader && (
        <PanelHeaderSlot side="left">{viewSwitch}</PanelHeaderSlot>
      )}
      {/* Главное действие — в закреплённой зоне: видно всегда, как «Новый» в «Файлах» */}
      {hasDocs && hasPanelHeader && (
        <PanelHeaderSlot pinned>{createControl}</PanelHeaderSlot>
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
      {/* Меню настроек панели: тумблер превью (галка справа — как у группировки
          в списке чатов) и вход в диалог области документации */}
      {settingsAnchor && (
        <Menu anchor={settingsAnchor} minWidth={230} maxHeight={190} onClose={() => setSettingsAnchor(null)}>
          <MenuItem
            icon={<PanelBottom size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => { setPreview(!previewEnabled); setSettingsAnchor(null); }}
            label={
              <span style={{
                display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                flex: 1, gap: SP.sm,
              }}>
                Превью снизу
                {previewEnabled && <Check size={ICON_SIZE.xs} strokeWidth={2.4} style={{ color: C.accent, flexShrink: 0 }} />}
              </span>
            }
          />
          {/* Поиск по документам: строка разворачивается над списком. Постоянной кнопки
              в шапке у него нет — ряд там узкий, а ищут в документации изредка */}
          <MenuItem
            icon={<Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            label={searchOpen ? 'Закрыть поиск' : 'Поиск по документам'}
            onClick={() => {
              // Из «Начала» поиску показывать результаты негде — README занимает панель
              // целиком; поэтому вместе с полем открываем и список
              if (searchOpen) closeSearch();
              else { if (homeView) setView('list'); setSearchOpen(true); }
              setSettingsAnchor(null);
            }}
          />
          {/* Область документации: дефолт docs/, но соглашение о папке в проектах разное */}
          <MenuItem
            icon={<FolderCog size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            label="Папки документации"
            onClick={() => { setScopeOpen(true); setSettingsAnchor(null); }}
          />
          {/* Типы документов: какие свойства есть у документов папки — статус решения,
              дата, ответственные */}
          <MenuItem
            icon={<Tags size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            label="Типы документов"
            onClick={() => { setTypesOpen(true); setSettingsAnchor(null); }}
          />
        </Menu>
      )}

      {/* Меню видов создания — порталом по якорю кнопки «Новый» */}
      {createMenuEl}

      {/* Действия строки по правому клику. Пока пункт один — переименование; удаление
          придёт сюда же, когда появится */}
      {rowMenu && (
        <Menu anchor={rowMenu.rect} minWidth={210} maxHeight={140} onClose={() => setRowMenu(null)}>
          <MenuItem
            icon={<PenLine size={15} strokeWidth={ICON_STROKE} />}
            label={rowMenu.doc.sectionFolder ? 'Переименовать раздел' : 'Переименовать'}
            onClick={() => { setRenaming(rowMenu.doc); setRowMenu(null); }}
          />
          <MenuSep />
          <MenuItem
            icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />}
            label={rowMenu.doc.sectionFolder ? 'Удалить раздел' : 'Удалить'}
            danger
            onClick={() => { setDeleting(rowMenu.doc); setRowMenu(null); }}
          />
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
        <div style={{ position: 'relative', flex: '1 1 auto', minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          {/* Развернуть в центре — поверх текста, в правом верхнем углу области чтения.
              Кнопка относится к документу под ней, а не к панели, поэтому и стоит на нём;
              подложка непрозрачная — под кнопкой едет прокручиваемый текст */}
          {homePath && (
            <div style={{
              position: 'absolute', top: SP.xs, right: SP.sm, zIndex: 1,
              background: C.bgWhite, borderRadius: R.md,
            }}>
              <IconButton title="Развернуть в центре" onClick={() => onOpenFile(homePath)} size="sm">
                <Maximize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            </div>
          )}
          <div style={{ flex: '1 1 auto', minHeight: 0, overflowY: 'auto', padding: `${SP.md}px ${SP.md}px ${SP.xl}px` }}>
            {!homeDoc && <div style={emptyStyle}>Загружаем…</div>}
            {homeDoc && (
              <MarkdownViewer
                content={homeDoc.content}
                // Переходы по ссылкам ведут из README в остальную документацию, поэтому
                // клик закрывает домашний режим и открывает документ обычным путём
                onDocLink={href => { setView('list'); handleHomeLink(href); }}
                resolveImageSrc={src => {
                  const target = resolveDocImage(homePath, src);
                  return target ? api.files.fileUrl(project.id, target) : undefined;
                }}
              />
            )}
          </div>
        </div>
      ) : searching ? (
        <div style={{ flex: '1 1 auto', minHeight: 0, overflowY: 'auto', padding: `${SP.xs}px 0` }}>
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
              с включённой — высоту, заданную хендлом ресайза. Высота идёт basis'ом
              со сжатием, а не height: в панели ниже запомненной высоты список
              отдаёт лишнее, вместо того чтобы вытеснить превью за край */}
          <div
            ref={treeRef}
            style={previewEnabled
              ? { flexGrow: 0, flexShrink: 1, flexBasis: treeH, display: 'flex', flexDirection: 'column', minHeight: TREE_SQUEEZE_H }
              // Расписано по осям, а не сокращением flex: у одного и того же узла
              // React не даёт смешивать shorthand и отдельные свойства между рендерами
              // и на каждом переключении режима ругается в консоль.
              //
              // Базис ИМЕННО auto, а не 0. Одиночная панель у центра стоит по контенту
              // (fill=false в PanelShell — см. panelStretched в PanelZone), то есть высота
              // родителя тут не задана. Нулевой базис в таком контейнере означает высоту 0:
              // растягивать flexGrow нечего, свободного места нет — и от панели оставалась
              // одна шапка. С auto список занимает свою настоящую высоту, а когда она
              // больше окна, панель упирается в maxHeight:100% и сжимается до скролла
              : { flexGrow: 1, flexShrink: 1, flexBasis: 'auto', display: 'flex', flexDirection: 'column', minHeight: 0 }
            }
          >
            {/* Итог переименования: сколько ссылок починено и сколько осталось битыми.
                Строкой над списком, а не тостом, — цифру про битые ссылки надо успеть
                прочитать, и закрывает её сам пользователь */}
            {renameNote && (
              <div style={{
                flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.xs,
                margin: `${SP.xs}px ${SP.xs}px 0`, padding: `${SP.xs}px ${SP.sm}px`,
                borderRadius: R.md, background: C.bgInset,
                fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary,
              }}>
                <span style={{ flex: 1, minWidth: 0 }}>{renameNote}</span>
                <IconButton title="Скрыть" onClick={() => setRenameNote(null)} size="sm">
                  <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
              </div>
            )}

            {/* Базис auto по той же причине, что у контейнера выше: в панели «по контенту»
                нулевой базис схлопнул бы список в ноль */}
            <div ref={listRef} style={{ flex: '1 1 auto', minHeight: 0, overflowY: 'auto', padding: `${SP.xs}px ${SP.xs}px` }}>
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
                {/* Вид «Разделы»: плоский список страниц разделов. Строка та же, что
                    у документа (клик, двойной клик, булавка, действия правым кликом), —
                    раздел здесь и есть документ, просто с папкой за спиной. Своей
                    перестановки нет: порядок разделов задаётся .order их папок, а тащить
                    строку тут значило бы менять его вслепую через дерево */}
                {sectionsView && sections.length === 0 && index?.length !== 0 && (
                  <EmptyState
                    compact
                    icon={<FolderTree size={20} strokeWidth={ICON_STROKE} />}
                    title="Разделов пока нет"
                    subtitle="Раздел — это страница с папкой: она открывает подкорпус документов"
                    action={
                      <Button
                        variant="primary"
                        size="sm"
                        disabled={!createFolder}
                        onClick={() => setCreateKind('section')}
                      >
                        Создать раздел
                      </Button>
                    }
                  />
                )}
                {/* Перетаскивание разделов: у краёв строки — порядок среди соседей
                    (.order их общей папки), в середине — вложение раздела внутрь цели,
                    то есть перенос папки со всем содержимым. Второе спрашивает
                    подтверждение, первое применяется сразу */}
                {sectionsView && sections.length > 0 && (
                <DndContext
                  sensors={dragSensors}
                  collisionDetection={closestCenter}
                  // onDragMove, а не onDragOver: тот срабатывает лишь на СМЕНУ цели, а
                  // смысл жеста здесь меняется по ходу движения — вправо уехали уже над
                  // той же строкой. С onDragOver подсветка не появлялась вовсе
                  onDragMove={e => {
                    const overId = e.over ? String(e.over.id) : null;
                    const nest = overId && overId !== String(e.active.id) && dropIntent(e) === 'nest';
                    setNestTarget(nest ? overId : null);
                  }}
                  onDragCancel={() => setNestTarget(null)}
                  onDragEnd={e => {
                    const intent = dropIntent(e);
                    setNestTarget(null);
                    const activeId = String(e.active.id);
                    const overId = e.over ? String(e.over.id) : null;
                    if (!overId || overId === activeId || !intent) return;
                    const from = sections.find(s => s.doc.path === activeId);
                    const to = sections.find(s => s.doc.path === overId);
                    if (!from || !to) return;

                    if (intent === 'nest') {
                      // Уже лежит в этом разделе — переносить некуда
                      if (folderOf(from.doc.path) === to.folder) return;
                      setMoving({ doc: from.doc, target: to.folder });
                      return;
                    }
                    // Порядок — только среди соседей: у разных родителей общего .order нет,
                    // и перестановка между ними означала бы перенос, о котором не спросили
                    const parent = folderOf(from.doc.path);
                    if (folderOf(to.doc.path) !== parent) return;
                    moveInFolder(parent,
                      sections.filter(s => folderOf(s.doc.path) === parent).map(s => s.doc),
                      activeId, overId);
                  }}
                >
                <SortableContext items={sections.map(s => s.doc.path)} strategy={verticalListSortingStrategy}>
                {sections.map(({ doc: d, folder, depth, count }) => (
                  <SortableRow key={d.path} doc={d} disabled={touch}>
                  <DocRow
                    doc={d}
                    dropInto={nestTarget === d.path}
                    selected={isShown(d.path)}
                    home={d.path === homePath}
                    pinned={pinned.has(d.path)}
                    {...statusOf(d)}
                    // Вложенность — отступом: подраздел читается как подраздел, а строк
                    // тут по числу разделов, не по числу документов
                    indent={depth * SP.md}
                    count={count}
                    // Открыть раздел — значит войти в него: создавать дальше надо внутри,
                    // как при клике по подписи раздела в дереве
                    onOpen={() => { handleRowClick(d.path); setActiveFolder(folder); }}
                    onJump={() => jumpToFolder(folder)}
                    onExpand={() => handleRowDoubleClick(d.path)}
                    onTogglePin={() => togglePin(d.path)}
                    onContextMenu={e => openRowMenu(d, e)}
                    pressing={pressingKey === d.path}
                    press={pressProps(d.path, p => openRowMenuAt(d, p))}
                  />
                  </SortableRow>
                ))}
                </SortableContext>
                </DndContext>
                )}
                {/* Один DndContext на всё дерево: бросок В СВОЮ группу переставляет
                    порядок (.order папки), в ЧУЖУЮ — переносит файл на диске. Разные
                    последствия у одного жеста, поэтому перенос спрашивает подтверждение */}
                {!sectionsView && (
                <DndContext
                  sensors={dragSensors}
                  collisionDetection={closestCenter}
                  onDragEnd={e => {
                    const activeId = String(e.active.id);
                    const overId = e.over ? String(e.over.id) : null;
                    if (!overId || overId === activeId) return;
                    const from = folderOf(activeId);
                    const to = folderOf(overId);
                    if (from === to) {
                      const block = blocks.find(b => b.folder === from);
                      if (block) moveInFolder(from, block.docs, activeId, overId);
                      return;
                    }
                    const doc = (index ?? []).find(d => d.path === activeId);
                    if (doc) setMoving({ doc, target: to });
                  }}
                >
                {blocks.map(({ key, folder, docs }) => {
                  // Блок под глубоко свёрнутым предком не рисуем вовсе — так поддерево
                  // исчезает целиком, а не просто пустеет
                  if (folder && isUnderDeepCollapsed(folder)) return null;
                  // Корневая группа без подписи — сворачивать её нечем и незачем.
                  // Глубокое сворачивание тоже прячет свои документы (grid ниже)
                  const isCollapsed = !!folder && (collapsed.has(folder) || deepCollapsed.has(folder));
                  const page = sectionPages.get(folder);
                  const { title } = groupTitle(folder);
                  return (
                  <div
                    key={key}
                    // Мигает вся секция целиком — подпись и её документы, чтобы после
                    // прыжка было видно границы группы, а не только её заголовок.
                    // Своего акцента у подписи при этом НЕТ: он держался ровной заливкой
                    // всё время подсветки и гас позже, чем отмигивали строки, — на глаз
                    // это читалось как рассинхрон. Одна анимация на секцию его снимает
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
                        title={title}
                        collapsed={isCollapsed}
                        hidden={docs.length}
                        onToggle={() => toggleFolder(folder)}
                        // У папки с парной страницей подпись открывает её — как узел
                        // дерева в wiki; сворачивание переезжает на шеврон.
                        // Текущей при этом становится САМА папка раздела, а не её родитель
                        // (страница-то лежит уровнем выше): открыть раздел — значит войти
                        // в него, и создавать дальше надо внутри
                        onOpenPage={page ? () => { handleRowClick(page.path); setActiveFolder(folder); } : undefined}
                        // Правый клик по подписи раздела — те же действия, что у строки
                        // документа: переименование раздела начинается с его страницы
                        onContextMenu={page ? e => openRowMenu(page, e) : undefined}
                        pressing={!!page && pressingKey === page.path}
                        press={page ? pressProps(page.path, p => openRowMenuAt(page, p)) : undefined}
                        pagePath={page?.path}
                        {...(page ? statusOf(page) : {})}
                        pinned={!!page && pinned.has(page.path)}
                        // Строкой страница раздела в дереве не рисуется — выделение
                        // открытого документа достаётся подписи его блока
                        active={!!page && isShown(page.path)}
                        onTogglePin={page ? () => togglePin(page.path) : undefined}
                        subtreeCollapsed={deepCollapsed.has(folder)}
                        // Двойной шеврон — только у раздела с вложенными подпапками
                        onCollapseSubtree={foldersWithSubtree.has(folder) ? () => collapseSubtree(folder) : undefined}
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
                    {/* SortableContext на группу, а контекст перетаскивания — общий на
                        дерево: так строка может уехать и в соседнюю группу, а какой это
                        жест — перестановка или перенос файла — решает onDragEnd выше */}
                      <SortableContext items={docs.map(d => d.path)} strategy={verticalListSortingStrategy}>
                        {docs.map(d => (
                          // В свёрнутой группе тащить нечего — её строки не видны; у
                          // не-markdown порядок задаёт индекс, и .order его не описывает
                          <SortableRow key={d.path} doc={d} disabled={touch || isCollapsed || !isMarkdown(d.path)}>
                            <DocRow
                              doc={d}
                              selected={isShown(d.path)}
                              home={d.path === homePath}
                              pinned={pinned.has(d.path)}
                              {...statusOf(d)}
                              onOpen={() => handleRowClick(d.path)}
                              onExpand={() => handleRowDoubleClick(d.path)}
                              onTogglePin={() => togglePin(d.path)}
                              onContextMenu={e => openRowMenu(d, e)}
                    pressing={pressingKey === d.path}
                    press={pressProps(d.path, p => openRowMenuAt(d, p))}
                            />
                          </SortableRow>
                        ))}
                      </SortableContext>
                    </div>
                    </div>
                  </div>
                  );
                })}
                </DndContext>
                )}
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
                      sensors={dragSensors}
                      collisionDetection={closestCenter}
                      onDragEnd={e => {
                        if (e.over) movePinned(String(e.active.id), String(e.over.id));
                      }}
                    >
                      <SortableContext items={pinnedDocs.map(d => d.path)} strategy={verticalListSortingStrategy}>
                        {pinnedDocs.map(d => (
                          <SortableRow key={d.path} doc={d} disabled={touch}>
                            <DocRow
                              doc={d}
                              selected={isShown(d.path)}
                              home={d.path === homePath}
                              pinned
                              pinColumn
                              {...statusOf(d)}
                              onOpen={() => handleRowClick(d.path)}
                              onExpand={() => handleRowDoubleClick(d.path)}
                              onTogglePin={() => togglePin(d.path)}
                              onContextMenu={e => openRowMenu(d, e)}
                    pressing={pressingKey === d.path}
                    press={pressProps(d.path, p => openRowMenuAt(d, p))}
                            />
                          </SortableRow>
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
                from: treeRef.current?.getBoundingClientRect().height ?? treeH,
                set: setTreeH, key: TREE_H_KEY,
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
          // Не сжимается: дефицит высоты забирает список выше (до TREE_SQUEEZE_H).
          // Раньше превью с flex:1 отдавало списку всё до нуля и пропадало насовсем,
          // а пропорциональное сжатие обеих зон вдобавок ломало запомненную высоту списка
          <div style={{ flexGrow: 1, flexShrink: 0, flexBasis: PREVIEW_MIN_H, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            {doc && (
              <div style={{
                flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', gap: SP.xs,
                padding: `${SP.xs}px ${SP.sm}px`, borderBottom: `1px solid ${C.border}`,
              }}>
                <span style={{
                  fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  // Иначе во флекс-ряду заголовок не сжимается и выталкивает плашку
                  minWidth: 0, flexShrink: 1,
                }}>{doc.title}</span>
                {/* Плашка главного свойства типа: она про документ, поэтому стоит рядом
                    с заголовком, а не среди кнопок панели справа от распорки */}
                {badgeDef && badgeDef.kind === 'choice' && (
                  <DocPropChip
                    def={badgeDef}
                    // значение из файла — по нему меню отмечает текущий пункт;
                    // подпись может отличаться, если у значения задан свой заголовок
                    value={badge?.value ?? ''}
                    label={badge?.label}
                    tone={badge?.tone}
                    saving={savingProp === badgeDef.key}
                    onSave={(key, value) => saveProp(doc.path, key, value)}
                    // Заголовок документа важнее плашки: она сжимается первой и обрезается
                    style={{ maxWidth: 160, minWidth: 0, flexShrink: 1 }}
                  />
                )}
                <div style={{ flex: 1 }} />
                {/* Отказ записи живёт в шапке, а не в ленте свойств: шапка видна всегда,
                    а лента уезжает с текстом. Без него плашка просто вернулась бы к
                    старому значению, и причина осталась бы неизвестной */}
                {propsError && (
                  <span title={propsError} style={{
                    fontSize: FS.xs, color: C.danger, flexShrink: 1, minWidth: 0,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>{propsError}</span>
                )}
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
                        level={h.level}
                        onJump={() => { scrollToHeading(contentRef.current, h); setTocAnchor(null); }}
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
              {/* Документ показывается как есть, со строками свойств в шапке текста:
                  редактор свойств живёт под шапкой открытого файла в центре, а в узком
                  превью от него остаётся только плашка главного значения выше. Резать
                  строки из превью нельзя — тогда шапку документа не видно нигде */}
              {doc && !doc.binary && (
                <MarkdownViewer content={doc.content} onDocLink={handleDocLink} resolveImageSrc={resolveImage} />
              )}
            </div>

            {/* Обратные ссылки: кто в документации ведёт на этот документ */}
            {doc && doc.backlinks.length > 0 && (
              <div style={{ flexShrink: 0, borderTop: `1px solid ${C.border}` }}>
                <button onClick={() => setBacklinksOpen(v => !v)} style={docsSectionHeadStyle}>
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
      {typesDialog}
      {createDialog}
      {/* Удаление — общий ConfirmDialog продукта. У раздела в подзаголовке считаем, что
          именно уйдёт: одна строка списка стоит целой ветки на диске, и узнать об этом
          надо до нажатия, а не из git diff */}
      {deleting && (
        <ConfirmDialog
          title={deleting.sectionFolder ? 'Удалить раздел?' : 'Удалить документ?'}
          subtitle={
            <span>
              <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{deleting.path}</span>
              {deleting.sectionFolder && (() => {
                const inside = (index ?? []).filter(d =>
                  d.path.toLowerCase().startsWith(`${deleting.sectionFolder!.toLowerCase()}/`)).length;
                return (
                  <span style={{ display: 'block', marginTop: SP.xs }}>
                    Папка <span style={{ fontFamily: FONT.mono }}>{deleting.sectionFolder}/</span> удаляется
                    целиком{inside > 0 ? ` — вместе с ней уйдёт ${inside} вложенных ${docCountWord(inside)}` : ''} и всё,
                    что в ней лежит помимо документации.
                  </span>
                );
              })()}
            </span>
          }
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => applyDelete(deleting)}
          onCancel={() => setDeleting(null)}
        />
      )}
      {moving && (
        <DocsMoveDialog
          projectId={project.id}
          doc={moving.doc}
          targetFolder={moving.target}
          targetLabel={groupTitle(moving.target).title}
          subtreeLabel={(() => {
            if (!moving.doc.sectionFolder) return undefined;
            const n = (index ?? []).filter(d =>
              d.path.toLowerCase().startsWith(`${moving.doc.sectionFolder!.toLowerCase()}/`)).length;
            return n > 0 ? `${n} ${docCountWord(n)}` : undefined;
          })()}
          onClose={() => setMoving(null)}
          onMoved={res => { setMoving(null); applyRename(res, 'Перенесено'); }}
        />
      )}
      {renaming && (
        <DocsRenameDialog
          projectId={project.id}
          path={renaming.path}
          title={renaming.title}
          sectionFolder={renaming.sectionFolder}
          onClose={() => setRenaming(null)}
          onRenamed={applyRename}
        />
      )}
    </div>
  );
}

const emptyStyle = {
  padding: `${SP.xl}px ${SP.md}px`, textAlign: 'center' as const,
  fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted,
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
