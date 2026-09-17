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
// Показ — своя обёртка `GlyphIcon` поверх карты loader'ов
// `dynamicIconImports`, а НЕ штатный `<DynamicIcon>`. Тот держит
// загруженный значок в собственном состоянии и грузит его в `useEffect`,
// поэтому каждая новая монтировка начинается с `fallback`: смена проекта
// перемонтирует воркспейс целиком (`key={project.id}` в App), и весь док
// проектов моргал инициалами на каждое переключение. Здесь загруженные
// значки лежат в МОДУЛЬНОМ кеше и на второй монтировке отдаются
// синхронно, ещё в первом рендере.
//
// `fallback` фиксирован **компонентом**, не элементом (§5.1): пока чанк
// не приехал — и навсегда, если имя неизвестно, — на месте значка стоит
// он, а `§7` запрещает пустой значок в любом состоянии.

// Импорт подмодуля `dynamic` (карта loader'ов + список имён).
// Путь без `.mjs` — у пакета v1.24.0 нет поля `exports`, оба
// `dynamic.mjs` и `dynamic.d.ts` лежат рядом, bundler-mode резолвит
// и значение, и тип. Там, где Node читает файл напрямую
// (vitest-сторож §5.4, генератор §5.2), путь == `lucide-react/dynamic.mjs` —
// Vite и TS используют один и тот же подпуть, но добавляют расширение
// автоматически.
import { useEffect, useState } from 'react';
import type { ComponentType, SVGProps } from 'react';
import { Icon as LucideIcon } from 'lucide-react';
import type { IconNode } from 'lucide-react';
import { dynamicIconImports, iconNames } from 'lucide-react/dynamic';

// `name` в карте loader'ов строго типизирован (literal union из 1995 имён).
// Бэк-сверка допускает `string` снаружи (любой ответ сервера), внутри
// обёртки приводим к `IconName`.
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

// Пропсы значка: обычный svg плюс lucide-евский `size`.
type GlyphSvgProps = SVGProps<SVGSVGElement> & { size?: number };

// Загруженные значки. Кеш МОДУЛЬНЫЙ, а не покомпонентный: значок один и тот
// же для всех мест показа и переживает размонтирование — иначе каждая
// монтировка начинала бы с fallback (см. шапку файла). Хранится РАЗМЕТКА
// значка (`__iconNode`), а не готовый компонент: рисует её всегда один и тот
// же `<Icon>` из lucide, поэтому смена имени не меняет тип элемента и svg
// не пересоздаётся (заодно и правило react-hooks/static-components довольно —
// компонент не берётся из хранилища во время рендера).
const loadedGlyphs = new Map<string, IconNode>();
// Идущие загрузки — чтобы десяток иконок дока с одним именем не завёл
// десяток промисов.
const pendingGlyphs = new Map<string, Promise<void>>();

// Загрузить чанк значка и положить компонент в кеш. Отказ (офлайн, промах
// кеша после выкатки) гасится: место значка остаётся за fallback (§7).
// Наружу отдана как `preloadGlyph`: прогрев значка до показа плюс точка
// входа для сторожа кеша (§5.4).
export function preloadGlyph(name: string): Promise<void> {
  if (!isLucideIconName(name)) return Promise.resolve();
  return loadGlyph(name);
}

function loadGlyph(name: string): Promise<void> {
  const already = pendingGlyphs.get(name);
  if (already) return already;
  const load = dynamicIconImports[name as IconName]()
    .then(mod => { loadedGlyphs.set(name, mod.__iconNode); })
    .catch(err => { console.error('[projectGlyphs] значок не загрузился:', name, err); })
    .finally(() => { pendingGlyphs.delete(name); });
  pendingGlyphs.set(name, load);
  return load;
}

export type GlyphIconProps = Omit<GlyphSvgProps, 'name' | 'fallback'> & {
  name: string;
  /** Компонент (НЕ JSX-элемент). Обязателен. */
  fallback: ComponentType;
};

// Показ значка по имени. Имя вне набора — сразу и навсегда `fallback`
// (тихая деградация §7), загрузка даже не начинается.
// Подмешивание `GLYPH_STROKE_PROPS` перед `...rest` позволяет вызывающему
// при желании переопределить, например, `strokeWidth` для особого
// состояния (disabled, hover).
export function GlyphIcon(props: GlyphIconProps) {
  const { name, fallback: Fallback, ...rest } = props;
  // Готовый значок берётся из кеша ПРЯМО в рендере: на повторной монтировке
  // (переключение проекта) он уже там, и промежуточного кадра с инициалами
  // не возникает вовсе. `tick` нужен только чтобы перерисоваться после
  // первой загрузки — сам компонент по-прежнему живёт в кеше, не в стейте.
  const [, setTick] = useState(0);
  const iconNode = loadedGlyphs.get(name);
  useEffect(() => {
    if (iconNode || !isLucideIconName(name)) return;
    let alive = true;
    void loadGlyph(name).then(() => { if (alive) setTick(t => t + 1); });
    return () => { alive = false; };
    // iconNode в зависимостях — ссылка из кеша, стабильная для одного имени:
    // после успешной загрузки эффект больше не перезапускается, а после
    // неудачной (офлайн) повтор не крутится в цикле — до новой монтировки.
  }, [name, iconNode]);
  if (!iconNode) return <Fallback />;
  return <LucideIcon iconNode={iconNode} {...GLYPH_STROKE_PROPS} {...rest} />;
}

