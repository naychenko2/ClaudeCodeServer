// Витрина дизайн-системы (dev-only). Открывается по роуту #/ui-kit через
// lazy-импорт в App.tsx; в production-бандл не попадает.
//
// Каркас: холст CanvasBackdrop → остров-шапка с переключателем темы →
// секции-витрины (палитра/типографика/кнопки и т.д. добавляются по задачам).
//
// ВСЕ визуальные значения — из токенов design.ts (C, FS, SP, ISLAND) и
// компонентов ui/. Ни одного hex-литерала: lint:design проходит зелёным.

import { useState, useEffect, type CSSProperties, Fragment } from 'react';
import {
  type LucideIcon,
  Palette, Layers, Type, ToggleRight,
  LayoutTemplate, MoreHorizontal, Pencil, Copy, Trash2,
  Ruler, Mail, Search, Smartphone,
  LayoutGrid, Columns2, Settings, X,
  MousePointerClick,
  Plus,
  Download,
  Send,
  Star, Database,
  ClipboardList, FolderTree, GitCompare, ListTodo,
  Bot, Users, SquareTerminal, MonitorPlay, User,
  ChevronRight, Folder,
  Funnel, Check, BookOpen,
  Calendar, Share2, MessageCircle,
} from 'lucide-react';
import { Rows3, Pin, FolderOpen, Bell, List, ListTree } from 'lucide-react';
import { C, FONT, FS, SP, R, SHADOW, ISLAND, MODAL_W, GROUP_COLORS } from '../lib/design';
import { AGENT_COLORS } from '../components/AgentSelector';
import { ChatCard } from '../components/ChatCard';
import { STATUS_CONFIG, STATUS_GLOW, type SessionStatus } from '../components/StatusIndicator';
import { ProviderLimitCard } from '../components/chat/ChatItemView';
import type { Session, ChatItem } from '../types';
import { useThemeMode, setThemeMode, type ThemeMode } from '../lib/themeMode';
import { useIsMobile, MOBILE_MAX, TABLET_MAX } from '../lib/breakpoints';
import { CanvasBackdrop } from '../components/ui/CanvasBackdrop';
import {
  Island, IslandHeader, SegmentedControl, IconSegmented, Toggle, Dot, FileTypeTile, FileStatusBadge, Badge,
  SidebarSection,
  Button, IconButton, Modal, ModalActions, ConfirmDialog,
  Menu, MenuItem, BackButton, WaitingIndicator,
  IslandScaffold, Splitter, SidebarSplitter, IslandSplitter, IslandSidebarSplitter,
  TextField, TextArea, IconField, Field, FieldLabel,
  PanelShell, PanelHeaderSlot, useHasPanelHeader, RailFlyout,
} from '../components/ui';
import { ICON_SIZE, ICON_STROKE, ICON_PROPS } from '../components/ui/icons';
import { Toolbar, ToolbarIconButton } from '../components/Toolbar';
import { ToolbarOverflowMenu, type OverflowItem } from '../components/ToolbarOverflowMenu';
import { EmptyState } from '../components/EmptyState';
import type {
  ButtonVariant, ButtonSize,
  IconButtonSize, IconButtonTone, IconButtonVariant,
  FileStatus, BadgeTone,
} from '../components/ui';

import { ColorsSection } from './ColorsSection';

// Опции переключателя темы: ключи — значения ThemeMode, лейблы на русском.
const THEME_OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'light',  label: 'Светлая'  },
  { value: 'dark',   label: 'Тёмная'   },
  { value: 'system', label: 'Системная' },
];

// Опции демо-сегмент-контрола: живой пример переключения.
const LAYOUT_OPTIONS: { value: string; label: string }[] = [
  { value: 'compact', label: 'Компактно' },
  { value: 'comfort', label: 'Комфорт'   },
  { value: 'wide',    label: 'Широко'    },
];

// Демо-цвета для Dot — только семантические токены C.*, никакого hex.
const DOT_SAMPLES: { color: string; label: string }[] = [
  { color: C.accent,       label: 'accent'       },
  { color: C.success,      label: 'success'      },
  { color: C.warning,      label: 'warning'      },
  { color: C.danger,       label: 'danger'       },
  { color: C.info,         label: 'info'         },
  { color: C.textMuted,    label: 'textMuted'    },
];

// Демо-файлы для FileTypeTile: код, разметка, документ, картинка и незнакомый тип
// (последний показывает фолбэк — первые три знака расширения на нейтральной плитке).
const FILE_TILE_SAMPLES = ['App.tsx', 'Program.cs', 'README.md', 'schema.json', 'shot.png', 'notes.rtf'];

// Состояния файла для FileStatusBadge — коды git, как их отдаёт статус репозитория
const FILE_STATUS_SAMPLES: { status: FileStatus; label: string }[] = [
  { status: 'M', label: 'изменён'       },
  { status: 'A', label: 'новый'         },
  { status: 'D', label: 'удалён'        },
  { status: 'R', label: 'переименован'  },
];

// Тоны плашки — роли дизайн-системы, а не цвета
const BADGE_TONES: BadgeTone[] = ['neutral', 'accent', 'success', 'warning', 'danger', 'info', 'plan'];

// Оглавление витрины: id секции (для якоря) + короткий лейбл в кнопке.
// Порядок соответствует основному flow ниже. При добавлении новой секции —
// добавь её сюда и повесь rootProps={{ id }} на её Island.
const TOC_SECTIONS: { id: string; label: string }[] = [
  { id: 'sec-viewport',   label: 'Замер экрана'      },
  { id: 'sec-toggles',    label: 'Переключатели'     },
  { id: 'sec-overlays',   label: 'Оверлеи'           },
  { id: 'sec-toolbar',    label: 'Тулбар'            },
  { id: 'sec-buttons',    label: 'Кнопки'            },
  { id: 'sec-fields',     label: 'Поля'              },
  { id: 'sec-typography', label: 'Типографика'       },
  { id: 'sec-scales',     label: 'Шкалы'             },
  { id: 'sec-palettes',   label: 'Палитры-данные'    },
  { id: 'sec-islands',    label: 'Острова и холст'   },
  { id: 'sec-colors',     label: 'Цвета'             },
  { id: 'sec-panels',     label: 'Панели'             },
  { id: 'sec-headers',    label: 'Шапки'              },
];

// Высота sticky-элементов над контентом: шапка темы + TOC-бар. Секция
// скроллится под них — scrollMarginTop = этой высоте + небольшой зазор.
const STICKY_OFFSET = 140;

export function UiKitPage() {
  const mode = useThemeMode();
  const isMobile = useIsMobile();

  // Внешний отступ холста: на мобиле компактнее (ISLAND.pad = 16, SP.md = 12).
  const pad = isMobile ? SP.md : ISLAND.pad;
  // Ширина переключателя темы в шапке: на мобиле — во всю доступную ширину,
  // на десктопе — фиксированная, чтобы заголовок не прижимался к контролу.
  const themeControlW = isMobile ? '100%' : 320;

  return (
    <div style={{
      position: 'relative',
      isolation: 'isolate',          // чтобы CanvasBackdrop (zIndex:-1) не провалился
      minHeight: '100vh',
      background: C.bgMain,
      fontFamily: FONT.sans,
      color: C.textPrimary,
    }}>
      <CanvasBackdrop />

      <div style={{
        position: 'relative',
        maxWidth: isMobile ? 1100 : 1280,
        margin: '0 auto',
        padding: pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Шапка витрины + переключатель темы */}
        <Island>
          <IslandHeader
            icon={
              <Palette
                size={ICON_SIZE.md}
                strokeWidth={ICON_STROKE}
                style={{ color: C.accent, flexShrink: 0 }}
              />
            }
            title="UI Kit — витрина дизайн-системы"
            badge="dev"
            actions={
              <div style={{ width: themeControlW, flexShrink: 0 }}>
                <SegmentedControl
                  value={mode}
                  options={THEME_OPTIONS}
                  onChange={setThemeMode}
                />
              </div>
            }
          />
        </Island>

        {/* TOC на мобиле — горизонтальный sticky-бар под шапкой темы.
            На десктопе вместо него — sidebar справа от секций (см. ниже). */}
        {isMobile && <Toc variant="bar" />}

        {/* Основная зона: на десктопе — flex-row {секции | TOC sidebar},
            на мобиле — просто колонка секций (TOC bar уже отрисован выше). */}
        <div style={{ display: 'flex', gap: ISLAND.gap, alignItems: 'flex-start' }}>
          <div style={{
            flex: 1,
            minWidth: 0,
            display: 'flex',
            flexDirection: 'column',
            gap: ISLAND.gap,
          }}>
            {/* Замер реальных CSS-размеров устройства (см. ViewportSection) */}
            <div id="sec-viewport" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <ViewportSection />
            </div>

            {/* Примитивы — переключатели */}
            <div id="sec-toggles" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <TogglesSection />
            </div>

            {/* Примитивы — оверлеи и меню */}
            <div id="sec-overlays" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <OverlaysSection />
            </div>

            {/* Toolbar + ToolbarIconButton + ToolbarOverflowMenu + EmptyState */}
            <div id="sec-toolbar" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <ToolbarAndEmptySection />
            </div>

            {/* Примитивы — кнопки */}
            <div id="sec-buttons" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <ButtonsSection />
            </div>

            {/* Примитивы — поля (TextField / TextArea / IconField / Field) */}
            <div id="sec-fields" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <FieldsSection />
            </div>

            {/* Секция «Типографика» — FS × FONT */}
            <div id="sec-typography" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <TypographySection />
            </div>

            {/* Секция «Шкалы» — SP (отступы), R (радиусы), SHADOW (тени) */}
            <div id="sec-scales" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <ScalesSection />
            </div>

            {/* Секция «Палитры-данные» — GROUP_COLORS × AGENT_COLORS */}
            <div id="sec-palettes" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <DataPalettesSection />
            </div>

            {/* Секция «Примитивы — острова и холст» — Island/IslandHeader,
                IslandScaffold (на холсте CanvasBackdrop), статичные сплиттеры */}
            <div id="sec-islands" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <IslandsSection />
            </div>

            {/* Секция «Цвета» — программный обход C с группировкой и
                resolved-значениями из getComputedStyle (обновляется при смене темы) */}
            <div id="sec-colors" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <ColorsSection />
            </div>

            {/* Секция «Панели» — правая рельса + левые сайдбары + чаты + тона */}
            <div id="sec-panels" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <PanelsSection />
            </div>

            {/* Секция «Шапки» — HubHeader, ProjectRail, IslandHeader */}
            <div id="sec-headers" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <HeadersSection />
            </div>
          </div>

          {/* TOC на десктопе — sticky sidebar справа от секций */}
          {!isMobile && (
            <aside style={{
              width: 200,
              flexShrink: 0,
              position: 'sticky',
              top: SP.md,
              alignSelf: 'flex-start',
            }}>
              <Toc variant="sidebar" />
            </aside>
          )}
        </div>

        {/* Placeholder «Секции» — сюда добавятся палитра/типографика/кнопки/... */}
        <Island>
          <IslandHeader
            icon={
              <Layers
                size={ICON_SIZE.md}
                strokeWidth={ICON_STROKE}
                style={{ color: C.textMuted, flexShrink: 0 }}
              />
            }
            title="Секции"
          />
          <div style={{
            padding: ISLAND.pad,
            display: 'flex',
            flexDirection: 'column',
            gap: SP.md,
          }}>
            <p style={{
              margin: 0,
              fontSize: FS.md,
              color: C.textSecondary,
            }}>
              Сюда добавятся секции компонентов: палитра цветов, типографика,
              кнопки, поля, модальные окна и т.д.
            </p>
            <p style={{
              margin: 0,
              fontSize: FS.sm,
              color: C.textMuted,
            }}>
              Каркас страницы готов. Каждая секция будет отдельным
              компонентом-витриной, рендерящимся на общем холсте.
            </p>
          </div>
        </Island>
      </div>
    </div>
  );
}

// === Секция «Примитивы — переключатели» ===========================
// Toggle (on/off/disabled) + SegmentedControl (живой пример) + Dot (цвета).
// Стили — только токены C/FS/SP/ISLAND; подписи к состояниям — через FS.sm
// и C.textMuted, чтобы повторять палитру каркаса.
function TogglesSection() {
  const [toggleOn, setToggleOn] = useState(true);
  const [toggleOff, setToggleOff] = useState(false);
  const [layout, setLayout] = useState('comfort');

  return (
    <Island>
      <IslandHeader
        icon={
          <ToggleRight
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Примитивы — переключатели"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Toggle: состояния on / off / disabled-on / disabled-off */}
        <SubBlock label="Toggle — on / off / disabled">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.lg, alignItems: 'center' }}>
            <ToggleRow label="Включён">
              <Toggle checked={toggleOn} onChange={setToggleOn} ariaLabel="Демо: включён" />
            </ToggleRow>
            <ToggleRow label="Выключен">
              <Toggle checked={toggleOff} onChange={setToggleOff} ariaLabel="Демо: выключен" />
            </ToggleRow>
            <ToggleRow label="Disabled (on)">
              <Toggle checked={true} onChange={() => {}} disabled ariaLabel="Демо: отключён включён" />
            </ToggleRow>
            <ToggleRow label="Disabled (off)">
              <Toggle checked={false} onChange={() => {}} disabled ariaLabel="Демо: отключён выключен" />
            </ToggleRow>
          </div>
        </SubBlock>

        {/* SegmentedControl: живой переключатель с 3 опциями */}
        <SubBlock label={`SegmentedControl — текущий: ${layout}`}>
          <SegmentedControl
            value={layout}
            options={LAYOUT_OPTIONS}
            onChange={setLayout}
          />
        </SubBlock>

        {/* Dot: демо-цвета из семантических токенов C.* */}
        <SubBlock label="Dot — индикаторы цвета">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.lg, alignItems: 'center' }}>
            {DOT_SAMPLES.map((d) => (
              <div key={d.label} style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
                <Dot color={d.color} size={10} />
                <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{d.label}</span>
              </div>
            ))}
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
              <Dot color={C.accent} size={14} />
              <span style={{ fontSize: FS.sm, color: C.textMuted }}>size=14</span>
            </div>
          </div>
        </SubBlock>

        {/* FileTypeTile: плитка типа файла — одна на все списки файлов продукта
            («Файлы», «Документы», «Изменения», шапка просмотрщика). Габарит и
            палитра живут внутри примитива, снаружи задаётся только имя файла */}
        <SubBlock label="FileTypeTile — тип файла перед именем">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.md, alignItems: 'center' }}>
            {FILE_TILE_SAMPLES.map((name) => (
              <div key={name} style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
                <FileTypeTile name={name} />
                <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{name}</span>
              </div>
            ))}
          </div>
        </SubBlock>

        {/* FileStatusBadge: состояние файла — общий значок дерева «Файлов» и панели
            «Изменения». Цветом имени состояние не кодируется нигде: цвет там занят
            другими смыслами (заметки, база знаний) */}
        <SubBlock label="FileStatusBadge — состояние файла после имени">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.md, alignItems: 'center' }}>
            {FILE_STATUS_SAMPLES.map((s) => (
              <div key={s.status} style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
                <FileStatusBadge status={s.status} />
                <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{s.label}</span>
              </div>
            ))}
          </div>
        </SubBlock>

        {/* Badge: плашка с текстом. Тон — роль, а не цвет: набор ограничен парами
            токенов, которые уже есть в обеих темах. Кликабельная плашка (есть onClick)
            становится кнопкой — так открывается меню смены значения свойства */}
        <SubBlock label="Badge — плашка состояния (7 тонов, 2 размера, кликабельная)">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
              {BADGE_TONES.map((t) => <Badge key={t} tone={t}>{t}</Badge>)}
            </div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
              {BADGE_TONES.map((t) => <Badge key={t} tone={t} size="xs" dot>{t}</Badge>)}
            </div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
              <Badge tone="success" dot onClick={() => {}}>Принято</Badge>
              <Badge tone="info" dot onClick={() => {}} active>Предложено (меню открыто)</Badge>
              <Badge tone="danger" icon={<X size={11} />}>С иконкой</Badge>
            </div>
          </div>
        </SubBlock>

        {/* SidebarSection: сворачиваемая секция колонки. Заголовок капсом со счётчиком,
            правый слот действий виден только у раскрытой. Свёрнутая занимает ровно
            строку заголовка — колонка из двух закрытых секций не оставляет пустоты */}
        <SubBlock label="SidebarSection — сворачиваемая секция колонки">
          <div style={{ maxWidth: 290, display: 'flex', flexDirection: 'column' }}>
            <SidebarSection title="Свойства" count={3} defaultOpen={false}>
              <div style={{ fontSize: FS.sm, color: C.textSecondary }}>Содержимое секции</div>
            </SidebarSection>
            <SidebarSection
              title="Комментарии" count={2} hint="2 откр."
              actions={<Button size="xs" variant="secondary">Разобрать</Button>}
            >
              <div style={{ fontSize: FS.sm, color: C.textSecondary }}>Список комментариев</div>
            </SidebarSection>
          </div>
        </SubBlock>
      </div>
    </Island>
  );
}

