import { C, FONT } from './design';

// Тема и базовые опции xterm.js — общие для терминала проекта и вьюера логов
// дев-сервера. Вынесены сюда, потому что ANSI-палитра нужна обоим, а держать две
// копии значит однажды покрасить их по-разному.
//
// Сырой hex тут неизбежен: xterm принимает только конкретные значения, CSS-переменные
// хоста до его канваса не доходят (см. RAW_COLOR_ALLOWED в eslint.config.js).
export const XTERM_THEME = {
  background: C.termBg as string,
  foreground: C.termText as string,
  cursor: C.accent as string,
  selectionBackground: C.accentMuted as string,
  black: '#2e2e2e', red: '#cc6666', green: '#93c97d', yellow: '#e0c080',
  blue: '#7fa6d6', magenta: '#c397d8', cyan: '#70c0b1', white: '#d0d0d0',
  brightBlack: '#555555', brightRed: '#d97757', brightGreen: '#b8d7a3',
  brightYellow: '#f0dfaf', brightBlue: '#a0b9d8', brightMagenta: '#d4a8d9',
  brightCyan: '#8ed0c4', brightWhite: '#e8e8e8',
};

// Опции, одинаковые у обоих вьюеров. Курсор и ввод настраиваются на месте:
// терминал интерактивный, лог — только для чтения.
export const XTERM_BASE_OPTIONS = {
  fontSize: 13,
  fontFamily: FONT.mono,
  theme: XTERM_THEME,
  allowTransparency: false,
  cols: 80,
  rows: 24,
  scrollback: 5000,
};
