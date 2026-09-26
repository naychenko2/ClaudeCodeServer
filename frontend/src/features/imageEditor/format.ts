// Тексты и расчёты экрана редактора: цена в единицах поставщика, имена версий,
// причины недоступности модели. Формулировки — дословно из макета image-editor-v1.

import type { ImageEditCatalog, ImageEditEstimate, ImageEditModel, ImageEditOp, ImageEditProvider } from './api';
import { AUTO_MODEL } from './api';

export const plural = (n: number, one: string, few: string, many: string) => {
  // Дробные суммы («1.5 кредита») согласуются по форме «few»
  if (!Number.isInteger(n)) return few;
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
};

export const variantsWord = (n: number) => `${n} ${plural(n, 'вариант', 'варианта', 'вариантов')}`;

// Локальные модели: денег нет, вместо цены — время и очередь (ADR-018 §11)
export const isFreeUnit = (unit: string) => unit === 'free';

// «$0.12» у fal, «6 кредитов» у Higgsfield, «Бесплатно» у локальных моделей
export function money(amount: number, unit: string): string {
  if (isFreeUnit(unit)) return 'Бесплатно';
  if (unit === 'usd') return `$${amount.toFixed(2)}`;
  const n = Math.round(amount * 100) / 100;
  return `${n} ${plural(n, 'кредит', 'кредита', 'кредитов')}`;
}

// Время и очередь бесплатного запуска — из котировки; до котировки их нет
export type FreeLoad = Pick<ImageEditEstimate, 'etaSeconds' | 'queueLength'>;

// «≈ 40 с», «≈ 2 мин»
export function etaText(seconds: number): string {
  return seconds < 60 ? `≈ ${Math.max(1, Math.round(seconds))} с` : `≈ ${Math.round(seconds / 60)} мин`;
}

// «Бесплатно · ≈ 1 мин · в очереди 2»; пустая очередь не пишется, время неизвестно — «уточняется»
export function freeSum(load?: FreeLoad | null): string {
  const eta = load?.etaSeconds;
  const queue = load?.queueLength;
  return ['Бесплатно', eta != null && eta > 0 ? etaText(eta) : 'время уточняется', queue ? `в очереди ${queue}` : null]
    .filter(Boolean).join(' · ');
}

// Только сумма: «≈ $0.12»; сумма неизвестна — честно так и пишем
export function priceSum(amount: number | null | undefined, unit: string, approx: boolean, load?: FreeLoad | null): string {
  if (isFreeUnit(unit)) return freeSum(load);
  if (amount == null) return 'Цена станет известна после запуска';
  return `${approx ? '≈ ' : ''}${money(amount, unit)}`;
}

// «≈ $0.12 · 3 варианта»
export function priceText(amount: number | null | undefined, unit: string, approx: boolean, count: number, load?: FreeLoad | null): string {
  return `${priceSum(amount, unit, approx, load)} · ${variantsWord(count)}`;
}

export const providerHint = (p: ImageEditProvider) =>
  p.priceUnit === 'usd' ? 'оплата в долларах' : p.priceUnit === 'credits' ? 'оплата в кредитах'
    : isFreeUnit(p.priceUnit) ? 'бесплатно, на своей видеокарте' : '';

// Операции поставщика — объединение caps его моделей; null — у какой-то модели caps нет,
// и честно сказать, чего поставщик не умеет, нельзя
export function providerOps(p: ImageEditProvider): ImageEditOp[] | null {
  const ops = new Set<ImageEditOp>();
  for (const m of p.models) {
    if (m.id === AUTO_MODEL) continue;
    if (!m.caps) return null;
    m.caps.ops.forEach(op => ops.add(op));
  }
  return [...ops];
}

// Операция по состоянию холста: с нуля — генерация, кисть — инпейнт, иначе правка
export function pickOp(hasImage: boolean, hasMask: boolean): ImageEditOp {
  if (!hasImage) return 'generate';
  return hasMask ? 'inpaint' : 'edit';
}

// Запрос просит стереть отмеченное — копия EditIntent.IsRemoval на сервере, менять вместе.
// Нужен котировке: чистый инпейнт (FLUX Fill) удалять не умеет, сервер берёт другую модель
const REMOVAL = /(?<!\p{L})(удал|убер|убра|сотр|стер|стира|избав|remove|erase|delete|get rid)/iu;
const OTHER_ACTION = /(?<!\p{L})(добав|замен|встав|нарисуй|дорисуй|постав|полож|сдела|превра|перекрас|add|replace|insert|put|draw|turn|make)/iu;
export const isRemovalPrompt = (prompt: string) => REMOVAL.test(prompt) && !OTHER_ACTION.test(prompt);

// Почему модель недоступна для текущей задачи (пусто — доступна). «Авто» подбирается
// сервером и недоступной не бывает.
export function modelBlockReason(m: ImageEditModel, hasImage: boolean, hasMask: boolean): string {
  if (m.id === AUTO_MODEL || !m.caps) return '';
  // Модель одного быстрого действия (FaceDetailer) промптом не запускается
  if (m.caps.ops.length && m.caps.ops.every(op => op === 'enhanceFaces')) return 'Запускается кнопкой «Улучшить лица» в быстрых действиях';
  if (!hasImage && !m.caps.ops.includes('generate')) return 'Только правит готовую картинку — сначала загрузите её';
  if (hasImage && hasMask && m.caps.mask === 'none') return 'Не правит по маске — сотрите кисть или возьмите другую модель';
  return '';
}

export type ProviderChoice = 'settings' | string;

// Поставщик, которым реально рисуем: «как в настройках» — умолчание админа
export function effectiveProvider(catalog: ImageEditCatalog, choice: ProviderChoice): ImageEditProvider | null {
  const key = choice === 'settings' ? catalog.default.provider : choice;
  return catalog.providers.find(p => p.key === key) ?? catalog.providers[0] ?? null;
}

// hero.png → hero.v2.png, hero.v2.png → hero.v3.png (суффикс снимается перед номером)
export function nextVersionName(fileName: string): string {
  const m = fileName.match(/^(.*?)(?:\.v(\d+))?\.(\w+)$/);
  if (!m) return `${fileName}.v2.png`;
  return `${m[1]}.v${Number(m[2] || 1) + 1}.${m[3]}`;
}

export const splitPath = (path: string) => {
  const i = path.lastIndexOf('/');
  return i < 0 ? { folder: '', name: path } : { folder: path.slice(0, i), name: path.slice(i + 1) };
};

const EDITABLE = /\.(png|jpe?g|webp)$/i;
// «Редактировать» есть только у растровых картинок: SVG редактор не открывает
export const isEditableImage = (path: string) => EDITABLE.test(path);