// === Секция «Примитивы — оверлеи и меню» ==========================
// Modal / ConfirmDialog / Menu+MenuItem / BackButton / WaitingIndicator.
// Триггеры открывают соответствующие оверлеи; BackButton — статично;
// WaitingIndicator показан в обычном режиме и с hint. Стили — только токены
// C/FS/SP/ISLAND; переиспользуется SubBlock из секции переключателей.
function OverlaysSection() {
  const [modalOpen, setModalOpen] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);

  return (
    <Island>
      <IslandHeader
        icon={
          <LayoutTemplate
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Примитивы — оверлеи и меню"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Modal: триггер открывает демку с title/subtitle/footer=ModalActions */}
        <SubBlock label="Modal — центрированная карточка / мобильная шторка">
          <Button variant="primary" size="md" onClick={() => setModalOpen(true)}>
            Открыть Modal
          </Button>
        </SubBlock>

        {/* ConfirmDialog: триггер открывает диалог подтверждения */}
        <SubBlock label="ConfirmDialog — замена window.confirm()">
          <Button variant="secondary" size="md" onClick={() => setConfirmOpen(true)}>
            Открыть ConfirmDialog
          </Button>
        </SubBlock>

        {/* Menu / MenuItem: триггер-иконка, пункты с danger и disabled */}
        <SubBlock label="Menu / MenuItem — выпадающее меню (danger + disabled)">
          <div style={{ position: 'relative', display: 'inline-flex' }}>
            <IconButton
              size="sm"
              variant="ghost"
              tone="muted"
              title="Открыть меню"
              onClick={() => setMenuOpen((v) => !v)}
            >
              <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
            {menuOpen && (
              <Menu onClose={() => setMenuOpen(false)}>
                <MenuItem
                  icon={<Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  label="Редактировать"
                  onClick={() => setMenuOpen(false)}
                />
                <MenuItem
                  icon={<Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  label="Дублировать"
                  onClick={() => setMenuOpen(false)}
                />
                <MenuItem
                  icon={<Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  label="Удалить"
                  danger
                  onClick={() => setMenuOpen(false)}
                />
                <MenuItem label="Недоступно (disabled)" disabled />
              </Menu>
            )}
          </div>
        </SubBlock>

        {/* BackButton: статичный пример */}
        <SubBlock label="BackButton — кнопка «назад» для тулбаров">
          <BackButton onClick={() => {}} title="Назад">
            <span style={{ fontSize: FS.base, color: C.textSecondary }}>Все проекты</span>
          </BackButton>
        </SubBlock>

        {/* WaitingIndicator: обычный режим + с hint */}
        <SubBlock label="WaitingIndicator — индикатор ожидания (обычный и с hint)">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
            <WaitingIndicator />
            <WaitingIndicator hint="Читаю файлы проекта…" />
          </div>
        </SubBlock>
      </div>

      {/* Демо Modal: title + subtitle + контент + footer=ModalActions */}
      {modalOpen && (
        <Modal
          title="Демо Modal"
          subtitle="Десктоп — центрированная карточка, мобила — шторка снизу с drag-handle."
          width={MODAL_W.form}
          onClose={() => setModalOpen(false)}
          footer={
            <ModalActions
              confirmLabel="Понятно"
              onConfirm={() => setModalOpen(false)}
              onCancel={() => setModalOpen(false)}
            />
          }
        >
          <p style={{
            margin: 0,
            fontSize: FS.md,
            color: C.textSecondary,
            lineHeight: 1.5,
          }}>
            Тело модалки — любой контент: формы, списки, текст. Footer собран
            из ModalActions: «Отмена» слева, основное действие справа, в один
            ряд на любой ширине.
          </p>
        </Modal>
      )}

      {/* Демо ConfirmDialog: danger-вариант */}
      {confirmOpen && (
        <ConfirmDialog
          title="Удалить демо-запись?"
          subtitle="Это действие нельзя отменить."
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => setConfirmOpen(false)}
          onCancel={() => setConfirmOpen(false)}
        />
      )}
    </Island>
  );
}

// Лейбл-подпись над группой примитива: единый визуальный ритм секции.
function SubBlock({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{label}</div>
      {children}
    </div>
  );
}

// Контрол + подпись его состояния: читается как «что я сейчас вижу».
function ToggleRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
      {children}
      <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{label}</span>
    </div>
  );
}

// === Секция «Примитивы — поля» ====================================
// TextField / TextArea / IconField во всех состояниях (обычное + disabled)
// и связка Field + FieldLabel (лейбл + контрол + hint). Focus-ring виден при
// клике — контролы из Field.tsx сами управляют boxShadow:focus. Размеры —
// только из шкал, ни одного magic number; цвета — из C, иконки — ICON_SIZE.
function FieldsSection() {
  const [text, setText] = useState('');
  const [textArea, setTextArea] = useState('');
  const [iconMail, setIconMail] = useState('');
  const [iconSearch, setIconSearch] = useState('');
  const [fielded, setFielded] = useState('');

  return (
    <Island>
      <IslandHeader
        icon={
          <Pencil
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Примитивы — поля"
        badge="ui/Field"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: SP.lg,
      }}>
        {/* TextField — однострочный ввод: обычное + disabled */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <FieldLabel>TextField</FieldLabel>
          <TextField
            value={text}
            onChange={setText}
            placeholder="Обычное поле ввода"
          />
          <TextField
            value=""
            onChange={() => {}}
            placeholder="Disabled поле"
            disabled
          />
        </div>

        {/* TextArea — многострочный ввод с авто-ростом: обычное + disabled */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <FieldLabel>TextArea</FieldLabel>
          <TextArea
            value={textArea}
            onChange={setTextArea}
            placeholder="Многострочный текст…"
            autoGrow
            minHeight={80}
          />
          <TextArea
            value=""
            onChange={() => {}}
            placeholder="Disabled"
            disabled
          />
        </div>

        {/* IconField — поле с lucide-иконкой-префиксом: обычное + disabled */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <FieldLabel>IconField</FieldLabel>
          <IconField
            icon={<Mail size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
            value={iconMail}
            onChange={setIconMail}
            placeholder="E-mail"
          />
          <IconField
            icon={<Search size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
            value={iconSearch}
            onChange={setIconSearch}
            placeholder="Поиск (disabled)"
            disabled
          />
        </div>

        {/* Field + FieldLabel — связка «лейбл + контрол + hint» */}
        <Field
          label="Email для рассылки"
          hint="На этот адрес придёт письмо с подтверждением."
        >
          <TextField
            value={fielded}
            onChange={setFielded}
            placeholder="you@example.com"
          />
        </Field>
      </div>
    </Island>
  );
}

// === Секция «Замер экрана» ========================================
// Служебная плашка: снимает РЕАЛЬНЫЕ CSS-размеры устройства, на котором открыта
// витрина. Нужна потому, что справочники устройств считают CSS как «физика ÷ 3»
// и промахиваются (у Fold 7 расчёт давал 728 CSS, живой замер — 673, отсюда и
// MOBILE_MAX = 600), а консоли и `javascript:`-строк в адресной строке на
// телефоне нет. Снятые цифры переносятся в docs/design/target-devices.md.

type ViewportMetrics = {
  w: number; h: number; dpr: number;
  screenW: number; screenH: number;
  standalone: boolean;
};

function readViewportMetrics(): ViewportMetrics {
  return {
    w: window.innerWidth,
    h: window.innerHeight,
    dpr: window.devicePixelRatio,
    screenW: window.screen.width,
    screenH: window.screen.height,
    standalone: window.matchMedia('(display-mode: standalone)').matches,
  };
}

// Пересъём на resize и повороте. Зум страницы меняет DPR и тоже приходит
// resize'ом, поэтому отдельная подписка на плотность не нужна.
function useViewportMetrics(): ViewportMetrics {
  const [m, setM] = useState(readViewportMetrics);
  useEffect(() => {
    const onChange = () => setM(readViewportMetrics());
    window.addEventListener('resize', onChange);
    window.addEventListener('orientationchange', onChange);
    return () => {
      window.removeEventListener('resize', onChange);
      window.removeEventListener('orientationchange', onChange);
    };
  }, []);
  return m;
}

// Строка «подпись — значение»: значение моноширинным, чтобы цифры стояли
// колонкой, а не плясали по ширине глифов.
function MetricRow({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
      <span style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: SP.xxxl * 3 }}>{label}</span>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.md, color: C.textPrimary }}>{value}</span>
      {hint ? <span style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</span> : null}
    </div>
  );
}

function ViewportSection() {
  const m = useViewportMetrics();

  const layout = m.w <= MOBILE_MAX ? 'мобильная'
    : m.w <= TABLET_MAX ? 'планшетная'
    : 'десктопная';

  // Запас до ближайшего порога раскладки. У раскладных он бывает в десяток
  // пикселей — тогда это решающая цифра, а не любопытная.
  const gap = m.w <= MOBILE_MAX ? MOBILE_MAX - m.w
    : m.w <= TABLET_MAX ? Math.min(m.w - MOBILE_MAX, TABLET_MAX - m.w)
    : m.w - TABLET_MAX;
  const tight = gap <= 40;

  const dpr = Math.round(m.dpr * 1000) / 1000;
  const summary = `${m.w} × ${m.h} CSS @ DPR ${dpr} → ${Math.round(m.w * dpr)} × ${Math.round(m.h * dpr)} физических`;

  return (
    <Island>
      <IslandHeader
        icon={
          <Smartphone
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Замер экрана"
        badge={`${layout} раскладка`}
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Готовая строка для доки: userSelect:'all' — на телефоне выделяется
            одним тапом, иначе её пришлось бы ловить пальцем по символу.
            Clipboard API тут не годится: с телефона витрину открывают по http
            в локальной сети, а там navigator.clipboard недоступен. */}
        <SubBlock label="Строка для docs/design/target-devices.md — выдели и скопируй">
          <div style={{
            fontFamily: FONT.mono,
            fontSize: FS.lg,
            color: C.textPrimary,
            background: C.bgWhite,
            border: `1px solid ${C.border}`,
            borderRadius: R.xl,
            padding: SP.md,
            userSelect: 'all',
            overflowWrap: 'anywhere',
          }}>
            {summary}
          </div>
        </SubBlock>

        <SubBlock label="Подробно">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <MetricRow label="Окно (CSS)" value={`${m.w} × ${m.h}`} hint="то, с чем работает вёрстка" />
            <MetricRow label="Плотность (DPR)" value={String(dpr)} hint="физических точек на CSS-пиксель" />
            <MetricRow
              label="Физические точки"
              value={`${Math.round(m.w * dpr)} × ${Math.round(m.h * dpr)}`}
              hint="паспортная цифра производителя"
            />
            <MetricRow
              label="Экран целиком (CSS)"
              value={`${m.screenW} × ${m.screenH}`}
              hint="весь дисплей, а не окно браузера"
            />
            <MetricRow label="Ориентация" value={m.h >= m.w ? 'портрет' : 'ландшафт'} />
            <MetricRow
              label="Режим отображения"
              value={m.standalone ? 'установленная PWA' : 'вкладка браузера'}
              hint="в PWA высота больше — нет адресной строки"
            />
            <MetricRow
              label="Пороги раскладки"
              value={`MOBILE_MAX ${MOBILE_MAX} · TABLET_MAX ${TABLET_MAX}`}
              hint={`запас до ближайшего — ${gap}px`}
            />
          </div>
        </SubBlock>

        {tight ? (
          <Badge tone="warning">
            До переключения раскладки {gap}px — экран на границе, проверь оба режима
          </Badge>
        ) : null}

        <SubBlock label="Чтобы замер не врал">
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.6 }}>
            Зум страницы — 100% (в Chrome он множится на DPR). Режим «версия для ПК» —
            выключен, он подменяет ширину десктопной. Складные меряем в обоих состояниях,
            остальные — в обеих ориентациях.
          </div>
        </SubBlock>
      </div>
    </Island>
  );
}

