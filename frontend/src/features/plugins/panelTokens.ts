// Токены панели из темы хоста (S8) — ОДИН источник правды на три потребителя:
// PluginFrame (живая панель), PluginKitSection (витрина кита) и DraftCard
// (предпросмотр черновика). Раньше карта CC_TOKEN_MAP и чтец readHostTokens
// дублировались в PluginFrame и PluginKitSection (копии разъезжались молча).
//
// Панель — sandbox-iframe без allow-same-origin: --c-* хоста до неё не доходят.
// Поэтому хост читает СВОИ computed --c-* (текущая тема) и мапит в --cc-* панели.
// Единственный источник правды по палитре — тема хоста (lib/theme.css, обе темы);
// в панели нет ручной копии. Каждый --cc-* ниже есть в теме хоста (критерий S8.3).
import type { PanelTokens } from './panelSrcdoc';

// --cc-* → --c-* хоста: карта токенов.
export const CC_TOKEN_MAP: Record<string, string> = {
  '--cc-bg':            '--c-bg-card',
  '--cc-fg':            '--c-text-primary',
  '--cc-fg-muted':      '--c-text-muted',
  '--cc-accent':        '--c-accent',
  '--cc-accent-fg':     '--c-on-accent',
  '--cc-accent-soft':   '--c-accent-soft',
  '--cc-border':        '--c-border',
  '--cc-bg-inset':      '--c-bg-inset',
  '--cc-bg-panel':      '--c-bg-panel',
  '--cc-bg-selected':   '--c-bg-selected',
  '--cc-surface':       '--c-bg-white',
  '--cc-success':       '--c-success',
  '--cc-warning':       '--c-warning',
  '--cc-danger':        '--c-danger',
  '--cc-info':          '--c-info',
  '--cc-success-text':  '--c-success-text',
  '--cc-warning-text':  '--c-warning-text',
  '--cc-danger-text':   '--c-danger-text',
  '--cc-danger-border': '--c-danger-border',
};

// Чтец токенов текущей темы: getComputedStyle(<html>) несёт активную тему
// (data-theme проставляет themeMode.ts), поэтому getPropertyValue('--c-*') даёт
// значение именно текущей темы. В node-тесте (без CSS) document/getComputedStyle
// заглушены — возвращаем {} (токены не критичны для логики моста).
export function readHostTokens(): PanelTokens {
  if (typeof document === 'undefined' || typeof getComputedStyle !== 'function') return {};
  const cs = getComputedStyle(document.documentElement);
  const tokens: PanelTokens = {};
  for (const cc of Object.keys(CC_TOKEN_MAP)) {
    const v = cs.getPropertyValue(CC_TOKEN_MAP[cc]).trim();
    if (v) tokens[cc] = v;
  }
  return tokens;
}
