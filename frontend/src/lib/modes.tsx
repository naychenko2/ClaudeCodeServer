// Режимы прав (permission modes) — единый источник правды для UI.
// Токены совпадают с camelCase-именами enum ClaudeMode на бэкенде
// (Models/Session.cs → ToWireToken / глобальный JsonStringEnumConverter).

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

// Штриховые монохромные иконки режимов (единый стиль с остальной иконографикой)
export function ModeIcon({ mode, size = 14 }: { mode: Mode; size?: number }) {
  const p = { width: size, height: size, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const };
  switch (mode) {
    case 'default': // вопрос в круге
      return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M9.2 9.2a3 3 0 0 1 5.6 1c0 2-2.8 2.4-2.8 2.4" /><line x1="12" y1="17.2" x2="12.01" y2="17.2" /></svg>;
    case 'acceptEdits': // карандаш
      return <svg {...p}><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4 12.5-12.5z" /></svg>;
    case 'plan': // планшет со списком
      return <svg {...p}><rect x="9" y="3" width="6" height="4" rx="1" /><path d="M9 5H7a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-2" /><path d="M9 12h6M9 16h4" /></svg>;
    case 'auto': // молния
      return <svg {...p}><path d="M13 3v7h6l-8 11v-7H5l8-11z" /></svg>;
    case 'dontAsk': // закрытый замок
      return <svg {...p}><rect x="4" y="11" width="16" height="9" rx="2" /><path d="M8 11V8a4 4 0 0 1 8 0v3" /></svg>;
    case 'bypass': // открытый замок
      return <svg {...p}><rect x="4" y="11" width="16" height="9" rx="2" /><path d="M8 11V8a4 4 0 0 1 7.5-2" /></svg>;
  }
}

// Опасный режим требует подтверждения через модалку DangerModeConfirm.
export function isDangerMode(mode: Mode): boolean {
  return !!MODE_META[mode].danger;
}
