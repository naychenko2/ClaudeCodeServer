// Инструменты пометок попапа «Редактор» (макет v3): набор инструментов холста, мини-панель
// телефона и подсказка секции.

import type { ReactNode } from 'react';
import { Brush, Eraser, Hand, MoveUpRight, SquareDashed, Trash2, Type } from 'lucide-react';
import { Button, IconButton, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
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