// === Секция «Шкалы» ===============================================
// Три шкалы через Object.entries — SP (отступы, длина полоски = значению),
// R (радиусы, скругление плашки = значению), SHADOW (тени, boxShadow из
// var(--shadow-*)). Все размеры — из шкал; ни одного magic number и ни одного
// hex — только токены C/FS/SP/R и семантика var(--shadow-*).
function ScalesSection() {
  // Object.entries на as const даёт union литералов; приводим к нужному типу.
  const sp = Object.entries(SP) as [string, number][];
  const radii = Object.entries(R) as [string, number | string][];
  const shadows = Object.entries(SHADOW) as [string, string][];

  return (
    <Island>
      <IslandHeader
        icon={
          <Ruler
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Шкалы"
        badge="SP · R · SHADOW"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* SP — отступы: полоска длиной = значению (flexShrink:0, minWidth —
            чтобы flex не сжал маленькие значения в ноль) */}
        <SubBlock label="SP — отступы (длина полоски = значению)">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            {sp.map(([key, value]) => (
              <div key={`sp-${key}`} style={{ display: 'flex', alignItems: 'center', gap: SP.md }}>
                <span style={{
                  flexShrink: 0,
                  width: SP.xxxl * 2,            // единая ширина подписи из шкалы
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textSecondary,
                }}>
                  SP.{key} = {value}
                </span>
                <div style={{
                  width: value,
                  height: SP.sm,
                  minWidth: value,
                  flexShrink: 0,
                  background: C.accent,
                  borderRadius: R.sm,
                }} />
              </div>
            ))}
          </div>
        </SubBlock>

        {/* R — радиусы: квадратная плашка со скруглением = значению.
            full='50%' и max=999 оба дают круг (на квадратной плашке). */}
        <SubBlock label="R — радиусы (скругление плашки = значению)">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.md }}>
            {radii.map(([key, value]) => (
              <div key={`r-${key}`} style={{
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                gap: SP.xs,
              }}>
                <div style={{
                  width: SP.xxxl,
                  height: SP.xxxl,
                  background: C.bgWhite,
                  border: `1px solid ${C.border}`,
                  borderRadius: value,
                }} />
                <span style={{
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textSecondary,
                  textAlign: 'center',
                }}>
                  R.{key} = {value}
                </span>
              </div>
            ))}
          </div>
        </SubBlock>

        {/* SHADOW — тени: карточка с boxShadow из var(--shadow-*). Значения —
            токены темы (theme.css), на тёмной — усилены. Никаких hex-литералов. */}
        <SubBlock label="SHADOW — тени (boxShadow из var(--shadow-*))">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.lg }}>
            {shadows.map(([key, value]) => (
              <div key={`shadow-${key}`} style={{
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                gap: SP.xs,
              }}>
                <div style={{
                  width: SP.xxxl * 2,            // побольше, чтобы тень была заметна
                  height: SP.xxxl,
                  background: C.bgWhite,
                  borderRadius: R.lg,
                  boxShadow: value,
                }} />
                <span style={{
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textSecondary,
                  textAlign: 'center',
                }}>
                  SHADOW.{key}
                </span>
              </div>
            ))}
          </div>
        </SubBlock>
      </div>
    </Island>
  );
}

// === Секция «Типографика» =========================================
// Программный обход Object.entries(FS): для каждого размера — три образца
// (FONT.sans для UI, FONT.serif для заголовков, FONT.mono для кода) и подпись
// токена FS.{key} = {px}. Размеры — только из FS, ни одного magic number.
// Цвета и отступы — из токенов C/SP/ISLAND (lint:design зелёный).

// Образцы текста для трёх семейств: каждый показывает характер шрифта в своём
// назначении. Длина подобрана так, чтобы при FS.display = 34 строка ещё
// влезала в колонку острова.
const TYPE_SAMPLES = {
  sans:  'Интерфейс — это разговор продукта с человеком.',
  serif: 'Заголовок держит внимание засечками.',
  mono:  "git commit -m 'feat: add tokens'",
} as const;

// Три семейства с подписями их роли — рендерятся одним размером для каждой
// строки FS.*, чтобы сравнивать только размер, а не шрифт.
const FONT_VARIANTS: { fam: string; label: string; sample: string; color: string }[] = [
  { fam: FONT.sans,  label: 'UI',        sample: TYPE_SAMPLES.sans,  color: C.textPrimary },
  { fam: FONT.serif, label: 'Заголовок', sample: TYPE_SAMPLES.serif, color: C.textHeading },
  { fam: FONT.mono,  label: 'Код',       sample: TYPE_SAMPLES.mono,  color: C.textSecondary },
];

function TypographySection() {
  // Object.entries на as const даёт union литералов; приводим к [string, number].
  const sizes = Object.entries(FS) as [string, number][];

  return (
    <Island>
      <IslandHeader
        icon={
          <Type
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Типографика"
        badge="FONT × FS"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: SP.lg,
      }}>
        {sizes.map(([key, px]) => (
          <div key={key} style={{
            display: 'flex',
            flexDirection: 'column',
            gap: SP.sm,
          }}>
            {/* Подпись токена — мелкая, моноширинная, не зависит от px образца */}
            <div style={{
              fontFamily: FONT.mono,
              fontSize: FS.xs,
              color: C.textMuted,
            }}>
              FS.{key} = {px}
            </div>
            {/* Три семейства одним размером — сравнение только по размеру */}
            <div style={{
              display: 'flex',
              flexDirection: 'column',
              gap: SP.xs,
            }}>
              {FONT_VARIANTS.map(({ fam, label, sample, color }) => (
                <div key={label} style={{
                  display: 'flex',
                  alignItems: 'baseline',
                  gap: SP.md,
                }}>
                  {/* Вкладка роли шрифта — фиксированной ширины, мелкая */}
                  <span style={{
                    flexShrink: 0,
                    width: 64,
                    fontFamily: FONT.sans,
                    fontSize: FS.xs,
                    color: C.textMuted,
                  }}>
                    {label}
                  </span>
                  {/* Образец текущим размером px — токен из FS, не magic number */}
                  <span style={{
                    fontFamily: fam,
                    fontSize: px,
                    lineHeight: 1.4,
                    color,
                  }}>
                    {sample}
                  </span>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    </Island>
  );
}

// === Секция «Примитивы — острова и холст» =========================
// Демо Island + IslandHeader (icon + title + badge + actions), мини-макет
// IslandScaffold (сайдбар + центр) внутри обёртки с CanvasBackdrop, и галерея
// статичных сплиттеров (Splitter, SidebarSplitter, IslandSplitter,
// IslandSidebarSplitter — вертикальные и горизонтальные). Компоненты только
// импортируются — их внутренности не правятся. Все цвета/размеры — из токенов
// C / FS / SP / ISLAND; ни одного hex-литерала.
function IslandsSection() {
  // Локальные стили для демо-«панелей» по бокам сплиттеров — убирают
  // дублирование и держат единый ритм с SubBlock.
  const flatPanelStyle: CSSProperties = {
    flex: 1, padding: SP.sm,
    background: C.bgInset, color: C.textSecondary,
    fontSize: FS.xs, fontFamily: FONT.mono,
    display: 'flex', alignItems: 'center',
  };
  // «Остров» в миниатюре — плашка с рамкой/скруглением как у настоящего Island,
  // чтобы IslandSplitter читался именно как зазор между островами.
  const islandPanelStyle: CSSProperties = {
    flex: 1, padding: SP.sm,
    background: C.bgMain, border: `1px solid ${ISLAND.border}`,
    borderRadius: ISLAND.radius,
    color: C.textSecondary, fontSize: FS.xs, fontFamily: FONT.mono,
    display: 'flex', alignItems: 'center',
  };
  // no-op колбэки: сплиттеры статичные, drag/collapse не обрабатываем.
  const noop = () => {};

  return (
    <Island>
      <IslandHeader
        icon={
          <LayoutGrid
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Примитивы — острова и холст"
        badge="ui/Island"
        actions={
          <>
            <IconButton title="Настройки острова" size="sm">
              <Settings size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
            <IconButton title="Закрыть" size="sm">
              <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
          </>
        }
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Island + IslandHeader — карточка с шапкой */}
        <SubBlock label="Island + IslandHeader — карточка с шапкой (icon / title / badge / actions)">
          <p style={{
            margin: 0,
            fontSize: FS.sm,
            color: C.textSecondary,
            lineHeight: 1.5,
          }}>
            Карточка-остров: рамка + скругление + тень + подложка. Шапка утоплена
            относительно тела — иконка слева, затем заголовок, бейдж и кнопки
            действий справа. Эта секция построена на Island + IslandHeader.
          </p>
        </SubBlock>

        {/* IslandScaffold — каркас хаб-страницы на холсте CanvasBackdrop */}
        <SubBlock label="IslandScaffold — сайдбар-остров → сплиттер-зазор → центр-остров (на холсте CanvasBackdrop)">
          {/* bg=transparent: шапка IslandHeader и контент-зона ниже несут свой
              bg=C.bgMain, а прозрачность корня видна только там, где рисует
              CanvasBackdrop — дудл-холст читается сквозь «воздух» между островами */}
          <Island bg="transparent">
            {/* Обёртка-холст: position:relative + isolation:isolate, чтобы
                CanvasBackdrop (zIndex:-1) не провалился под фон родителя */}
            <div style={{
              position: 'relative',
              isolation: 'isolate',
              height: 260,
              minHeight: 0,
              background: C.bgMain,
            }}>
              <CanvasBackdrop />
              <IslandScaffold
                sidebarOpen
                sidebarWidth={180}
                sidebarDragging={false}
                onSidebarDrag={noop}
                onSidebarCollapse={noop}
                sidebar={
                  <div style={{
                    height: '100%',
                    padding: ISLAND.pad,
                    display: 'flex',
                    flexDirection: 'column',
                    gap: SP.sm,
                    fontFamily: FONT.sans,
                    color: C.textSecondary,
                  }}>
                    <span style={{
                      fontSize: FS.sm,
                      fontWeight: 600,
                      color: C.textHeading,
                    }}>
                      Сайдбар
                    </span>
                    <span style={{ fontSize: FS.xs }}>Пункт списка 1</span>
                    <span style={{ fontSize: FS.xs }}>Пункт списка 2</span>
                    <span style={{ fontSize: FS.xs }}>Пункт списка 3</span>
                  </div>
                }
                center={
                  <div style={{
                    height: '100%',
                    display: 'flex',
                    flexDirection: 'column',
                    alignItems: 'center',
                    justifyContent: 'center',
                    gap: SP.xs,
                    fontFamily: FONT.sans,
                    color: C.textSecondary,
                  }}>
                    <Columns2
                      size={ICON_SIZE.lg}
                      strokeWidth={ICON_STROKE}
                      style={{ color: C.accent, flexShrink: 0 }}
                    />
                    <span style={{ fontSize: FS.lg, color: C.textHeading }}>
                      Центральный остров
                    </span>
                    <span style={{ fontSize: FS.sm }}>
                      сайдбар → зазор-сплиттер → центр
                    </span>
                  </div>
                }
              />
            </div>
          </Island>
        </SubBlock>

        {/* Splitter — базовый 1px-сплиттер, v/h, accent в active */}
        <SubBlock label="Splitter — тонкая 1px-линия (v / h), accent при active/hover">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <div style={{ display: 'flex', height: 64, alignItems: 'stretch' }}>
              <div style={flatPanelStyle}>панель</div>
              <Splitter orientation="v" active onMouseDown={noop} />
              <div style={{ ...flatPanelStyle, background: 'transparent', color: C.textMuted }}>
                контент
              </div>
            </div>
            <div style={{ display: 'flex', flexDirection: 'column' }}>
              <div style={flatPanelStyle}>контент над</div>
              <Splitter orientation="h" active onMouseDown={noop} />
              <div style={{ ...flatPanelStyle, background: 'transparent', color: C.textMuted }}>
                контент под
              </div>
            </div>
          </div>
        </SubBlock>

        {/* SidebarSplitter — Splitter + всплывающая кнопка «свернуть панель» */}
        <SubBlock label="SidebarSplitter — Splitter + всплывающая кнопка «свернуть панель»">
          <div style={{ display: 'flex', height: 64, alignItems: 'stretch' }}>
            <div style={flatPanelStyle}>сайдбар</div>
            <SidebarSplitter active onMouseDown={noop} onCollapse={noop} />
            <div style={{ ...flatPanelStyle, background: 'transparent', color: C.textMuted }}>
              контент
            </div>
          </div>
        </SubBlock>

        {/* IslandSplitter — прозрачный зазор между островами */}
        <SubBlock label="IslandSplitter — прозрачный зазор между островами (v / h)">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <div style={{ display: 'flex', height: 64, alignItems: 'stretch' }}>
              <div style={islandPanelStyle}>остров</div>
              <IslandSplitter orientation="v" active onMouseDown={noop} />
              <div style={islandPanelStyle}>остров</div>
            </div>
            <div style={{ display: 'flex', flexDirection: 'column' }}>
              <div style={islandPanelStyle}>остров над</div>
              <IslandSplitter orientation="h" active onMouseDown={noop} />
              <div style={islandPanelStyle}>остров под</div>
            </div>
          </div>
        </SubBlock>

        {/* IslandSidebarSplitter — IslandSplitter + кнопка «свернуть панель» */}
        <SubBlock label="IslandSidebarSplitter — IslandSplitter + кнопка «свернуть панель»">
          <div style={{ display: 'flex', height: 64, alignItems: 'stretch' }}>
            <div style={islandPanelStyle}>остров</div>
            <IslandSidebarSplitter active onMouseDown={noop} onCollapse={noop} />
            <div style={islandPanelStyle}>остров</div>
          </div>
        </SubBlock>
      </div>
    </Island>
  );
}

// === Данные для секции «Примитивы — кнопки» ============================

const BUTTON_VARIANTS: ButtonVariant[] = ['primary', 'secondary', 'ghost', 'ghostAccent', 'danger', 'dashed'];
const BUTTON_SIZES: ButtonSize[] = ['sm', 'md', 'lg'];

const IB_SIZES: IconButtonSize[] = ['xs', 'sm', 'md', 'lg'];
const IB_TONES: IconButtonTone[] = ['muted', 'accent', 'danger'];
const IB_VARIANTS: IconButtonVariant[] = ['ghost', 'soft'];

// Семантическая иконка на каждый тон: muted — поиск, accent — звезда, danger — удаление.
const TONE_ICON: Record<IconButtonTone, LucideIcon> = {
  muted: Search,
  accent: Star,
  danger: Trash2,
};

// Размер иконки внутри IconButton зависит от коробки (синхронизировано с ICON_SIZE).
const IB_ICON_BY_SIZE: Record<IconButtonSize, number> = {
  xs: ICON_SIZE.xs,
  sm: ICON_SIZE.sm,
  md: ICON_SIZE.sm,
  lg: ICON_SIZE.md,
};

// === Секция «Примитивы — кнопки» =======================================
// Button (6 variants × 3 sizes + disabled/loading/fullWidth/leftIcon) и
// IconButton (size × tone × variant + active/disabled). Все ячейки видны
// одновременно — статичная витрина компонентов и токенов.
function ButtonsSection() {
  return (
    <Island>
      <IslandHeader
        icon={
          <MousePointerClick
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Примитивы — кнопки"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* --- Button: матрица variants × sizes --- */}
        <SubBlock label="Button — варианты × размеры">
          <div style={{ overflowX: 'auto' }}>
            <div style={{
              display: 'grid',
              gridTemplateColumns: '112px repeat(3, auto)',
              columnGap: SP.lg,
              rowGap: SP.md,
              alignItems: 'center',
              minWidth: 460,
            }}>
              <div />
              {BUTTON_SIZES.map(s => (
                <div key={s} style={{
                  fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
                  textTransform: 'uppercase', letterSpacing: 0.5,
                }}>{s}</div>
              ))}
              {BUTTON_VARIANTS.map(v => (
                <Fragment key={v}>
                  <div style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textSecondary }}>{v}</div>
                  {BUTTON_SIZES.map(s => (
                    <Button key={v + '-' + s} variant={v} size={s}>{v}</Button>
                  ))}
                </Fragment>
              ))}
            </div>
          </div>
        </SubBlock>

        {/* --- Button: состояния --- */}
        <SubBlock label="Button — состояния">
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
            <ButtonsStateRow label="disabled">
              {BUTTON_VARIANTS.map(v => (
                <Button key={v} variant={v} size="sm" disabled>{v}</Button>
              ))}
            </ButtonsStateRow>
            <ButtonsStateRow label="loading">
              <Button variant="primary" size="sm" loading>Сохранить</Button>
              <Button variant="secondary" size="sm" loading>Отмена</Button>
              <Button variant="ghostAccent" size="sm" loading>Действие</Button>
              <Button variant="danger" size="sm" loading>Удалить</Button>
            </ButtonsStateRow>
            <ButtonsStateRow label="fullWidth">
              <div style={{ flex: 1, minWidth: 200 }}>
                <Button variant="primary" fullWidth>На всю ширину</Button>
              </div>
            </ButtonsStateRow>
            <ButtonsStateRow label="leftIcon">
              <Button variant="primary" size="sm" leftIcon={<Plus {...ICON_PROPS} size={ICON_SIZE.sm} />}>Создать</Button>
              <Button variant="secondary" size="sm" leftIcon={<Download {...ICON_PROPS} size={ICON_SIZE.sm} />}>Экспорт</Button>
              <Button variant="ghostAccent" size="sm" leftIcon={<Send {...ICON_PROPS} size={ICON_SIZE.sm} />}>Отправить</Button>
            </ButtonsStateRow>
          </div>
        </SubBlock>

        {/* --- IconButton: size × tone × variant (ghost / soft) --- */}
        <SubBlock label="IconButton — size × tone × variant (ghost / soft)">
          <div style={{ overflowX: 'auto' }}>
            <div style={{
              display: 'grid',
              gridTemplateColumns: '56px repeat(3, auto)',
              columnGap: SP.xl,
              rowGap: SP.md,
              alignItems: 'center',
              minWidth: 380,
            }}>
              <div />
              {IB_TONES.map(t => (
                <div key={t} style={{
                  fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
                  textTransform: 'uppercase', letterSpacing: 0.5,
                }}>{t}</div>
              ))}
              {IB_SIZES.map(sz => (
                <Fragment key={sz}>
                  <div style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textSecondary }}>{sz}</div>
                  {IB_TONES.map(t => {
                    const Icon = TONE_ICON[t];
                    return (
                      <div key={t} style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
                        {IB_VARIANTS.map(vr => (
                          <IconButton
                            key={vr}
                            size={sz}
                            tone={t}
                            variant={vr}
                            title={sz + ' · ' + t + ' · ' + vr}
                          >
                            <Icon {...ICON_PROPS} size={IB_ICON_BY_SIZE[sz]} />
                          </IconButton>
                        ))}
                      </div>
                    );
                  })}
                </Fragment>
              ))}
            </div>
          </div>
          {/* active + disabled */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, marginTop: SP.md }}>
            <ButtonsStateRow label="active">
              {IB_TONES.map(t => {
                const Icon = TONE_ICON[t];
                return (
                  <IconButton key={t} tone={t} active title={'active · ' + t}>
                    <Icon {...ICON_PROPS} size={ICON_SIZE.sm} />
                  </IconButton>
                );
              })}
            </ButtonsStateRow>
            <ButtonsStateRow label="disabled">
              {IB_TONES.map(t => {
                const Icon = TONE_ICON[t];
                return (
                  <IconButton key={t} tone={t} disabled title={'disabled · ' + t}>
                    <Icon {...ICON_PROPS} size={ICON_SIZE.sm} />
                  </IconButton>
                );
              })}
            </ButtonsStateRow>
          </div>
        </SubBlock>
      </div>
    </Island>
  );
}

// Строка состояния кнопок: моноширинная метка фиксированной ширины + контент.
function ButtonsStateRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.lg, flexWrap: 'wrap' }}>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textSecondary, width: 88, flexShrink: 0 }}>{label}</div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', flex: 1, minWidth: 0 }}>
        {children}
      </div>
    </div>
  );
}

