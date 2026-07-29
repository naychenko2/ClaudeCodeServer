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
  Ruler, Mail, Search,
  LayoutGrid, Columns2, Settings, X,
  MousePointerClick,
  Plus,
  Download,
  Send,
  Star, Database,
} from 'lucide-react';
import { Rows3, Bell, Pin, FolderOpen } from 'lucide-react';
import { C, FONT, FS, SP, R, SHADOW, ISLAND, MODAL_W, GROUP_COLORS } from '../lib/design';
import { AGENT_COLORS } from '../components/AgentSelector';
import { ChatCard } from '../components/ChatCard';
import { ProviderLimitCard } from '../components/chat/ChatItemView';
import type { Session, ChatItem } from '../types';
import { useThemeMode, setThemeMode, type ThemeMode } from '../lib/themeMode';
import { useIsMobile } from '../lib/breakpoints';
import { CanvasBackdrop } from '../components/ui/CanvasBackdrop';
import {
  Island, IslandHeader, SegmentedControl, Toggle, Dot,
  Button, IconButton, Modal, ModalActions, ConfirmDialog,
  Menu, MenuItem, BackButton, WaitingIndicator,
  IslandScaffold, Splitter, SidebarSplitter, IslandSplitter, IslandSidebarSplitter,
  TextField, TextArea, IconField, Field, FieldLabel,
} from '../components/ui';
import { ICON_SIZE, ICON_STROKE, ICON_PROPS } from '../components/ui/icons';
import { Toolbar, ToolbarIconButton } from '../components/Toolbar';
import { ToolbarOverflowMenu, type OverflowItem } from '../components/ToolbarOverflowMenu';
import { EmptyState } from '../components/EmptyState';
import type {
  ButtonVariant, ButtonSize,
  IconButtonSize, IconButtonTone, IconButtonVariant,
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

// Оглавление витрины: id секции (для якоря) + короткий лейбл в кнопке.
// Порядок соответствует основному flow ниже. При добавлении новой секции —
// добавь её сюда и повесь rootProps={{ id }} на её Island.
const TOC_SECTIONS: { id: string; label: string }[] = [
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
  { id: 'sec-composite',  label: 'Составные'         },
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

            {/* Секция «Составные компоненты» — ChatCard и каркас панели */}
            <div id="sec-composite" style={{ scrollMarginTop: STICKY_OFFSET }}>
              <CompositeSection />
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


// Реальные контейнеры экранов: ChatCard (карточка чата в списках) и каркас
// правой панели (Island + IslandHeader + белая контентная зона). Это уровень
// КОМПОЗИЦИИ над примитивами — показывает, как из Button/IconButton/Island
// собираются реальные блоки. В отличие от примитивов, эти компоненты тянут
// стор (personas/tasks/lastMechanic) — в витрине он пуст, поэтому состояния
// «чат персоны» и «чат-задача» здесь не видны; показаны базовые раскладки.

// Минимальные валидные Session для демо: обязательные поля заполнены,
// опциональные — только те, что меняют визуал ChatCard.
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

function CompositeSection() {
  // Hover активной карточки — чтобы видеть кнопки действий (onHover у ChatCard).
  const [hoveredId, setHoveredId] = useState<string | null>('demo-2');

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
        title="Составные компоненты"
        badge="ChatCard · каркас панели"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* ChatCard: карточка чата в боковых списках (глобальный ChatList и
            проектный SessionList — один компонент). Три состояния: обычная /
            активная+закреплённая / ожидающая. Полоса действий (правка/удаление)
            показывается по hover — наведите на любую карточку. */}
        <SubBlock label="ChatCard — карточка чата (обычная / активная / ожидающая)">
          {/* Белая подложка повторяет реальный фон списка в воркспейсе (C.bgWhite):
              так виден контраст активной полосы и теней, как в проде. */}
          <div style={{
            background: C.bgWhite,
            borderRadius: R.xl,
            padding: SP.md,
            display: 'flex',
            flexDirection: 'column',
            gap: SP.xs,
          }}>
            {DEMO_SESSIONS.map((s, i) => (
              <ChatCard
                key={s.id}
                session={s}
                isActive={i === 1}
                isMobile={false}
                fallbackName={`Чат #${i + 1}`}
                online={true}
                hovered={hoveredId === s.id}
                workflowRunning={false}
                onSelect={() => {}}
                onHover={h => setHoveredId(h ? s.id : null)}
                onEdit={() => {}}
                onDelete={() => {}}
                onTogglePin={() => {}}
              />
            ))}
          </div>
          {/* Подсказка: полные состояния (персона/задача/механика) требуют стора.
              В витрине стор пуст — поэтому плашки персоны и статуса задачи не видны. */}
          <p style={{
            margin: 0,
            fontSize: FS.xs,
            color: C.textMuted,
            fontFamily: FONT.mono,
            lineHeight: 1.5,
          }}>
            Стороны персоны/задачи/механики тянутся из store (personas/tasks/
            lastMechanic); в витрине он пуст — плашки не видны.
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

        {/* Каркас правой панели: Island + IslandHeader + белая контентная зона.
           PanelShell в RightPanelStack собран из тех же примитивов — это и есть
            «рецепт панели». Шапка на тоне острова (C.bgMain), контент — на белом,
            как у Файлов/Изменений/Задач/Терминала. */}
        <SubBlock label="Каркас панели — Island + IslandHeader + контент на C.bgWhite">
          <Island
            bg={C.bgMain}
            borderColor={ISLAND.border}
            style={{ overflow: 'hidden' }}
          >
            <IslandHeader
              icon={<Columns2 size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
              title="Панель"
              badge="3"
              actions={
                <button
                  title="Скрыть панель"
                  style={{
                    width: 26, height: 26, border: 'none', borderRadius: R.sm,
                    background: 'transparent', cursor: 'pointer',
                    display: 'flex', alignItems: 'center', justifyContent: 'center',
                    color: C.textMuted, flexShrink: 0,
                  }}
                >
                  <X size={14} strokeWidth={ICON_STROKE} />
                </button>
              }
            />
            {/* Контентная зона: C.bgWhite — отделяет рабочую область от кремовой
                шапки и фона страницы с дудл-паттерном. Здесь живут реальные
                дочерние компоненты (FileViewer, TaskBoard, GitPanel…). */}
            <div style={{
              flex: 1,
              minHeight: 120,
              display: 'flex',
              flexDirection: 'column',
              overflow: 'hidden',
              background: C.bgWhite,
              padding: SP.md,
              gap: SP.sm,
            }}>
              {/* Заглушка контента — повторяет визуальные токи реальной панели */}
              <div style={{
                height: 10, borderRadius: R.sm,
                background: C.borderLight, width: '60%',
              }} />
              <div style={{
                height: 10, borderRadius: R.sm,
                background: C.borderLight, width: '85%',
              }} />
              <div style={{
                height: 10, borderRadius: R.sm,
                background: C.borderLight, width: '40%',
              }} />
              {/* Скелтон «активной строки» — акцентный штрих */}
              <div style={{
                marginTop: SP.xs,
                display: 'flex', alignItems: 'center', gap: SP.sm,
                padding: `${SP.sm}px ${SP.md}px`,
                borderRadius: R.md,
                background: C.accentLight,
                border: `1px solid ${C.accentMuted}`,
              }}>
                <div style={{ width: 6, height: 6, borderRadius: '50%', background: C.accent }} />
                <span style={{ fontSize: FS.sm, color: C.accent, fontWeight: 600 }}>
                  Строка состояния / индикатор
                </span>
              </div>
            </div>
          </Island>
          <p style={{
            margin: 0,
            fontSize: FS.xs,
            color: C.textMuted,
            fontFamily: FONT.mono,
            lineHeight: 1.5,
          }}>
            Один рецепт для всех панелей рельсы: План / Агенты / Персона / Файлы /
            Изменения / Задачи / Команда / Терминал / Preview. Меняется только
            Icon в шапке и дочерний контент.
          </p>
        </SubBlock>

        {/* Шпаргалка: чем отличаются два уровня композиции. */}
        <SubBlock label="Различие — карточка vs панель">
          <div style={{
            display: 'flex',
            flexDirection: 'column',
            gap: SP.xs,
            fontSize: FS.sm,
            color: C.textSecondary,
            lineHeight: 1.55,
          }}>
            <div>
              <strong style={{ color: C.textHeading }}>ChatCard</strong> — компактная
              карточка-строка в боковом списке: статус точкой, название, превью
              последнего сообщения, действия по hover. Padding ~11px, ширина = колонка.
            </div>
            <div>
              <strong style={{ color: C.textHeading }}>Каркас панели</strong> — крупный
              остров-контейнер в правой рельсе: шапка с тулбаром, зона контента во
              всю высоту, DnD/ресайз. Padding ~ISLAND.pad, занимает всю высоту колонки.
            </div>
            <div>
            Оба собираются из одних примитивов (Island, IconButton, токены C), но
            на разных уровнях: ChatCard — атом списка, панель — молекула рельсы.
            </div>
          </div>
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
