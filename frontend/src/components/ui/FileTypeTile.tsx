// Плитка типа файла — цветной квадратик с расширением, стоящий перед именем файла.
// Общий примитив для всех мест, где показывают файл списком или строкой: дерево
// «Файлов», «Документы», «Изменения», шапка просмотрщика. Раньше разметка была
// скопирована в каждое из этих мест, и габарит успел разъехаться (20px в
// «Изменениях» против 16 у остальных), а гарнитура местами прибита строкой мимо
// FONT.mono.
//
// Подписка на тему живёт ЗДЕСЬ, внутри примитива. Цвета плитки считаются в момент
// рендера (getEffectiveTheme), поэтому панель без useThemeMode оставалась со старой
// палитрой до ближайшей перерисовки по другой причине — так было в «Документах» и
// «Изменениях». Компонент подписан сам, и звать хук в панелях больше не нужно.

import { FONT } from '../../lib/design';
import { getEffectiveTheme, useThemeMode } from '../../lib/themeMode';

// Палитра типов: пастельная подложка + насыщенная буква. Значения сырые (не токены):
// это палитра-данные, а не цвета темы — оттенок кодирует ЯЗЫК файла и одинаков в обеих
// темах, тёмная получает его через hexToRgba ниже.
const EXT_META: Record<string, { bg: string; fg: string; label: string }> = {
  ts:   { bg: '#E6EEF5', fg: '#3E7CA6', label: 'ts' },
  tsx:  { bg: '#E6EEF5', fg: '#3E7CA6', label: 'tsx' },
  js:   { bg: '#FBF3D5', fg: '#B5830A', label: 'js' },
  jsx:  { bg: '#FBF3D5', fg: '#B5830A', label: 'jsx' },
  cs:   { bg: '#F0E6F5', fg: '#8E4A82', label: 'cs' },
  py:   { bg: '#E7EFF5', fg: '#3E7CA6', label: 'py' },
  json: { bg: '#FBEBE0', fg: '#C2693B', label: 'json' },
  md:   { bg: '#EFEAE0', fg: '#8A8072', label: 'md' },
  txt:  { bg: '#EFEAE0', fg: '#9A8F7E', label: 'txt' },
  html: { bg: '#FBEBE0', fg: '#C2693B', label: 'html' },
  css:  { bg: '#E6EEF5', fg: '#3E7CA6', label: 'css' },
  png:  { bg: '#F2E6F0', fg: '#8E4A82', label: 'img' },
  jpg:  { bg: '#F2E6F0', fg: '#8E4A82', label: 'img' },
  jpeg: { bg: '#F2E6F0', fg: '#8E4A82', label: 'img' },
  gif:  { bg: '#F2E6F0', fg: '#8E4A82', label: 'img' },
  webp: { bg: '#F2E6F0', fg: '#8E4A82', label: 'img' },
  svg:  { bg: '#F2E6F0', fg: '#8E4A82', label: 'svg' },
  // Документы и медиа: у них расширение говорит больше, чем имя (спека, схема, запись)
  pdf:  { bg: '#F7E3E0', fg: '#B04A3E', label: 'pdf' },
  docx: { bg: '#E4EAF6', fg: '#3B5BA5', label: 'doc' },
  xlsx: { bg: '#E3F0E6', fg: '#3E7A52', label: 'xls' },
  pptx: { bg: '#F9E7DC', fg: '#C2693B', label: 'ppt' },
  vsdx: { bg: '#E4EAF6', fg: '#3B5BA5', label: 'vsd' },
  drawio: { bg: '#FBF3D5', fg: '#B5830A', label: 'dio' },
  mp3:  { bg: '#EDE6F5', fg: '#6E58A6', label: 'mp3' },
  wav:  { bg: '#EDE6F5', fg: '#6E58A6', label: 'wav' },
  mp4:  { bg: '#E6EEF5', fg: '#3E7CA6', label: 'mp4' },
};

// Светлый hex → полупрозрачный rgba (для тёмного тонированного фона плитки)
function hexToRgba(hex: string, a: number): string {
  const h = hex.replace('#', '');
  const r = parseInt(h.slice(0, 2), 16), g = parseInt(h.slice(2, 4), 16), b = parseInt(h.slice(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

// Цвета и подпись плитки по имени файла (или пути — берётся последнее расширение)
function extMeta(name: string) {
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  const m = EXT_META[ext] ?? { bg: '#EFEAE0', fg: '#9A8F7E', label: ext.slice(0, 3) || '•' };
  // В тёмной теме светлый пастельный фон плитки заменяем на тёмный тонированный
  // того же оттенка (rgba от fg поверх тёмного фона), буква остаётся цветной
  if (getEffectiveTheme() === 'dark') return { ...m, bg: hexToRgba(m.fg, 0.18) };
  return m;
}

// Габарит плитки. Числа не из шкал: это внутренняя геометрия примитива (квадрат под
// строку списка высотой 22 и подпись в три знака), а не отступы раскладки. Держим их
// здесь — единственным местом, откуда габарит берут все панели.
const SIZE = 16;
const RADIUS = 4;
const LABEL_FS = 7.5;

/** Плитка типа файла перед его именем. `name` — имя файла либо путь. */
export function FileTypeTile({ name }: { name: string }) {
  useThemeMode();  // перекраска плитки при смене темы
  const m = extMeta(name);
  return (
    <span style={{
      width: SIZE, height: SIZE, borderRadius: RADIUS, flexShrink: 0,
      background: m.bg, color: m.fg,
      fontFamily: FONT.mono, fontSize: LABEL_FS, fontWeight: 700,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      letterSpacing: '-0.02em',
    }}>{m.label}</span>
  );
}