// === Секция «Палитры-данные» ======================================
// GROUP_COLORS (массив 7 цветов групп проектов) + AGENT_COLORS (Record 9 цветов
// агентов). Плашки красятся ЗНАЧЕНИЯМИ ИЗ ПАЛИТР — это данные, а не литералы
// в стиле: lint:design видит идентификатор (colors[idx] / AGENT_COLORS[key]),
// а не Literal, поэтому правило design/no-raw-color молчит. Источник hex —
// design.ts и AgentSelector.tsx, оба в списке RAW_COLOR_ALLOWED.
function DataPalettesSection() {
  // GROUP_COLORS — readonly tuple; для .map приводим к строковому массиву.
  const groupColors = GROUP_COLORS as readonly string[];
  // AGENT_COLORS — Record<string, string>; обходим через Object.entries.
  const agentEntries = Object.entries(AGENT_COLORS) as [string, string][];

  return (
    <Island>
      <IslandHeader
        icon={
          <Database
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Палитры-данные"
        badge="GROUP_COLORS · AGENT_COLORS"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* GROUP_COLORS — массив: для каждого цвета плашка + индекс + hex */}
        <SubBlock label="GROUP_COLORS — цвета групп проектов (массив, индекс)">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.md }}>
            {groupColors.map((color, idx) => (
              <div key={`gc-${idx}`} style={{
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                gap: SP.xs,
              }}>
                {/* Плашка красится значением из палитры (данные, не литерал) */}
                <div style={{
                  width: SP.xxxl,
                  height: SP.xxxl,
                  background: color,
                  borderRadius: R.md,
                }} />
                {/* Индекс массива */}
                <span style={{
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textSecondary,
                }}>
                  [{idx}]
                </span>
                {/* hex-значение из данных */}
                <span style={{
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textMuted,
                }}>
                  {color}
                </span>
              </div>
            ))}
          </div>
        </SubBlock>

        {/* AGENT_COLORS — Record: для каждого ключа плашка + имя + hex */}
        <SubBlock label="AGENT_COLORS — цвета агентов (Record, имя ключа)">
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.md }}>
            {agentEntries.map(([name, color]) => (
              <div key={`ac-${name}`} style={{
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                gap: SP.xs,
              }}>
                {/* Плашка красится значением из палитры (данные, не литерал) */}
                <div style={{
                  width: SP.xxxl,
                  height: SP.xxxl,
                  background: color,
                  borderRadius: R.md,
                }} />
                {/* Имя ключа из Record */}
                <span style={{
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textSecondary,
                }}>
                  {name}
                </span>
                {/* hex-значение из данных */}
                <span style={{
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textMuted,
                }}>
                  {color}
                </span>
              </div>
            ))}
          </div>
        </SubBlock>
      </div>
    </Island>
  );
}

// === TOC — оглавление витрины =====================================
// Два варианта одного компонента:
//  - sidebar: вертикальный список справа от контента (десктоп), sticky-top
//  - bar: горизонтальный pill-row сверху (мобила), sticky-top с горизонтальным скроллом
// Активная секция подсвечивается через IntersectionObserver; клик — плавный
// скролл к Island секции (block:'start' + scrollMarginTop на обёртке секции).
// Persist scroll-позиции между перезагрузками живёт в sidebar-варианте (он
// монтируется на десктопе), но логика одинакова — переноси в любой вариант.
function Toc({ variant }: { variant: 'sidebar' | 'bar' }) {
  const isSidebar = variant === 'sidebar';
  const [activeId, setActiveId] = useState<string>(TOC_SECTIONS[0].id);

  // Persist scroll-позиции между перезагрузками (только в одном из вариантов,
  // чтобы не дублировать слушатель): на mount восстанавливаем сохранённый
  // window.scrollY, при скролле — debounce-сейв в sessionStorage.
  // Браузерное авто-восстановление отключаем (history.scrollRestoration),
  // потому что оно не всегда отрабатывает на lazy-загружаемой странице.
  useEffect(() => {
    if (!isSidebar) return;
    const SS_KEY = 'cc_uikit_scroll';
    history.scrollRestoration = 'manual';
    const saved = sessionStorage.getItem(SS_KEY);
    if (saved) {
      const y = Number(saved);
      if (Number.isFinite(y) && y > 0) window.scrollTo(0, y);
    }
    let timer: number | undefined;
    const onScroll = () => {
      window.clearTimeout(timer);
      timer = window.setTimeout(() => {
        sessionStorage.setItem(SS_KEY, String(window.scrollY));
      }, 200);
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      window.removeEventListener('scroll', onScroll);
      window.clearTimeout(timer);
      history.scrollRestoration = 'auto';
    };
  }, [isSidebar]);

  useEffect(() => {
    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter(e => e.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
        if (visible[0]) setActiveId(visible[0].target.id);
      },
      { rootMargin: `-${STICKY_OFFSET}px 0px -55% 0px` },
    );
    TOC_SECTIONS.forEach(s => {
      const el = document.getElementById(s.id);
      if (el) observer.observe(el);
    });
    return () => observer.disconnect();
  }, []);

  const scrollTo = (id: string) => {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  // Контейнер-Island: на десктопе — без sticky (sidebar-обёртка сама sticky),
  // на мобиле — sticky-top чтобы оставался на виду при скролле секций
  return (
    <Island
      style={
        isSidebar
          ? { padding: `${SP.sm}px 0` }
          : { position: 'sticky', top: SP.md, zIndex: 10, padding: `${SP.sm}px ${SP.md}px` }
      }
    >
      <div
        style={
          isSidebar
            ? { display: 'flex', flexDirection: 'column', gap: SP.xxs }
            : { display: 'flex', gap: SP.xs, overflowX: 'auto', scrollbarWidth: 'thin' }
        }
      >
        {TOC_SECTIONS.map(s => {
          const active = s.id === activeId;
          return (
            <button
              key={s.id}
              onClick={() => scrollTo(s.id)}
              title={s.label}
              style={{
                flexShrink: 0,
                textAlign: isSidebar ? 'left' : 'center',
                padding: isSidebar ? `${SP.sm}px ${SP.md}px` : `${SP.xs}px ${SP.md}px`,
                borderRadius: R.md,
                border: `1px solid ${active ? C.accent : isSidebar ? 'transparent' : C.borderLight}`,
                background: active ? C.accentLight : 'transparent',
                color: active ? C.accent : C.textSecondary,
                fontFamily: FONT.sans,
                fontSize: FS.sm,
                fontWeight: active ? 600 : 500,
                cursor: 'pointer',
                whiteSpace: 'nowrap',
                transition: 'background 0.12s ease, color 0.12s ease, border-color 0.12s ease',
              }}
            >
              {s.label}
            </button>
          );
        })}
      </div>
    </Island>
  );
}


// Минимальные валидные Session для демо ChatCard: обязательные поля
// заполнены, опциональные — только те, что меняют визуал карточки.
const DEMO_SESSIONS: Session[] = [
  {
    id: 'demo-1',
    mode: 'plan',
    status: 'active',
    messageCount: 4,
    createdAt: '2025-01-01T10:00:00Z',
    updatedAt: '2025-01-01T10:30:00Z',
    name: 'Рефакторинг модуля авторизации',
    lastMessage: 'Готово, накатил миграцию и прогнал тесты',
    origin: 'manual',
    topic: 'refactor',
  },
  {
    id: 'demo-2',
    mode: 'default',
    status: 'working',
    messageCount: 12,
    createdAt: '2025-01-01T09:00:00Z',
    updatedAt: '2025-01-01T10:45:00Z',
    name: 'Разбор архитектуры бэкапов',
    lastMessage: 'Сейчас ищу, где лежит BackupSchema…',
    origin: 'manual',
    isPinned: true,
    topic: 'arch',
  },
  {
    id: 'demo-3',
    mode: 'default',
    status: 'waiting',
    messageCount: 1,
    createdAt: '2025-01-01T10:50:00Z',
    updatedAt: '2025-01-01T10:50:00Z',
    name: 'Новый чат',
    origin: 'manual',
  },
];

// Порядок состояний для витрины ореола: сначала светящиеся (живые, потом ошибка),
// следом спокойные. Подписи и цвета не дублируем — берём боевые таблицы
// STATUS_CONFIG / STATUS_GLOW, чтобы витрина не разъезжалась с карточкой.
const GLOW_STATES: SessionStatus[] = [
  'starting', 'working', 'waiting', 'error', 'active', 'orphaned', 'finished',
];

// Чем состояние себя ведёт — подпись под демо-карточкой
const glowBehaviour = (st: SessionStatus) => {
  const g = STATUS_GLOW[st];
  if (g.alpha === 0) return 'без свечения';
  if (!g.breath) return `ровный контур · ${g.alpha}%`;
  return `переливается${g.slow ? ' (медленно)' : ''} · ${g.alpha}%`;
};

