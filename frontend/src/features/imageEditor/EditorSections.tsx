// Левая панель редактора v2 (макет image-editor-v2, «.lp»): шесть сворачиваемых секций
// с кратким итогом в заголовке. Какие секции раскрыты — запоминается на пользователя.

import { useCallback, useState, type ReactNode } from 'react';
import { Brush, Eraser, Hand, MoveUpRight, SquareDashed, Trash2, Type } from 'lucide-react';
import { Button, IconButton, SidebarSection, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import { parseSections, sectionsStorageKey, toggleSection, type SectionId, type SectionState } from './layout';
import type { Tool } from './marks';

const ic = (I: typeof Brush, size: number = ICON_SIZE.sm) => <I size={size} strokeWidth={ICON_STROKE} />;

export const TOOLS: { id: Tool; icon: typeof Brush; label: string; title: string }[] = [
  { id: 'hand', icon: Hand, label: 'Рука', title: 'Перемещать' },
  { id: 'mask', icon: Brush, label: 'Кисть', title: 'Кисть: закрасить место' },
  { id: 'arrow', icon: MoveUpRight, label: 'Стрелка', title: 'Стрелка' },
  { id: 'rect', icon: SquareDashed, label: 'Рамка', title: 'Рамка' },
  { id: 'text', icon: Type, label: 'Подпись', title: 'Подпись' },
  { id: 'eraser', icon: Eraser, label: 'Ластик', title: 'Ластик: убрать пометку' },
];

// Мини-панель поверх холста на телефоне: остальное — в шторке «Инструменты»
const MOBILE_TOOLS: Tool[] = ['hand', 'mask', 'arrow', 'eraser'];

export function useEditorSections(userId: string | null) {
  const key = sectionsStorageKey(userId);
  const [state, setState] = useState<{ key: string; open: SectionState }>(() => ({ key, open: parseSections(localStorage.getItem(key)) }));
  // Пользователь стал известен позже первого рендера — перечитываем его состояние
  const open = state.key === key ? state.open : parseSections(localStorage.getItem(key));
  const toggle = useCallback((id: SectionId) => {
    const next = toggleSection(open, id);
    localStorage.setItem(key, JSON.stringify(next));
    setState({ key, open: next });
  }, [key, open]);
  return { open, toggle };
}

export interface EditorSection {
  id: SectionId;
  title: string;
  // Краткий итог справа в заголовке: «3 пометки», «fal · Авто»
  meta?: ReactNode;
  body: ReactNode;
}

export function EditorSections({ sections, open, onToggle }: {
  sections: EditorSection[];
  open: SectionState;
  onToggle: (id: SectionId) => void;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      {sections.map(s => {
        const meta = s.meta && <SectionMeta>{s.meta}</SectionMeta>;
        return (
          <div key={s.id} data-section={s.id} style={{ padding: `${SP.xs}px ${SP.md}px ${open[s.id] ? SP.md : SP.xs}px`, borderBottom: `1px solid ${C.borderLight}` }}>
            <SidebarSection title={s.title} open={open[s.id]} onToggle={() => onToggle(s.id)} actions={meta} collapsedActions={meta}>
              {s.body}
            </SidebarSection>
          </div>
        );
      })}
    </div>
  );
}

function SectionMeta({ children }: { children: ReactNode }) {
  return (
    <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: 150 }}>
      {children}
    </span>
  );
}

export function SectionHint({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>{children}</div>;
}

// Секция «Пометки»: инструменты холста и очистка
export function MarksTools({ tool, onTool, marksCount, onClear, disabled }: {
  tool: Tool;
  onTool: (t: Tool) => void;
  marksCount: number;
  onClear: () => void;
  disabled: boolean;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: SP.xs }}>
        {TOOLS.map(t => (
          <Button key={t.id} size="sm" fullWidth variant={tool === t.id ? 'ghostAccent' : 'ghostFilled'} disabled={disabled}
            title={t.title} leftIcon={ic(t.icon, ICON_SIZE.xs)} onClick={() => onTool(t.id)}
            style={{ justifyContent: 'flex-start' }}>
            {t.label}
          </Button>
        ))}
        <Button size="sm" fullWidth variant="ghostFilled" disabled={disabled || !marksCount}
          title="Очистить пометки" leftIcon={ic(Trash2, ICON_SIZE.xs)} onClick={onClear}
          style={{ justifyContent: 'flex-start' }}>
          Очистить
        </Button>
      </div>
      <SectionHint>Кисть отмечает, что менять. Стрелка, рамка и подпись поясняют задачу — их модель тоже видит.</SectionHint>
    </div>
  );
}

export function MobileToolbar({ tool, onTool }: { tool: Tool; onTool: (t: Tool) => void }) {
  return (
    <div
      onPointerDown={e => e.stopPropagation()}
      style={{
        position: 'absolute', left: SP.sm, bottom: SP.sm, display: 'flex', gap: SP.xxs, padding: SP.xxs,
        background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
      }}
    >
      {TOOLS.filter(t => MOBILE_TOOLS.includes(t.id)).map(t => (
        <IconButton key={t.id} size="sm" active={tool === t.id} title={t.label} ariaLabel={t.label} onClick={() => onTool(t.id)}>
          {ic(t.icon)}
        </IconButton>
      ))}
    </div>
  );
}
