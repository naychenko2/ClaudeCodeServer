// Чип общего тега на карточке чата + меню маркировки (мультивыбор чекбоксами).
// Визуал — по макету docs/mockups/chat-tags-switch.html: тонированный фон от цвета
// тега (цвет без реестра → accent), точка-индикатор, крестик удаления по hover.
import { useEffect, useState } from 'react';
import { Check, Plus, X } from 'lucide-react';
import type { ProjectTag } from '../types';
import { C, R, FONT, FS, SP } from '../lib/design';
import { Button, Menu, TextField } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { tagColor } from '../lib/tagRegistry';

// Тонированный фон чипа: прозрачность цвета тега. Цвет приходит ДАННЫМИ из реестра
// (палитра-данных GROUP_COLORS) — не тема, поэтому не через C.*; тёмная тема делит
// те же значения (конвенция макета).
function tint(color: string, alpha: number): string {
  const h = color.replace('#', '');
  const full = h.length === 3 ? h.split('').map(ch => ch + ch).join('') : h;
  const n = parseInt(full, 16);
  if (Number.isNaN(n) || full.length !== 6) return color;
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

// === Чип тега ===
export function TagChip({ name, color, onRemove, title }: {
  name: string;
  color?: string;      // из реестра; без него — accent
  onRemove?: () => void; // задан — крестик по hover (на тач hover нет: удаление через меню)
  title?: string;
}) {
  const [hover, setHover] = useState(false);
  const ink = color ?? C.accent;
  const bg = color ? tint(color, 0.15) : C.accentLight;
  return (
    <span
      title={title ?? name}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      onClick={e => e.stopPropagation()}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4,
        padding: '2px 6px', borderRadius: R.pill, flexShrink: 0,
        background: bg, color: ink,
        fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans, lineHeight: '14px',
        maxWidth: 110,
      }}
    >
      <span style={{ width: 8, height: 8, borderRadius: '50%', background: ink, flexShrink: 0 }} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
      {onRemove && (
        <button
          onClick={e => { e.stopPropagation(); onRemove(); }}
          aria-label={`Снять тег «${name}»`}
          style={{
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            width: 16, height: 16, marginRight: -2,
            border: 'none', borderRadius: R.sm, cursor: 'pointer',
            background: 'transparent', color: 'inherit', padding: 0,
            opacity: hover ? 1 : 0, transition: 'opacity 0.15s',
          }}
        >
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </button>
      )}
    </span>
  );
}

// === Тело выбора тегов: чекбоксы по реестру (цветовая точка + имя) + создание нового ===
// Переиспользуется и в поповере (TagAssignMenu, карточка чата в списке), и инлайн
// (NewChatSetup, диалог создания чата) — один способ ввода тегов на оба места.
export function TagPickerBody({ registry, selected, onToggle, onCreate, autoFocusCreate }: {
  registry: ProjectTag[];
  selected: string[];
  onToggle: (name: string) => void;
  onCreate: (name: string) => void;
  autoFocusCreate?: boolean;
}) {
  const [draft, setDraft] = useState('');
  const selectedLower = new Set(selected.map(t => t.toLowerCase()));
  const trimmed = draft.trim();
  const canCreate = trimmed.length > 0
    && !registry.some(t => t.name.toLowerCase() === trimmed.toLowerCase());

  const submit = () => {
    if (!canCreate) return;
    onCreate(trimmed);
    setDraft('');
  };

  return (
    <>
      <div style={{ maxHeight: 220, overflowY: 'auto' }} onClick={e => e.stopPropagation()}>
        {registry.length === 0 && (
          <div style={{ padding: '10px 12px', fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans }}>
            Тегов пока нет — создайте первый ниже.
          </div>
        )}
        {registry.map(t => {
          const active = selectedLower.has(t.name.toLowerCase());
          return (
            <TagMenuRow key={t.name} active={active} color={tagColor(registry, t.name)} label={t.name}
              onClick={() => onToggle(t.name)} />
          );
        })}
      </div>
      {/* Создание нового тега — добавляется в реестр и сразу назначается чату */}
      <div
        onClick={e => e.stopPropagation()}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.xs,
          marginTop: 4, paddingTop: 5, borderTop: `1px solid ${C.borderLight}`,
        }}
      >
        <TextField
          value={draft}
          onChange={setDraft}
          onEnter={submit}
          placeholder="Новый тег…"
          autoFocus={autoFocusCreate}
          style={{ padding: '6px 10px', fontSize: FS.sm }}
        />
        <Button
          size="sm"
          onClick={submit}
          disabled={!canCreate}
          title={canCreate ? 'Создать и назначить' : 'Введите новое имя тега'}
          leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        >
          Создать
        </Button>
      </div>
    </>
  );
}

// === Меню маркировки чата тегами (карточка в списке) ===
// ui/Menu в anchor-режиме (fixed по якорю кнопки): список чатов — скролл-контейнер,
// absolute-меню обрезалось бы его краями. Закрытие — клик вне (overlay Menu), Esc и
// скролл списка — здесь (поведение конкретного меню, не контрола).
const MENU_MAX_H = 300;
const MENU_W = 220;

export function TagAssignMenu({ anchor, registry, selected, onToggle, onCreate, onClose }: {
  anchor: DOMRect;          // rect кнопки-триггера
  registry: ProjectTag[];
  selected: string[];
  onToggle: (name: string) => void;
  onCreate: (name: string) => void;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    // capture: ловим скролл скролл-контейнера списка (он не всплывает до window)
    const onScroll = () => onClose();
    document.addEventListener('keydown', onKey);
    document.addEventListener('scroll', onScroll, true);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('scroll', onScroll, true);
    };
  }, [onClose]);

  return (
    <Menu onClose={onClose} anchor={anchor} maxHeight={MENU_MAX_H} minWidth={MENU_W}>
      <TagPickerBody registry={registry} selected={selected} onToggle={onToggle} onCreate={onCreate} autoFocusCreate />
    </Menu>
  );
}

// Строка тега в меню: чекбокс + цветовая точка + имя (макет «Мульти-выбор тегов»)
function TagMenuRow({ active, color, label, onClick }: {
  active: boolean;
  color?: string;
  label: string;
  onClick: () => void;
}) {
  const [hover, setHover] = useState(false);
  const ink = color ?? C.accent;
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
        padding: '8px 10px', border: 'none', borderRadius: R.md, cursor: 'pointer',
        background: hover ? C.bgSelected : active ? C.accentLight : 'transparent',
        color: active ? C.accent : C.textPrimary,
        fontFamily: FONT.sans, fontSize: FS.sm, textAlign: 'left',
      }}
    >
      <span style={{
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        width: 16, height: 16, flexShrink: 0,
        border: `2px solid ${active ? C.accent : C.border}`, borderRadius: R.sm,
        background: active ? C.accent : 'transparent',
        color: C.onAccent, transition: 'background 0.12s, border-color 0.12s',
      }}>
        {active && <Check size={11} strokeWidth={3} />}
      </span>
      <span style={{ width: 8, height: 8, borderRadius: '50%', background: ink, flexShrink: 0 }} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
    </button>
  );
}