// Демо карточки лимита: полная (аккаунты пула + сторонние провайдеры) и короткая
// (только сторонние — когда здоровых аккаунтов в пуле не осталось). resetsAt —
// «через пару часов», чтобы подпись сброса была «сегодня в HH:MM».
const DEMO_PROVIDER_LIMIT_ITEMS: Extract<ChatItem, { kind: 'provider_limit' }>[] = [
  {
    kind: 'provider_limit',
    resetsAt: new Date(Date.now() + 2 * 3600_000).toISOString(),
    providers: [
      { key: 'acc-second', displayName: 'Вторая', model: 'claude-sonnet-5', kind: 'subscription', tierLabel: 'Max 5×', utilization: 0.41 },
      { key: 'acc-third', displayName: 'Запасная', model: 'claude-sonnet-5', kind: 'subscription', tierLabel: 'Pro', utilization: 0.12 },
      { key: 'glm', displayName: 'GLM', model: 'glm-4.7' },
      { key: 'deepseek', displayName: 'DeepSeek', model: 'deepseek-chat' },
    ],
  },
  {
    kind: 'provider_limit',
    providers: [
      { key: 'glm', displayName: 'GLM', model: 'glm-4.7' },
      { key: 'deepseek', displayName: 'DeepSeek', model: 'deepseek-chat' },
    ],
  },
];

// Мета 9 панелей правой рельсы — копия PANEL_META из RightPanelStack (там
// не экспортируется). Меняется только Icon и title; контент у каждого свой.
const PANELS_DEMO: { key: string; title: string; Icon: LucideIcon; accent?: boolean }[] = [
  { key: 'plan',     title: 'План',      Icon: ClipboardList },
  { key: 'agents',   title: 'Агенты',    Icon: Bot },
  { key: 'context',  title: 'Персона',   Icon: User },
  { key: 'files',    title: 'Файлы',     Icon: FolderTree },
  { key: 'changes',  title: 'Изменения', Icon: GitCompare },
  { key: 'tasks',    title: 'Задачи',    Icon: ListTodo },
  { key: 'team',     title: 'Команда',   Icon: Users },
  { key: 'terminal', title: 'Терминал',  Icon: SquareTerminal, accent: true },
  { key: 'preview',  title: 'Сервисы',   Icon: MonitorPlay, accent: true },
];

// Четыре фоновых тона дизайн-системы (Rider Islands): холст → остров →
// утопленная зона → контент. Плашки красятся РЕАЛЬНЫМИ значениями токенов C.*,
// поэтому при смене темы видно инверсию: в светлой — остров темнее холста,
// в тёмной — светлее. Hex-значения — в theme.css, здесь только семантика.
const BG_TONES: { token: string; color: string; usage: string }[] = [
  { token: 'bgMain',  color: C.bgMain,  usage: 'Фон-холст страницы (виден в зазорах)' },
  { token: 'bgPanel', color: C.bgPanel, usage: 'Карточка-остров (дефолт Island.bg)' },
  { token: 'bgInset', color: C.bgInset, usage: 'Шапки панелей / футеры / утопленные зоны' },
  { token: 'bgWhite', color: C.bgWhite, usage: 'Контентные зоны: Файлы, Изменения, ввод' },
];

// Демо-данные для левых сайдбаров разделов. Имитируют реальные списки:
// персоны (имя + роль + цвет аватара + инициалы), базы знаний (с тегом
// Pub/Drive/Local), задачи (статус + название), файлы (дерево).
const SIDEBAR_PERSONAS = [
  { name: 'Алиса',   role: 'Аналитик',       initials: 'А', color: AGENT_COLORS.orange },
  { name: 'Борис',   role: 'Разработчик',    initials: 'Б', color: AGENT_COLORS.blue },
  { name: 'Команда', role: 'Центр памяти',   initials: 'К', color: AGENT_COLORS.purple },
];

const SIDEBAR_KNOWLEDGE_PERSONAL = [
  { name: 'Архитектура CCS', tag: 'markdown', count: 24 },
  { name: 'Гайды по API',    tag: 'docs',     count: 8 },
];

const SIDEBAR_KNOWLEDGE_PUB = [
  { name: 'Документация .NET 9', tag: 'public', count: 156 },
];

const SIDEBAR_TASKS = [
  { title: 'Поправить фильтр списка', done: false, active: true },
  { title: 'Ревью PR-142',           done: false, active: false },
  { title: 'Обновить README',         done: true,  active: false },
  { title: 'Миграция на .NET 10',     done: false, active: false },
];

// Мини-бейдж ✓/✗ для таблицы «Панель vs Остров»: зелёная галка для true,
// нейтральный минус для false (не red — false здесь не «ошибка», а факт).
function TrueFalseBadge({ value }: { value: boolean }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      width: 18, height: 18, borderRadius: '50%',
      background: value ? `${C.success}1F` : C.bgInset,
      color: value ? C.success : C.textMuted,
      fontSize: 11, fontWeight: 700, flexShrink: 0,
    }}>
      {value ? '✓' : '—'}
    </span>
  );
}

