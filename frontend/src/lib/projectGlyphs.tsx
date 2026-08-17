// Значок проекта (ADR-009 §5).
//
// Источник истины — установленный пакет `lucide-react` (1.24.0). Значок
// берётся по имени из всего набора (1995 ключей карты loader'ов
// `dynamicIconImports`, ключи совпадают с множеством `iconNames`),
// а не из рукописных 89 имён. Список выдаётся наружу
// (`LUCIDE_ICON_NAMES` / `LUCIDE_ICON_NAME_SET`) — он генерируется
// импортом, а не выписан руками, и пригоден для бэк-сверки
// (vitest-сторож §5.4).
//
// Показ — `<DynamicIcon>` из `lucide-react/dynamic.mjs` через тонкую
// обёртку `GlyphIcon`. `fallback` фиксирован **компонентом**, не
// элементом: иначе §5.1 — пока чанк не приехал (и навсегда, если имя
// неизвестно), DynamicIcon рисует `null`, и `§7` запрещает пустой
// значок в любом состоянии.

// Импорт подмодуля `dynamic` (карта loader'ов + компонент DynamicIcon).
// Путь без `.mjs` — у пакета v1.24.0 нет поля `exports`, оба
// `dynamic.mjs` и `dynamic.d.ts` лежат рядом, bundler-mode резолвит
// и значение, и тип. Там, где Node читает файл напрямую
// (vitest-сторож §5.4, генератор §5.2), путь == `lucide-react/dynamic.mjs` —
// Vite и TS используют один и тот же подпуть, но добавляют расширение
// автоматически.
import type { ComponentType, ReactElement, SVGProps } from 'react';
import { DynamicIcon, iconNames } from 'lucide-react/dynamic';

// `name` и `fallback` в DynamicIcon строго типизированы: `name` — это
// ключ карты loader'ов (literal union из 1995 имён), `fallback` — функция
// без аргументов, возвращающая JSX. Бэк-сверка допускает `string`
// снаружи (любой ответ сервера), внутри обёртки приводим к `IconName`.
// `ComponentType` (React) шире, чем `() => JSX.Element` (DynamicIcon), —
// приводим к callable без аргументов.
type IconName = (typeof iconNames)[number];

// ──────────────────────────────────────────────────────────────────────────
// Список имён
// ──────────────────────────────────────────────────────────────────────────

// Полный список имён установленного lucide-react. `iconNames` собирается
// в `dynamicIconImports.mjs` как `Object.keys(dynamicIconImports)`;
// добавление/удаление имён следует за версией `lucide-react`, рукописных
// списков не ведём.
//
// Иммутабельный `readonly string[]` снаружи — изменение состава пакета
// единственный источник правды; под капотом массив тот же, что у пакета.
export const LUCIDE_ICON_NAMES: readonly string[] = iconNames;

// O(1) предикат: «входит ли имя в набор». Удобно в тех местах, где
// вызов горячий (preview, валидация ответа модели на клиенте).
export const LUCIDE_ICON_NAME_SET: ReadonlySet<string> = new Set(iconNames);

export function isLucideIconName(name: string): boolean {
  return LUCIDE_ICON_NAME_SET.has(name);
}

// ──────────────────────────────────────────────────────────────────────────
// Показ значка
// ──────────────────────────────────────────────────────────────────────────

// В один ряд с остальным UI: stroke 2, round caps, `currentColor`
// (значок красится цветом проекта снаружи). Дубликат `ICON_PROPS` из
// `components/ui/icons.ts` намеренный — обратное направление импорта
// дало бы цикл (icons.ts реэкспортирует GLYPHS отсюда).
const GLYPH_STROKE_PROPS = {
  strokeWidth: 2,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
};

// Пропсы DynamicIcon нужны для типобезопасной обёртки. Сам `DynamicIcon`
// экспортируется без TS-типов (он не из основного barrel), описываем
// минимально нужное.
type DynamicIconProps = SVGProps<SVGSVGElement> & {
  name: IconName;
  /** Компонент, не элемент: DynamicIcon делает `createElement(Fallback)`. */
  fallback?: () => ReactElement | null;
  size?: number;
};

export type GlyphIconProps = Omit<DynamicIconProps, 'name' | 'fallback'> & {
  name: string;
  /** Компонент (НЕ JSX-элемент). Обязателен. */
  fallback: ComponentType;
};

// Показ значка по имени. Если имя не в наборе — DynamicIcon пишет
// в `console.error` и остаётся на `fallback` (тихая деградация §7).
// Подмешивание `GLYPH_STROKE_PROPS` перед `...rest` позволяет вызывающему
// при желании переопределить, например, `strokeWidth` для особого
// состояния (disabled, hover).
export function GlyphIcon(props: GlyphIconProps) {
  const { name, fallback, ...rest } = props;
  return (
    <DynamicIcon
      name={name as IconName}
      fallback={fallback as () => ReactElement | null}
      {...GLYPH_STROKE_PROPS}
      {...rest}
    />
  );
}

