import { ISLAND } from '../../lib/design';

// Фон страницы под островами (Islands): два декоративных слоя поверх bgMain —
// мягкий accent-нимб (ISLAND.glow) и дудл-паттерн в тематике продукта (терминал,
// скобки, git, чат, файлы...). Паттерн — один SVG-тайл через mask-image: цвет линий
// задаёт background-color rgba(var(--canvas-ink), α), поэтому темизация бесплатная —
// в тёмной теме тушь светлеет одной CSS-переменной, без второй картинки.
//
// Живёт на КОРНЕ страницы (за прозрачной шапкой HubHeader — фон начинается с самого
// верха окна): родитель обязан иметь position:'relative' и isolation:'isolate'
// (иначе слои zIndex:-1 провалятся под его собственный background).
// pointer-events: none — клики, сплиттеры и DnD не задеваются.

// Тайл 260×260: 12 дудлов + разбросанные мелочи, лёгкий разворот у каждого —
// рисунок «от руки», как на бумаге. stroke чёрный — в mask важна только альфа.
const DOODLE_TILE = `
<svg xmlns="http://www.w3.org/2000/svg" width="260" height="260" viewBox="0 0 260 260"
     fill="none" stroke="#000" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round">
  <g transform="translate(18,20) rotate(-7)">
    <rect x="0" y="0" width="34" height="25" rx="4"/>
    <path d="M7 9l4.5 4-4.5 4M17 17h10"/>
  </g>
  <g transform="translate(104,14) rotate(6)">
    <path d="M10 0C5 0 6 6 6 8c0 2-3 2.5-3 2.5S6 11 6 13c0 2-1 8 4 8"/>
    <path d="M22 0c5 0 4 6 4 8 0 2 3 2.5 3 2.5S26 11 26 13c0 2 1 8-4 8"/>
  </g>
  <g transform="translate(186,26) rotate(-4)">
    <circle cx="5" cy="5" r="4"/><circle cx="5" cy="27" r="4"/><circle cx="26" cy="16" r="4"/>
    <path d="M5 9v14M5 16h9c5 0 8-1.5 8 0"/>
  </g>
  <g transform="translate(30,86) rotate(5)">
    <path d="M2 4a4 4 0 014-4h20a4 4 0 014 4v12a4 4 0 01-4 4H12l-7 6v-6H6a4 4 0 01-4-4z"/>
  </g>
  <g transform="translate(112,80) rotate(-10)">
    <path d="M13 0l3.2 9.3L26 12.5l-9.8 3.2L13 25l-3.2-9.3L0 12.5l9.8-3.2z"/>
  </g>
  <g transform="translate(190,92) rotate(8)">
    <path d="M4 0h13l9 9v19a3 3 0 01-3 3H4a3 3 0 01-3-3V3a3 3 0 013-3z"/>
    <path d="M17 0v9h9M7 18h12M7 24h8"/>
  </g>
  <g transform="translate(20,168) rotate(-5)">
    <path d="M1 5a3 3 0 013-3h8l4 4h13a3 3 0 013 3v14a3 3 0 01-3 3H4a3 3 0 01-3-3z"/>
  </g>
  <g transform="translate(96,160) rotate(10)">
    <circle cx="14" cy="14" r="13"/><path d="M8.5 14.5l4 4L20 10"/>
  </g>
  <g transform="translate(176,170) rotate(-8)">
    <circle cx="13" cy="13" r="5"/>
    <path d="M13 0v4M13 22v4M0 13h4M22 13h4M3.8 3.8l2.9 2.9M19.3 19.3l2.9 2.9M22.2 3.8l-2.9 2.9M6.7 19.3l-2.9 2.9"/>
  </g>
  <g transform="translate(66,224) rotate(4)">
    <path d="M8 0L0 8l8 8M26 0l8 8-8 8"/>
  </g>
  <g transform="translate(212,214) rotate(-6)">
    <path d="M9 22h8M10 26h6M13 0a9 9 0 015 16.5V19H8v-2.5A9 9 0 0113 0z"/>
  </g>
  <g transform="translate(146,226) rotate(9)">
    <rect x="0" y="4" width="26" height="18" rx="3"/><path d="M8 4V1.5M18 4V1.5M0 10h26"/>
  </g>
</svg>`;

const DOODLE_URI = `url("data:image/svg+xml,${encodeURIComponent(DOODLE_TILE.trim())}")`;

export function CanvasBackdrop() {
  const maskProps = {
    maskImage: DOODLE_URI,
    WebkitMaskImage: DOODLE_URI,
    maskSize: `${ISLAND.patternSize} ${ISLAND.patternSize}`,
    WebkitMaskSize: `${ISLAND.patternSize} ${ISLAND.patternSize}`,
    maskRepeat: 'repeat',
    WebkitMaskRepeat: 'repeat',
  } as const;
  return (
    <div aria-hidden style={{ position: 'absolute', inset: 0, zIndex: -1, pointerEvents: 'none' }}>
      <div style={{ position: 'absolute', inset: 0, background: ISLAND.glow }} />
      <div style={{
        position: 'absolute', inset: 0,
        backgroundColor: `rgba(${ISLAND.ink}, ${ISLAND.patternAlpha})`,
        ...maskProps,
      }} />
    </div>
  );
}
