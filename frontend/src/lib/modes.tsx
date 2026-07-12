// Режимы прав (permission modes) — единый источник правды для UI.
// Токены совпадают с camelCase-именами enum ClaudeMode на бэкенде
// (Models/Session.cs → ToWireToken / глобальный JsonStringEnumConverter).

import { CircleHelp, SquarePen, ClipboardList, Zap, Lock, LockOpen } from 'lucide-react';

export type Mode = 'default' | 'acceptEdits' | 'plan' | 'auto' | 'dontAsk' | 'bypass';

// Порядок отображения в выпадающем списке
export const MODES: Mode[] = ['default', 'acceptEdits', 'plan', 'auto', 'dontAsk', 'bypass'];

export const MODE_META: Record<Mode, { label: string; desc: string; danger?: boolean }> = {
  default: { label: 'Спросить', desc: 'Спрашивает разрешение на каждое действие' },
  acceptEdits: { label: 'Авто-правки', desc: 'Сам применяет правки файлов и базовые команды, спрашивает на рискованном' },
  plan: { label: 'План', desc: 'Сначала показывает план, ждёт подтверждения' },
  auto: { label: 'Авто', desc: 'Действует сам, с фоновыми проверками безопасности' },
  dontAsk: { label: 'Только разрешённое', desc: 'Не спрашивает: выполняет лишь заранее одобренные инструменты' },
  bypass: { label: 'Без ограничений', desc: 'Полный обход проверок прав. Опасно — выполняется любое действие без запроса' , danger: true },
};

// Иконки режимов на базе lucide-react (Feather-стиль). Имя/сигнатура сохранены.
export function ModeIcon({ mode, size = 14 }: { mode: Mode; size?: number }) {
  const props = { size, strokeWidth: 2, style: { flexShrink: 0 } as const };
  switch (mode) {
    case 'default': return <CircleHelp {...props} />;        // вопрос в круге
    case 'acceptEdits': return <SquarePen {...props} />;     // карандаш
    case 'plan': return <ClipboardList {...props} />;        // планшет со списком
    case 'auto': return <Zap {...props} />;                  // молния
    case 'dontAsk': return <Lock {...props} />;              // закрытый замок
    case 'bypass': return <LockOpen {...props} />;           // открытый замок
  }
}

// Опасный режим требует подтверждения через модалку DangerModeConfirm.
export function isDangerMode(mode: Mode): boolean {
  return !!MODE_META[mode].danger;
}