// ──────────────────────────────────────────────────────────────────────────
// @deprecated — рукописная карта `GLYPHS`. Оставлена для бэк-совместимости
// с компонентами, которые ещё не переехали на `GlyphIcon`:
// `frontend/src/components/ui/icons.ts` (реэкспорт),
// `frontend/src/features/projects/ProjectIcon.tsx`,
// `frontend/src/features/projects/ProjectIconSection.tsx`.
// Удалится, когда фронт полностью уйдёт с `GLYPHS[name]` и IDE
// погасит все вхождения.
// ──────────────────────────────────────────────────────────────────────────

import {
  House, Sofa, Bed, Key, Wrench, Hammer, Plug, Lightbulb,
  Wallet, PiggyBank, Banknote, CreditCard, Receipt, Coins,
  ChartLine, ChartPie, ChartColumn, Table, Gauge, Target,
  Code, Terminal, GitBranch, Database, Server, Cpu, Bug, Boxes,
  Book, BookOpen, GraduationCap, Pencil, NotebookPen, Brain,
  Heart, Activity, Dumbbell, Stethoscope, Pill, Apple, Leaf,
  Utensils, Coffee, ChefHat, ShoppingCart, Cake,
  Plane, Car, TrainFront, Bike, Map, MapPin, Compass, Tent,
  Camera, Image, Film, Music, Mic, Headphones, Palette, Brush,
  Briefcase, Building2, Store, Factory, Calendar, Clock, Users,
  Rocket, Atom, FlaskConical, Microscope, Telescope,
  Gamepad2, Puzzle, Trophy, Dice5, Flag, Star, Sparkles,
  Folder, FileText, Layers, Shield, Lock, Globe, Bot, Zap,
} from 'lucide-react';

/** @deprecated Используйте `<GlyphIcon name={...} />` (§5.1). Этот объект —
 *  мост для миграции, не решение. Удалится после перевода всех потребителей
 *  (`features/projects/ProjectIcon.tsx`, `ProjectIconSection.tsx`,
 *  `components/ui/icons.ts`). */
export const GLYPHS = {
  'house': House, 'sofa': Sofa, 'bed': Bed, 'key': Key, 'wrench': Wrench, 'hammer': Hammer, 'plug': Plug, 'lightbulb': Lightbulb,
  'wallet': Wallet, 'piggy-bank': PiggyBank, 'banknote': Banknote, 'credit-card': CreditCard, 'receipt': Receipt, 'coins': Coins,
  'chart-line': ChartLine, 'chart-pie': ChartPie, 'chart-column': ChartColumn, 'table': Table, 'gauge': Gauge, 'target': Target,
  'code': Code, 'terminal': Terminal, 'git-branch': GitBranch, 'database': Database, 'server': Server, 'cpu': Cpu, 'bug': Bug, 'boxes': Boxes,
  'book': Book, 'book-open': BookOpen, 'graduation-cap': GraduationCap, 'pencil': Pencil, 'notebook-pen': NotebookPen, 'brain': Brain,
  'heart': Heart, 'activity': Activity, 'dumbbell': Dumbbell, 'stethoscope': Stethoscope, 'pill': Pill, 'apple': Apple, 'leaf': Leaf,
  'utensils': Utensils, 'coffee': Coffee, 'chef-hat': ChefHat, 'shopping-cart': ShoppingCart, 'cake': Cake,
  'plane': Plane, 'car': Car, 'train-front': TrainFront, 'bike': Bike, 'map': Map, 'map-pin': MapPin, 'compass': Compass, 'tent': Tent,
  'camera': Camera, 'image': Image, 'film': Film, 'music': Music, 'mic': Mic, 'headphones': Headphones, 'palette': Palette, 'brush': Brush,
  'briefcase': Briefcase, 'building-2': Building2, 'store': Store, 'factory': Factory, 'calendar': Calendar, 'clock': Clock, 'users': Users,
  'rocket': Rocket, 'atom': Atom, 'flask-conical': FlaskConical, 'microscope': Microscope, 'telescope': Telescope,
  'gamepad-2': Gamepad2, 'puzzle': Puzzle, 'trophy': Trophy, 'dice-5': Dice5, 'flag': Flag, 'star': Star, 'sparkles': Sparkles,
  'folder': Folder, 'file-text': FileText, 'layers': Layers, 'shield': Shield, 'lock': Lock, 'globe': Globe, 'bot': Bot, 'zap': Zap,
} as const;