// Мини-карточка сайдбара для витрины: дескриптор сверху (название раздела +
// где используется) + контент сайдбара снизу. Дескриптор — служебный,
// в реальных разделах его нет; нужен чтобы в витрине было ясно, что перед нами.
function MiniSidebarCard({ title, where, children }: { title: string; where: string; children: React.ReactNode }) {
  return (
    <div style={{
      display: 'flex', flexDirection: 'column',
      borderRadius: R.xl, overflow: 'hidden',
      border: `1px solid ${ISLAND.border}`,
      boxShadow: SHADOW.island,
      minHeight: 240,
    }}>
      {/* Служебный дескриптор — не часть реального сайдбара */}
      <div style={{
        padding: `${SP.xs}px ${SP.sm}px`,
        background: C.bgInset,
        borderBottom: `1px solid ${C.border}`,
        display: 'flex', alignItems: 'center', gap: SP.xs,
        fontSize: FS.xs, fontFamily: FONT.mono, color: C.textMuted,
      }}>
        <span style={{ fontWeight: 700, color: C.textSecondary }}>{title}</span>
        <span style={{ flex: 1 }} />
        <span style={{ opacity: 0.7 }}>{where}</span>
      </div>
      {/* Контентная зона: здесь живёт реальный стиль сайдбара */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        {children}
      </div>
    </div>
  );
}

// Карточка базы знаний для мини-сайдбара KnowledgeList: иконка-кружок +
// название + цветной тег типа. Активная — на accentLight.
function KnowledgeRow({ kb, active }: { kb: { name: string; tag: string; count: number }; active: boolean }) {
  const tagColor = kb.tag === 'public' ? C.success : C.accent;
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md, margin: `${SP.xxs}px 0`,
      background: active ? C.accentLight : 'transparent',
      cursor: 'pointer',
    }}>
      <span style={{
        width: 24, height: 24, borderRadius: 6,
        background: active ? C.accent : C.bgInset,
        color: active ? C.onAccent : C.textSecondary,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}>
        <BookOpen size={13} strokeWidth={2} />
      </span>
      <span style={{
        flex: 1, fontSize: FS.sm, minWidth: 0,
        color: active ? C.accent : C.textPrimary,
        fontWeight: active ? 600 : 400,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {kb.name}
      </span>
      {/* Тег типа базы */}
      <span style={{
        fontSize: 9, fontWeight: 700, letterSpacing: '0.05em',
        textTransform: 'uppercase', color: tagColor,
        padding: '1px 5px', borderRadius: 3,
        background: `${tagColor}1F`,
        flexShrink: 0,
      }}>
        {kb.tag}
      </span>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>
        {kb.count}
      </span>
    </div>
  );
}

// Живой эталон слота шапки: контролы объявлены ВНУТРИ содержимого панели и
// приезжают в шапку порталом. Никаких пропов от владельца — он рендерит только
// <PanelShell><HeaderSlotDemoContent /></PanelShell>.
function HeaderSlotDemoContent() {
  const [view, setView] = useState<'list' | 'tree'>('list');
  // Шапки может не быть (мобила) — тогда контролы рисуются в теле панели
  const inHeader = useHasPanelHeader();
  const controls = (
    <>
      <IconButton size="sm" tone="accent" title="Добавить">
        <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
      </IconButton>
      <IconSegmented<'list' | 'tree'>
        value={view}
        onChange={setView}
        options={[
          { value: 'list', label: 'Списком', icon: <List size={14} strokeWidth={ICON_STROKE} /> },
          { value: 'tree', label: 'Деревом', icon: <ListTree size={14} strokeWidth={ICON_STROKE} /> },
        ]}
      />
    </>
  );
  return (
    <>
      {inHeader && <PanelHeaderSlot>{controls}</PanelHeaderSlot>}
      <div style={{ padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        {!inHeader && <div style={{ display: 'flex', gap: SP.xs }}>{controls}</div>}
        <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
          вид: {view === 'list' ? 'списком' : 'деревом'}
        </div>
        <div style={{ height: 8, borderRadius: R.sm, background: C.borderLight, width: '70%' }} />
        <div style={{ height: 8, borderRadius: R.sm, background: C.borderLight, width: '45%' }} />
      </div>
    </>
  );
}

// === Секция «Панели правой рельсы» =================================
// Все 9 панелей (План/Агенты/Персона + Файлы/Изменения/Задачи/Команда/
// Терминал/Preview) в виде мини-PanelShell — одна сетка, чтобы видеть,
// что рецепт общий: Island + IslandHeader (icon+title) + контент на C.bgWhite.
// accent=true у Терминал/Preview — их кнопки по умолчанию лежат в ящике рельсы.
function PanelsSection() {
  return (
    <Island>
      <IslandHeader
        icon={
          <Columns2
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Панели"
        badge="9 рельсы + чаты"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Концептуальная шпаргалка: панель ≠ остров. Island — это визуальная
            обёртка (атом), панель — функциональная роль (организм). Большинство
            наших панелей живут ВНУТРИ острова, но сами Island не используют. */}
        <SubBlock label="Панель vs Остров — концептуальная разница">
          <div style={{
            display: 'flex', flexDirection: 'column', gap: SP.sm,
            fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.55,
          }}>
            <div>
              <strong style={{ color: C.textHeading }}>Island</strong> — визуальный
              примитив (атом): скруглённая карточка с тенью/фоном/бордером.
              <code style={{ color: C.accent, fontFamily: FONT.mono, margin: '0 4px' }}>&lt;Island&gt;</code>
              — это div с косметикой, может содержать что угодно.
            </div>
            <div>
              <strong style={{ color: C.textHeading }}>Панель</strong> — функциональная
              роль в раскладке (сайдбар / рельса / центр). Это *роль*, не *внешний вид*.
              Может быть реализована через Island, через <code style={{ color: C.accent }}>&lt;aside&gt;</code>,
              или просто через div.
            </div>

            {/* Таблица: какие панели у нас как реализованы */}
            <div style={{
              marginTop: SP.xs,
              borderRadius: R.md,
              border: `1px solid ${C.border}`,
              overflow: 'hidden',
            }}>
              {/* Заголовок таблицы */}
              <div style={{
                display: 'grid',
                gridTemplateColumns: '1.6fr 1fr 1fr',
                background: C.bgInset,
                borderBottom: `1px solid ${C.border}`,
                fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
                fontFamily: FONT.mono, textTransform: 'uppercase', letterSpacing: '0.04em',
              }}>
                <div style={{ padding: `${SP.xs}px ${SP.sm}px` }}>Панель</div>
                <div style={{ padding: `${SP.xs}px ${SP.sm}px` }}>Сама Island?</div>
                <div style={{ padding: `${SP.xs}px ${SP.sm}px` }}>Внутри Island?</div>
              </div>
              {/* Строки таблицы */}
              {[
                { name: 'PanelShell (рельса)',     self: true,  wrap: false, note: 'остров-панель' },
                { name: 'SessionList (воркспейс)', self: false, wrap: true,  note: '' },
                { name: 'ChatList (раздел «Чаты»)', self: false, wrap: true,  note: '' },
                { name: 'PersonaList',             self: false, wrap: true,  note: '' },
                { name: 'KnowledgeList',           self: false, wrap: true,  note: '' },
                { name: 'TasksPanel',              self: false, wrap: true,  note: 'через cc-panels' },
                { name: 'ProjectSidebar',          self: false, wrap: false, note: 'свой <aside> с bgPanel' },
                { name: 'FileExplorer',            self: false, wrap: false, note: 'без обёртки' },
              ].map((row, i) => (
                <div key={row.name} style={{
                  display: 'grid',
                  gridTemplateColumns: '1.6fr 1fr 1fr',
                  background: i % 2 === 0 ? 'transparent' : C.bgInset,
                  borderBottom: i < 7 ? `1px solid ${C.borderLight}` : 'none',
                  fontSize: FS.xs,
                }}>
                  <div style={{
                    padding: `${SP.xs}px ${SP.sm}px`,
                    color: C.textPrimary,
                    display: 'flex', flexDirection: 'column', gap: 2,
                  }}>
                    <span style={{ fontWeight: 500 }}>{row.name}</span>
                    {row.note && (
                      <span style={{ fontSize: 10, color: C.textMuted, fontFamily: FONT.mono }}>
                        {row.note}
                      </span>
                    )}
                  </div>
                  <div style={{ padding: `${SP.xs}px ${SP.sm}px` }}>
                    <TrueFalseBadge value={row.self} />
                  </div>
                  <div style={{ padding: `${SP.xs}px ${SP.sm}px` }}>
                    <TrueFalseBadge value={row.wrap} />
                  </div>
                </div>
              ))}
            </div>

            <p style={{
              margin: 0, fontSize: FS.xs, color: C.textMuted,
              fontFamily: FONT.mono, lineHeight: 1.5,
            }}>
              Только <code style={{ color: C.accent }}>PanelShell</code> правой рельсы —
              настоящий остров-панель. Остальные наши панели либо обёрнуты в Island
              снаружи (IslandScaffold), либо живут сами по себе с прямым стилем.
              Island и панель — ортогональные понятия: «Island?» про внешний вид,
              «панель?» про роль в раскладке.
            </p>
          </div>
        </SubBlock>

        <SubBlock label="9 панелей — один рецепт (Island + IslandHeader + bgWhite)">
          {/* Сетка: auto-fill с minmax — 3 колонки на широком, 2 на среднем, 1 на узком.
              Каждая ячейка — мини-PanelShell с иконкой/заголовком и skeleton-контентом. */}
          <div style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
            gap: SP.md,
          }}>
            {PANELS_DEMO.map(({ key, title, Icon, accent }) => (
              <Island
                key={key}
                bg={C.bgMain}
                borderColor={ISLAND.border}
                style={{ overflow: 'hidden' }}
              >
                <IslandHeader
                  icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
                  title={title}
                />
                {/* Контентная зона: bgWhite — единая для всех панелей рельсы. */}
                <div style={{
                  minHeight: 90,
                  padding: SP.md,
                  background: C.bgWhite,
                  display: 'flex',
                  flexDirection: 'column',
                  gap: SP.xs,
                }}>
                  {/* Skeleton-строки — разной ширины, чтобы намекнуть на контент */}
                  <div style={{ height: 8, borderRadius: R.sm, background: C.borderLight, width: '70%' }} />
                  <div style={{ height: 8, borderRadius: R.sm, background: C.borderLight, width: '90%' }} />
                  <div style={{ height: 8, borderRadius: R.sm, background: C.borderLight, width: '45%' }} />
                  {/* accent=true — Терминал/Preview: их кнопки по умолчанию в ящике
                      рельсы. Подпись-признак вместо живого состояния. */}
                  {accent && (
                    <div style={{
                      marginTop: SP.xs,
                      fontSize: FS.xs,
                      fontFamily: FONT.mono,
                      color: C.textMuted,
                      padding: `${SP.xxs}px ${SP.sm}px`,
                      background: C.bgInset,
                      borderRadius: R.sm,
                      alignSelf: 'flex-start',
                    }}>
                      по умолчанию в «…»
                    </div>
                  )}
                </div>
              </Island>
            ))}
          </div>
        </SubBlock>

        {/* Контролы в шапке панели — единственный штатный механизм.
            Здесь настоящий PanelShell с настоящим PanelHeaderSlot: кнопки
            живут в коде содержимого, а видны в шапке карточки. */}
        <SubBlock label="Контролы в шапке — PanelHeaderSlot (живой)">
          <div style={{ maxWidth: 340 }}>
            <PanelShell
              icon={<FolderOpen size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
              title="Панель с контролами"
              badge="12"
              fill={false}
              animate={false}
            >
              <HeaderSlotDemoContent />
            </PanelShell>
          </div>
          <p style={{ margin: `${SP.sm}px 0 0`, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, lineHeight: 1.5 }}>
            Панель кладёт кнопки в шапку сама — <code style={{ color: C.accent }}>PanelHeaderSlot</code> телепортирует
            их порталом в ближайший <code style={{ color: C.accent }}>PanelShell</code>. Владелец экрана не участвует:
            ни пропов с готовым узлом, ни колбэков «отдай тулбар», ни window-событий.
            Нет шапки (мобила) — <code style={{ color: C.accent }}>useHasPanelHeader()</code> вернёт false,
            и панель рисует те же контролы в теле.
          </p>
        </SubBlock>

        {/* Левые сайдбары разделов — реальные стили шапок и контента.
            Каждый сайдбар узнаваем: своя шапка (без IslandHeader — его нет
            в левых списках) и характерные элементы контента. Шапка мини-острова
            с дескриптором (название раздела + где используется) — служебная,
            отделяет демо-карточки витрины от реального стиля сайдбара. */}
        <SubBlock label="Левые сайдбары разделов — реальные стили">
          <div style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))',
            gap: SP.md,
          }}>

            {/* 1. Чаты — SessionList/ChatList. Шапка: Button dashed «Новый чат»
                + упрощённый FilterBar. Контент: 2 ChatCard. */}
            <MiniSidebarCard title="Чаты" where="Chats · Workspace">
              <div style={{
                padding: '10px 12px', borderBottom: `1px solid ${C.divider}`,
                display: 'flex', alignItems: 'center', gap: 8,
              }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Button variant="dashed" size="md" fullWidth leftIcon={<Plus size={15} strokeWidth={2.2} />}>
                    Новый чат
                  </Button>
                </div>
              </div>
              {/* Упрощённый FilterBar: поиск + переключатель вида */}
              <div style={{
                padding: `${SP.xs}px ${SP.sm}px`, borderBottom: `1px solid ${C.divider}`,
                display: 'flex', gap: SP.xs, alignItems: 'center',
              }}>
                <Search size={13} strokeWidth={2} color={C.textMuted} />
                <span style={{ fontSize: FS.xs, color: C.textMuted, flex: 1 }}>Поиск…</span>
                <LayoutGrid size={13} strokeWidth={2} color={C.textMuted} />
              </div>
              <div style={{ padding: SP.sm, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                {DEMO_SESSIONS.slice(0, 2).map((s, i) => (
                  <ChatCard
                    key={s.id}
                    session={s}
                    isActive={i === 0}
                    isMobile={false}
                    fallbackName={`Чат #${i + 1}`}
                    online={true}
                    hovered={false}
                    workflowRunning={false}
                    onSelect={() => {}}
                    onHover={() => {}}
                    onDelete={() => {}}
                  />
                ))}
              </div>
            </MiniSidebarCard>

            {/* 1b. Чаты в проекте — SessionList. Тот же компонент ChatCard,
                но обёртка принудительно белая (C.bgWhite), чтобы визуально
                родниться с контентными зонами правой рельсы (Файлы/Изменения).
                Ср. с разделом «Чаты» — там обёртки нет, фон кремовый. */}
            <MiniSidebarCard title="Чаты проекта" where="Workspace · SessionList">
              <div style={{
                padding: '10px 12px', borderBottom: `1px solid ${C.divider}`,
                display: 'flex', alignItems: 'center', gap: 8,
              }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Button variant="dashed" size="md" fullWidth leftIcon={<Plus size={15} strokeWidth={2.2} />}>
                    Новый чат
                  </Button>
                </div>
              </div>
              <div style={{
                padding: `${SP.xs}px ${SP.sm}px`, borderBottom: `1px solid ${C.divider}`,
                display: 'flex', gap: SP.xs, alignItems: 'center',
              }}>
                <Search size={13} strokeWidth={2} color={C.textMuted} />
                <span style={{ fontSize: FS.xs, color: C.textMuted, flex: 1 }}>Поиск…</span>
                <LayoutGrid size={13} strokeWidth={2} color={C.textMuted} />
              </div>
              {/* БЕЛАЯ обёртка — отличие от раздела «Чаты» */}
              <div style={{
                background: C.bgWhite,
                padding: SP.sm, display: 'flex', flexDirection: 'column', gap: SP.xs,
              }}>
                {DEMO_SESSIONS.slice(0, 2).map((s, i) => (
                  <ChatCard
                    key={s.id}
                    session={s}
                    isActive={i === 0}
                    isMobile={false}
                    fallbackName={`Чат #${i + 1}`}
                    online={true}
                    hovered={false}
                    workflowRunning={false}
                    onSelect={() => {}}
                    onHover={() => {}}
                    onDelete={() => {}}
                  />
                ))}
              </div>
            </MiniSidebarCard>

            {/* 2. Проекты — ProjectSidebar. Шапка: «Все проекты» + иконка
                настроек групп. Контент: цветные маркеры групп с count. Фон —
                C.bgPanel (как в реальном ProjectSidebar.tsx). */}
            <MiniSidebarCard title="Проекты" where="ProjectListPage">
              <div style={{
                background: C.bgPanel, padding: '8px 10px 14px',
                display: 'flex', flexDirection: 'column', gap: 0,
              }}>
                {/* Row «Все проекты» */}
                <div style={{
                  display: 'flex', alignItems: 'center', gap: 11,
                  padding: '9px 11px', borderRadius: R.lg, marginBottom: 3,
                  background: 'transparent',
                }}>
                  <LayoutGrid size={15} strokeWidth={2} color={C.textSecondary} />
                  <span style={{ flex: 1, fontSize: 13.5, fontWeight: 500, color: C.textPrimary }}>Все проекты</span>
                  <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>12</span>
                </div>
                {/* «ГРУППЫ» + IconButton */}
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '13px 4px 9px' }}>
                  <span style={{
                    flex: 1, fontFamily: FONT.mono, fontSize: 10,
                    letterSpacing: '0.08em', color: C.textMuted,
                  }}>
                    ГРУППЫ
                  </span>
                  <Settings size={13} strokeWidth={2} color={C.textMuted} />
                </div>
                {/* Группы с цветными маркерами */}
                {[
                  { color: GROUP_COLORS[0], name: 'Фронтенд',   count: 3, active: true },
                  { color: GROUP_COLORS[1], name: 'Бэкенд',     count: 5, active: false },
                  { color: GROUP_COLORS[2], name: 'Личное',     count: 2, active: false },
                ].map(g => (
                  <div key={g.name} style={{
                    display: 'flex', alignItems: 'center', gap: 11,
                    padding: '9px 11px', borderRadius: R.lg, marginBottom: 3,
                    background: g.active ? C.bgWhite : 'transparent',
                    boxShadow: g.active ? SHADOW.card : 'none',
                  }}>
                    <span style={{
                      width: 4, height: 17, borderRadius: 2,
                      background: g.color, flexShrink: 0,
                    }} />
                    <span style={{
                      flex: 1, fontSize: 13.5,
                      fontWeight: g.active ? 700 : 500,
                      color: g.active ? C.textHeading : C.textPrimary,
                    }}>
                      {g.name}
                    </span>
                    <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
                      {g.count}
                    </span>
                  </div>
                ))}
              </div>
            </MiniSidebarCard>

            {/* 3. Персоны — PersonaList. Шапка: Button dashed + SegmentedControl
                mode (Глобальные/Все). Контент: аватар 32px + имя + роль. */}
            <MiniSidebarCard title="Персоны" where="PersonasPage">
              <div style={{
                padding: '10px 10px 9px', borderBottom: `1px solid ${C.border}`,
                display: 'flex', flexDirection: 'column', gap: 8,
              }}>
                <Button variant="dashed" size="md" fullWidth leftIcon={<Plus size={15} strokeWidth={2.2} />}>
                  Новая персона
                </Button>
                <SegmentedControl
                  value="global"
                  options={[
                    { value: 'global', label: 'Глобальные' },
                    { value: 'all',    label: 'Все' },
                  ]}
                  onChange={() => {}}
                />
              </div>
              <div style={{ padding: 6, display: 'flex', flexDirection: 'column', gap: 2 }}>
                {SIDEBAR_PERSONAS.map(p => {
                  const active = p.name === 'Алиса';
                  return (
                    <div key={p.name} style={{
                      width: '100%', display: 'flex', alignItems: 'center', gap: 10,
                      padding: '8px 10px', borderRadius: R.md, textAlign: 'left',
                      background: active ? C.accentMuted : 'transparent',
                    }}>
                      <span style={{
                        width: 32, height: 32, borderRadius: '50%',
                        background: p.color, color: C.onDark,
                        display: 'flex', alignItems: 'center', justifyContent: 'center',
                        fontSize: 13, fontWeight: 600, flexShrink: 0,
                      }}>
                        {p.initials}
                      </span>
                      <span style={{ flex: 1, minWidth: 0 }}>
                        <span style={{
                          display: 'block', fontSize: 13, fontWeight: 600,
                          color: C.textHeading,
                          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                        }}>
                          {p.name}
                        </span>
                        <span style={{
                          display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1,
                          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                        }}>
                          {p.role}
                        </span>
                      </span>
                    </div>
                  );
                })}
              </div>
            </MiniSidebarCard>

            {/* 4. Знания — KnowledgeList. GroupLabel «Мои»/«Публичные»
                (uppercase, letterSpacing). Карточки с цветным тегом типа. */}
            <MiniSidebarCard title="База знаний" where="KnowledgePage">
              <div style={{ padding: '8px 8px 20px' }}>
                {/* GroupLabel «Мои» */}
                <div style={{
                  fontSize: 10.5, fontWeight: 700, letterSpacing: '0.06em',
                  textTransform: 'uppercase', color: C.textMuted,
                  fontFamily: FONT.sans, padding: '8px 10px 4px',
                }}>
                  Мои
                </div>
                {SIDEBAR_KNOWLEDGE_PERSONAL.map(kb => (
                  <KnowledgeRow key={kb.name} kb={kb} active={kb.name === 'Архитектура CCS'} />
                ))}
                {/* GroupLabel «Публичные» */}
                <div style={{
                  fontSize: 10.5, fontWeight: 700, letterSpacing: '0.06em',
                  textTransform: 'uppercase', color: C.textMuted,
                  fontFamily: FONT.sans, padding: '12px 10px 4px',
                }}>
                  Публичные
                </div>
                {SIDEBAR_KNOWLEDGE_PUB.map(kb => (
                  <KnowledgeRow key={kb.name} kb={kb} active={false} />
                ))}
              </div>
            </MiniSidebarCard>

            {/* 5. Файлы — FileExplorer. Строка 22px (как в «Документации»), отступ
                12 на уровень, стрелка-Chevron поворотом, у файла — плитка расширения
                16×16 вместо иконки. Активный файл — на C.accentMuted. */}
            <MiniSidebarCard title="Файлы" where="Workspace">
              <div style={{ padding: `${SP.xs}px ${SP.xs}px`, display: 'flex', flexDirection: 'column' }}>
                {[
                  { depth: 0, kind: 'dir' as const, name: 'src', open: true },
                  { depth: 1, kind: 'ts' as const, name: 'main.ts' },
                  { depth: 1, kind: 'dir' as const, name: 'components', open: false },
                  { depth: 1, kind: 'tsx' as const, name: 'App.tsx', active: true },
                  { depth: 0, kind: 'dir' as const, name: 'docs', open: false },
                ].map(row => (
                  <div key={row.name} style={{
                    display: 'flex', alignItems: 'center', gap: 5, minHeight: 22,
                    padding: `1px ${SP.xs}px`, paddingLeft: SP.sm + row.depth * 12,
                    borderRadius: R.md,
                    background: row.active ? C.accentMuted : 'transparent',
                    boxShadow: row.active ? `inset 2px 0 0 ${C.accent}` : 'none',
                  }}>
                    <span style={{ width: 12, flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted }}>
                      {row.kind === 'dir' && (
                        <ChevronRight size={11} strokeWidth={2} style={{ transform: row.open ? 'rotate(90deg)' : 'none' }} />
                      )}
                    </span>
                    {row.kind === 'dir' ? (
                      <Folder size={14} strokeWidth={2} color={C.accent} style={{ flexShrink: 0 }} />
                    ) : (
                      <span style={{
                        width: 16, height: 16, borderRadius: 4, flexShrink: 0,
                        background: C.bgInset, color: C.textSecondary,
                        fontFamily: FONT.mono, fontSize: 7.5, fontWeight: 700,
                        display: 'flex', alignItems: 'center', justifyContent: 'center',
                      }}>{row.kind}</span>
                    )}
                    <span style={{
                      fontFamily: FONT.mono, fontSize: FS.sm,
                      fontWeight: row.kind === 'dir' ? 700 : 500,
                      color: C.textHeading,
                    }}>{row.name}</span>
                  </div>
                ))}
              </div>
            </MiniSidebarCard>

            {/* 6. Задачи — TasksPanel. Шапка: Button dashed + IconButton Funnel.
                SegmentedControl «Список|По дате|Доска». Контент: строки с
                чекбоксами; выполненные — с зачёркиванием. */}
            <MiniSidebarCard title="Задачи" where="Workspace">
              <div style={{
                padding: '8px 12px 4px', display: 'flex', gap: 7, alignItems: 'stretch',
              }}>
                <div style={{ flex: 1 }}>
                  <Button variant="dashed" size="sm" fullWidth leftIcon={<Plus size={13} strokeWidth={2.2} />}>
                    Новая задача
                  </Button>
                </div>
                <button title="Фильтр" style={{
                  width: 30, border: 'none', borderRadius: R.sm,
                  background: 'transparent', cursor: 'pointer',
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  color: C.textMuted,
                }}>
                  <Funnel size={14} strokeWidth={2} />
                </button>
              </div>
              <div style={{ padding: `${SP.xs}px ${SP.md}px ${SP.sm}px` }}>
                <SegmentedControl
                  value="list"
                  options={[
                    { value: 'list',   label: 'Список' },
                    { value: 'date',   label: 'По дате' },
                    { value: 'board',  label: 'Доска' },
                  ]}
                  onChange={() => {}}
                />
              </div>
              <div style={{ padding: `${SP.xs}px ${SP.sm}px ${SP.sm}px`, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                {SIDEBAR_TASKS.map(t => (
                  <div key={t.title} style={{
                    display: 'flex', alignItems: 'center', gap: SP.sm,
                    padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md,
                    background: t.active ? C.accentLight : 'transparent',
                  }}>
                    {/* Чекбокс */}
                    <span style={{
                      width: 14, height: 14, borderRadius: 3,
                      border: `1.5px solid ${t.done ? C.success : C.border}`,
                      background: t.done ? C.success : 'transparent',
                      display: 'flex', alignItems: 'center', justifyContent: 'center',
                      flexShrink: 0,
                    }}>
                      {t.done && <Check size={10} strokeWidth={3} color={C.onAccent} />}
                    </span>
                    <span style={{
                      flex: 1, fontSize: FS.sm,
                      color: t.done ? C.textMuted : (t.active ? C.accent : C.textPrimary),
                      fontWeight: t.active ? 600 : 400,
                      textDecoration: t.done ? 'line-through' : 'none',
                    }}>
                      {t.title}
                    </span>
                  </div>
                ))}
              </div>
            </MiniSidebarCard>

          </div>
          <p style={{
            margin: 0,
            marginTop: SP.sm,
            fontSize: FS.xs,
            color: C.textMuted,
            fontFamily: FONT.mono,
            lineHeight: 1.5,
          }}>
            7 реальных левых сайдбаров продукта: ChatList (раздел «Чаты») и
            SessionList (в проекте) — один компонент, но разные обёртки; плюс
            ProjectSidebar, PersonaList, KnowledgeList, FileExplorer, TasksPanel.
            Никто не использует <code style={{ color: C.accent }}>IslandHeader</code> —
            это прерогатива правой рельсы. Шапки — кастомные div с padding/borderBottom
            и реальными контролами (Button dashed, SegmentedControl, IconButton).
            HomePage и CalendarPage — без сайдбаров.
          </p>
        </SubBlock>

        {/* Панель чатов — отдельный вид панели (не из правой рельсы).
            Та же ChatCard, но два визуальных варианта в зависимости от раздела. */}
        <SubBlock label="Панель чатов — 2 варианта (раздел «Чаты» / воркспейс проекта)">
          <div style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
            gap: SP.md,
          }}>
            {/* Вариант 1: раздел «Чаты» — карточки лежат прямо на острове
                с дефолтным фоном (C.bgMain, кремовый). ChatList. */}
            <div>
              <div style={{
                marginBottom: SP.xs,
                fontSize: FS.xs,
                color: C.textMuted,
                fontFamily: FONT.mono,
              }}>
                ChatList — раздел «Чаты» (без обёртки, на острове)
              </div>
              <Island bg={C.bgMain} borderColor={ISLAND.border} style={{ overflow: 'hidden' }}>
                <div style={{
                  padding: SP.sm,
                  display: 'flex',
                  flexDirection: 'column',
                  gap: SP.xs,
                }}>
                  {DEMO_SESSIONS.slice(0, 2).map((s, i) => (
                    <ChatCard
                      key={s.id}
                      session={s}
                      isActive={i === 1}
                      isMobile={false}
                      fallbackName={`Чат #${i + 1}`}
                      online={true}
                      hovered={false}
                      workflowRunning={false}
                      onSelect={() => {}}
                      onHover={() => {}}
                      onDelete={() => {}}
                    />
                  ))}
                </div>
              </Island>
            </div>

            {/* Вариант 2: воркспейс проекта — карточки на белой обёртке (C.bgWhite),
                чтобы визуально родниться с контентными зонами правой рельсы. SessionList. */}
            <div>
              <div style={{
                marginBottom: SP.xs,
                fontSize: FS.xs,
                color: C.textMuted,
                fontFamily: FONT.mono,
              }}>
                SessionList — воркспейс (обёртка bgWhite)
              </div>
              <Island bg={C.bgMain} borderColor={ISLAND.border} style={{ overflow: 'hidden' }}>
                <div style={{
                  background: C.bgWhite,
                  padding: SP.sm,
                  display: 'flex',
                  flexDirection: 'column',
                  gap: SP.xs,
                }}>
                  {DEMO_SESSIONS.slice(0, 2).map((s, i) => (
                    <ChatCard
                      key={s.id}
                      session={s}
                      isActive={i === 1}
                      isMobile={false}
                      fallbackName={`Чат #${i + 1}`}
                      online={true}
                      hovered={false}
                      workflowRunning={false}
                      onSelect={() => {}}
                      onHover={() => {}}
                      onDelete={() => {}}
                    />
                  ))}
                </div>
              </Island>
            </div>
          </div>
          <p style={{
            margin: 0,
            marginTop: SP.sm,
            fontSize: FS.xs,
            color: C.textMuted,
            fontFamily: FONT.mono,
            lineHeight: 1.5,
          }}>
            Один компонент ChatCard; различие — в обёртке списка. В разделе
            «Чаты» правой рельсы проектных инструментов нет, поэтому белый
            «проектный» тон там выглядел бы лишним — оставили кремовый. В
            воркспейсе SessionList стоит рядом с Файлами/Изменениями/Задачами,
            все на C.bgWhite — поэтому обёртка принудительно белая.
          </p>
        </SubBlock>

        {/* Состояние чата в списке несёт ореол самой карточки — точки статуса в ней
            больше нет. Ниже боевой ChatCard во всех 7 состояниях: он рисует ореол сам
            по таблицам STATUS_CONFIG / STATUS_GLOW (StatusIndicator.tsx) классами
            cc-glow-* из index.css. Цвет отвечает «что происходит», переливание —
            «происходит прямо сейчас», сила (alpha) — насколько это требует внимания. */}
        <SubBlock label="ChatCard — ореол статуса, все 7 состояний">
          <Island bg={C.bgMain} borderColor={ISLAND.border} style={{ overflow: 'hidden' }}>
            <div style={{
              padding: SP.md, display: 'grid',
              gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: SP.md,
            }}>
              {GLOW_STATES.map(st => (
                <div key={st}>
                  <ChatCard
                    session={{ ...DEMO_SESSIONS[1], id: `demo-glow-${st}`, status: st, isPinned: false }}
                    isActive={false}
                    isMobile={false}
                    fallbackName="Новый чат"
                    online={true}
                    hovered={false}
                    workflowRunning={false}
                    onSelect={() => {}}
                    onHover={() => {}}
                    onDelete={() => {}}
                  />
                  <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
                    {STATUS_CONFIG[st].label} — {glowBehaviour(st)}
                  </div>
                </div>
              ))}
            </div>
          </Island>

          <p style={{ margin: `${SP.sm}px 0 0`, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, lineHeight: 1.5 }}>
            Светятся только те состояния, что требуют внимания: живые (запуск, работа,
            ожидание — у него бег медленнее) и ошибка. «Активна», «прервана» и «готово»
            не светятся вовсе — иначе список превращается в гирлянду. Цвет ауры — основной
            статусный токен из <code>STATUS_CONFIG</code> (info / accent / success / plan /
            warning / danger / textMuted): точки и ореол одного цвета, без отдельной
            насыщенной палитры. «Работает» — на <code style={{ color: C.accent }}>C.accent</code>,
            «ждёт ввода» — на <code style={{ color: C.plan }}>C.plan</code>.
            При <code>prefers-reduced-motion</code> переливание гаснет, ровный контур остаётся.
          </p>
        </SubBlock>

        {/* Карточка лимита подписки из ленты чата: секция аккаунтов пула (та же
            модель, своя предоплата) идёт перед сторонними провайдерами; когда
            здоровых аккаунтов нет — карточка выглядит как раньше. onMigrate не
            задан — кнопки демо, без реальной миграции. */}
        <SubBlock label="ProviderLimitCard — лимит исчерпан: аккаунты пула + сторонние / только сторонние">
          <div style={{
            background: C.bgWhite,
            borderRadius: R.xl,
            padding: SP.md,
            display: 'flex',
            flexDirection: 'column',
            gap: SP.sm,
          }}>
            {DEMO_PROVIDER_LIMIT_ITEMS.map((it, i) => (
              <ProviderLimitCard key={i} item={it} online={true} />
            ))}
          </div>
        </SubBlock>

        {/* Шпаргалка по 4 фоновым тонам дизайн-системы. Плашки красятся
            РЕАЛЬНЫМИ значениями C.*, поэтому при смене темы (SegmentedControl
            в шапке) видна инверсия: в светлой остров темнее холста, в тёмной
            светлее. Hex-значения обеих тем — в lib/theme.css. */}
        <SubBlock label="Фоновые тона — иерархия холст → остров → контент">
          <div style={{
            display: 'flex',
            flexWrap: 'wrap',
            gap: SP.md,
          }}>
            {BG_TONES.map(t => (
              <div key={t.token} style={{
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                gap: SP.xs,
                maxWidth: 150,
              }}>
                {/* Плашка красится РЕАЛЬНЫМ токеном — меняется с темой */}
                <div style={{
                  width: SP.xxxl,
                  height: SP.xxxl,
                  background: t.color,
                  borderRadius: R.md,
                  border: `1px solid ${C.border}`,
                }} />
                <span style={{
                  fontFamily: FONT.mono,
                  fontSize: FS.xs,
                  color: C.textSecondary,
                }}>
                  C.{t.token}
                </span>
                <span style={{
                  fontSize: FS.xs,
                  color: C.textMuted,
                  textAlign: 'center',
                  lineHeight: 1.4,
                }}>
                  {t.usage}
                </span>
              </div>
            ))}
          </div>
          <p style={{
            margin: 0,
            marginTop: SP.sm,
            fontSize: FS.xs,
            color: C.textMuted,
            fontFamily: FONT.mono,
            lineHeight: 1.5,
          }}>
            Плашки красятся реальными значениями токенов — переключайте тему в
            шапке витрины, чтобы увидеть инверсию. В светлой: bgMain (#F4F0E8)
            → bgPanel (#EDE7DC) → bgInset (#E7E0D2) → bgWhite (#FFFFFF). В тёмной
            всё перевернуто: bgMain (#201C18) → bgPanel (#272320) → bgInset
            (#1B1815) → bgWhite (#2E2A25).
          </p>
        </SubBlock>

        <p style={{
          margin: 0,
          fontSize: FS.xs,
          color: C.textMuted,
          fontFamily: FONT.mono,
          lineHeight: 1.5,
        }}>
          Сессийная группа (План/Агенты/Персона) — данные тянет из артефактов
          сессии и store персон; в витрине он пуст, поэтому плашки не видны.
          Проектные (Файлы/Изменения/Задачи/Команда/Терминал/Preview) берут
          данные из своих сервисов. Кнопки Терминал/Preview по умолчанию лежат
          в ящике рельсы («…») — как редко используемые.
        </p>
      </div>
    </Island>
  );
}

// === Секция «Шапки» ===============================================
// Верхние панели уровня экрана: HubHeader (главная шапка хаба),
// ProjectRail (док проектов второй левой рельсой воркспейса) и
// IslandHeader как атомарный паттерн шапки острова/панели рельсы.
// Живая демонстрация RailFlyout: кнопка рельсы с подписью сбоку, при action —
// ещё и с кнопкой в подписи (у дока проектов так открываются настройки).
function RailFlyoutDemo({ label, action }: { label: string; action?: boolean }) {
  const [hover, setHover] = useState(false);
  return (
    <span
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{ display: 'flex' }}
    >
      <RailFlyout
        side="left"
        label={label}
        open={hover}
        railWidth={0}
        action={action ? { Icon: Settings, title: 'Настройки проекта', onClick: () => {} } : undefined}
      >
        <IconButton size="md" ariaLabel={label}>
          {action
            ? <LayoutTemplate size={17} strokeWidth={ICON_STROKE} />
            : <ListTree size={17} strokeWidth={ICON_STROKE} />}
        </IconButton>
      </RailFlyout>
    </span>
  );
}

function HeadersSection() {
  return (
    <Island>
      <IslandHeader
        icon={
          <LayoutTemplate
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Шапки"
        badge="HubHeader · ProjectSwitcher · IslandHeader"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>

        {/* 1. HubHeader — верхняя шапка хаба на всех главных экранах
            (кроме воркспейса проекта и страницы входа). Слева — логотип,
            центр — HubTabs (Чаты/Проекты/Календарь/Заметки/Персоны + модули),
            справа — AvatarMenu и бейджи уведомлений/истории. */}
        <SubBlock label="HubHeader — верхняя шапка хаба">
          <div style={{
            display: 'flex', alignItems: 'center', gap: SP.md,
            padding: `${SP.sm}px ${SP.md}px`,
            background: C.bgPanel,
            borderRadius: R.lg,
            border: `1px solid ${C.border}`,
          }}>
            {/* Логотип слева */}
            <div style={{
              display: 'flex', alignItems: 'center', gap: SP.xs,
              flexShrink: 0,
            }}>
              <div style={{
                width: 28, height: 28, borderRadius: R.sm,
                background: C.accent, color: C.onAccent,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontWeight: 700, fontFamily: FONT.serif, fontSize: 15,
              }}>
                C
              </div>
              <span style={{
                fontFamily: FONT.serif, fontWeight: 500,
                fontSize: FS.md, color: C.textHeading,
              }}>
                Home AI
              </span>
            </div>

            {/* HubTabs по центру — 5 постоянных вкладок DEFAULT_TABS.
                Реальный активный фон — C.navInk (тёмный, «чернильный»), текст C.onNavInk.
                Вне HubHeader (variant="default") активная — на белом, но HubHeader
                использует variant="hub" → navInk. */}
            <div style={{
              flex: 1, display: 'flex', justifyContent: 'center',
              background: 'transparent',
              borderRadius: R.md,
              padding: `${SP.xxs}px ${SP.xs}px`,
              gap: SP.xxs,
            }}>
              {[
                { key: 'chats',       label: 'Чаты',     Icon: MessageCircle, active: true },
                { key: 'projects',    label: 'Проекты',  Icon: Folder },
                { key: 'calendar',    label: 'Календарь', Icon: Calendar },
                { key: 'notes',       label: 'Заметки',  Icon: Share2 },
                { key: 'personas',    label: 'Персоны',  Icon: Users },
              ].map(t => {
                const ActiveIcon = t.Icon;
                return (
                  <div key={t.key} style={{
                    display: 'flex', alignItems: 'center', gap: SP.xs,
                    padding: `${SP.xs}px ${SP.sm}px`,
                    borderRadius: R.sm,
                    background: t.active ? C.navInk : 'transparent',
                    boxShadow: t.active ? SHADOW.card : 'none',
                    fontSize: FS.sm,
                    color: t.active ? C.onNavInk : C.textSecondary,
                    fontWeight: t.active ? 600 : 400,
                    cursor: 'pointer',
                  }}>
                    <ActiveIcon size={14} strokeWidth={2} />
                    <span>{t.label}</span>
                  </div>
                );
              })}
            </div>

            {/* Справа: Bell с бейджем + аватар */}
            <div style={{
              display: 'flex', alignItems: 'center', gap: SP.sm,
              flexShrink: 0,
            }}>
              {/* Bell с бейджем непрочитанных */}
              <div style={{ position: 'relative' }}>
                <Bell size={16} strokeWidth={2} color={C.textSecondary} />
                <span style={{
                  position: 'absolute', top: -3, right: -5,
                  background: C.danger, color: C.onAccent,
                  fontSize: 9, fontWeight: 700,
                  minWidth: 14, height: 14, borderRadius: 7,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  padding: '0 3px',
                }}>3</span>
              </div>
              {/* Аватар пользователя */}
              <div style={{
                width: 28, height: 28, borderRadius: '50%',
                background: AGENT_COLORS.purple, color: C.onDark,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontWeight: 600, fontSize: 12,
                cursor: 'pointer',
              }}>
                Г
              </div>
            </div>
          </div>
          <p style={{
            margin: 0, marginTop: SP.sm,
            fontSize: FS.xs, color: C.textMuted,
            fontFamily: FONT.mono, lineHeight: 1.5,
          }}>
            Постоянные вкладки (DEFAULT_TABS): Чаты · Проекты · Календарь ·
            Заметки · Персоны. Через AvatarMenu открываются: Знания · Уведомления ·
            Аналитика · Использование · Фоновые задачи · Эксперименты · Витрина
            (dev) · «Что нового» · Сменить пароль · Внешние модули (Puzzle).
            Логотип и URL-бейдж скрыты на мобиле.
          </p>
        </SubBlock>

        {/* 2. ProjectRail — док проектов ВТОРОЙ левой рельсой (под рельсой
            панелей). Вертикальная капсула той же геометрии: «+» новый проект,
            закреплённые, недавние, поиск с «+N». Настройки активного проекта
            живут в подписи его иконки (RailFlyout), а не отдельной кнопкой. */}
        <SubBlock label="ProjectRail — док проектов (вторая левая рельса)">
          <div style={{
            width: 40, boxSizing: 'border-box',
            display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs + 2,
            paddingTop: SP.xs, paddingBottom: SP.xs,
            background: C.bgMain,
            borderTop: `1px solid ${C.border}`,
            borderBottom: `1px solid ${C.border}`,
            borderRight: `1px solid ${C.border}`,
            borderTopRightRadius: ISLAND.radius, borderBottomRightRadius: ISLAND.radius,
            boxShadow: ISLAND.shadow,
          }}>
            {/* Новый проект */}
            <div style={{
              width: 32, height: 32, borderRadius: R.md, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted,
            }}>
              <Plus size={17} strokeWidth={ICON_STROKE} />
            </div>
            <div style={{ width: 22, height: 1, background: C.border }} />

            {/* Закреплённые, затем недавние. Кнопка — IconButton md variant="media":
                картинка занимает бокс целиком, а состояние показывает сама — текущий
                проект в полном цвете, прочие до наведения приглушённые (ProjectIcon
                muted: grayscale-картинка либо бледный контур с инициалами). */}
            {[
              { initials: 'CC', color: AGENT_COLORS.blue,   active: true,  status: undefined, sepBefore: false },
              { initials: 'B',  color: AGENT_COLORS.green,  active: false, status: 'working', sepBefore: false },
              { initials: 'Д',  color: AGENT_COLORS.orange, active: false, status: 'waiting', sepBefore: true },
              { initials: 'P',  color: AGENT_COLORS.pink,   active: false, status: undefined, sepBefore: false },
            ].map(p => (
              <Fragment key={p.initials}>
                {p.sepBefore && <div style={{ width: 22, height: 2, background: C.divider, borderRadius: 1 }} />}
                <div style={{
                  width: 32, height: 32, borderRadius: R.md, cursor: 'pointer',
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  position: 'relative',
                  background: p.active ? p.color : 'transparent',
                  color: p.active ? C.onDark : C.textMuted,
                  border: p.active ? undefined : `1px solid ${C.border}`,
                  boxSizing: 'border-box',
                  fontWeight: p.active ? 700 : 600, fontSize: 12,
                }}>
                  {p.initials}
                  {/* Статус-точка: working=success / waiting=accent */}
                  {p.status && (
                    <span style={{
                      position: 'absolute', top: -2, right: -2,
                      width: 8, height: 8, borderRadius: R.full,
                      background: p.status === 'working' ? C.success : C.accent,
                      border: `2px solid ${C.bgMain}`, boxSizing: 'content-box',
                    }} />
                  )}
                </div>
              </Fragment>
            ))}

            <div style={{ width: 22, height: 1, background: C.border }} />
            {/* Поиск: кружок — сколько проектов не поместилось */}
            <div style={{
              width: 32, height: 32, borderRadius: R.md, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted,
            }}>
              <div style={{ position: 'relative', display: 'flex' }}>
                <Search size={17} strokeWidth={ICON_STROKE} />
                <span style={{
                  position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
                  borderRadius: 7, background: C.accent, color: C.onAccent,
                  fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
                }}>
                  7
                </span>
              </div>
            </div>
          </div>
          <p style={{
            margin: 0, marginTop: SP.sm,
            fontSize: FS.xs, color: C.textMuted,
            fontFamily: FONT.mono, lineHeight: 1.5,
          }}>
            Вторая капсула у левой кромки, под рельсой панелей. Порядок СТАБИЛЬНЫЙ:
            закреплённые (Pin) сверху, недавние — append-only, активный остаётся на
            своей позиции и остаётся единственным в полном цвете — прочие обесцвечены
            и возвращают цвет под курсором (там же лёгкий подъём). Статус-точки
            рисуются ПОВЕРХ кнопки, поэтому «агент ждёт» виден и у серой иконки:
            working (зелёная) / waiting (оранжевая). Вертикальный drag-and-drop —
            сторона разделителя решает пин/недавние, место вставки показывает линия
            (иконки не расступаются); правый клик — контекст-меню; что не влезло по
            высоте, уходит в «+N» на лупе. Подпись при наведении и настройки
            активного проекта — общий RailFlyout, см. блок ниже.
          </p>
        </SubBlock>

        {/* 3. RailFlyout — подпись кнопки рельсы (живой примитив). Общее поведение
            обеих рельс и дока: подпись сбоку вместо нативного title, при нужде с
            кнопкой-действием. */}
        <SubBlock label="RailFlyout — подпись кнопки рельсы (живой)">
          <div style={{
            display: 'flex', alignItems: 'center', gap: SP.xl,
            padding: `${SP.md}px ${SP.lg}px`,
            background: C.bgMain, borderRadius: R.lg, border: `1px solid ${C.border}`,
          }}>
            <RailFlyoutDemo label="Задачи" />
            <RailFlyoutDemo label="ClaudeCodeServer" action />
          </div>
          <p style={{
            margin: 0, marginTop: SP.sm,
            fontSize: FS.xs, color: C.textMuted,
            fontFamily: FONT.mono, lineHeight: 1.5,
          }}>
            Наведите на кнопки. В 40px-рельсе подписей нет места, а нативный title
            приходит с задержкой браузера и не умеет носить кнопку. Плашка —
            продолжение кнопки: та же высота, тот же тон, что у кнопки под курсором,
            примыкает вплотную, а кнопка на стыке теряет скругление и раскрывается в
            неё. Курсор доходит до действия, не теряя подсказку; у кнопок БЕЗ действия
            она гаснет сразу.
          </p>
        </SubBlock>

        {/* 4. IslandHeader — атомарный паттерн шапки острова. Используется
            в PanelShell правой рельсы и в IslandsSection витрины. */}
        <SubBlock label="IslandHeader — атомарный паттерн (правая рельса · секции витрины)">
          <Island bg={C.bgMain} borderColor={ISLAND.border} style={{ overflow: 'hidden' }}>
            <IslandHeader
              icon={<LayoutTemplate size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
              title="Заголовок острова"
              badge="3"
              actions={
                <button title="Закрыть" style={{
                  width: 26, height: 26, border: 'none', borderRadius: R.sm,
                  background: 'transparent', cursor: 'pointer',
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  color: C.textMuted,
                }}>
                  <X size={14} strokeWidth={ICON_STROKE} />
                </button>
              }
            />
            <div style={{
              background: C.bgWhite,
              padding: SP.md,
              fontSize: FS.xs, color: C.textMuted,
              fontFamily: FONT.mono, lineHeight: 1.5,
            }}>
              Контент острова — белая зона (C.bgWhite). Шапка — на тоне острова
              (C.bgMain / bgPanel), высота 40px (ISLAND.headerH).
            </div>
          </Island>
          <p style={{
            margin: 0, marginTop: SP.sm,
            fontSize: FS.xs, color: C.textMuted,
            fontFamily: FONT.mono, lineHeight: 1.5,
          }}>
            Единственный «островной» паттерн шапки в системе. Используется в
            PanelShell правой рельсы (9 панелей) и в IslandsSection этой витрины.
            Левые сайдбары его НЕ используют — у них кастомные div-шапки.
          </p>
        </SubBlock>
      </div>
    </Island>
  );
}

// === Секция «Тулбар и EmptyState» =================================
// Toolbar (контейнер) с несколькими ToolbarIconButton и одним
// ToolbarOverflowMenu (items + toggle-item + danger) — повторяет раскладку
// реальных тулбаров (HubHeader / WorkspacePage). Ниже — EmptyState в
// обёртке-карточке: icon + title + subtitle + action (Button primary).
// Стили — только токены C/FS/SP/R/ISLAND; используется общий SubBlock.
function ToolbarAndEmptySection() {
  const isMobile = useIsMobile();
  const [overflowToggle, setOverflowToggle] = useState(false);

  // Пункты overflow-меню: обычные действия + переключатель + danger.
  // Реальные тулбары собирают именно такой набор (переименовать/закрепить/
  // экспорт/уведомление/удалить).
  const toolbarActions: OverflowItem[] = [
    { key: 'rename', icon: <Pencil   size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Переименовать',  onClick: () => {} },
    { key: 'pin',    icon: <Pin      size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Закрепить',      onClick: () => {} },
    { key: 'export', icon: <Download size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Экспорт', sublabel: 'В .md', onClick: () => {} },
    { key: 'notify', label: 'Уведомлять о новых записях', toggle: overflowToggle, onClick: () => setOverflowToggle((v) => !v) },
    { key: 'delete', icon: <Trash2   size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Удалить', danger: true, onClick: () => {} },
  ];

  return (
    <Island>
      <IslandHeader
        icon={
          <Rows3
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Тулбар и EmptyState"
        badge="Toolbar · Overflow · Empty"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Toolbar: 3 ToolbarIconButton (search / bell-active / settings) +
            spacer + ToolbarOverflowMenu. noBorder + скругление, чтобы блок
            читался как демо-остров, а не как линия в разрезе экрана. */}
        <SubBlock label="Toolbar — ToolbarIconButton × 3 + ToolbarOverflowMenu">
          <Toolbar
            isMobile={isMobile}
            noBorder
            style={{ borderRadius: R.xl, background: C.bgInset }}
          >
            <ToolbarIconButton title="Поиск"><Search size={ICON_SIZE.md} strokeWidth={ICON_STROKE} /></ToolbarIconButton>
            <ToolbarIconButton title="Уведомления" active><Bell size={ICON_SIZE.md} strokeWidth={ICON_STROKE} /></ToolbarIconButton>
            <ToolbarIconButton title="Настройки"><Settings size={ICON_SIZE.md} strokeWidth={ICON_STROKE} /></ToolbarIconButton>
            <div style={{ flex: 1 }} />
            <ToolbarOverflowMenu isMobile={isMobile} title="Действия" items={toolbarActions} />
          </Toolbar>
        </SubBlock>

        {/* EmptyState: icon + title + subtitle + action (Button primary).
            Обёртка-карточка даёт пустому состоянию «пол» — как в реальных
            разделах, где EmptyState занимает тело острова. */}
        <SubBlock label="EmptyState — icon + title + subtitle + action">
          <div style={{
            background: C.bgInset,
            borderRadius: R.xl,
            padding: ISLAND.pad,
            minHeight: SP.xxxl * 5,
          }}>
            <EmptyState
              icon={<FolderOpen size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
              title="Папка пуста"
              subtitle="Перетащите файлы сюда или создайте новый — он появится в этом списке."
              action={
                <Button variant="primary" size="sm" leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>
                  Добавить файл
                </Button>
              }
            />
          </div>
        </SubBlock>
      </div>
    </Island>
  );
}
