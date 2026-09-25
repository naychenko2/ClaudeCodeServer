// Тексты и расчёты экрана редактора: цена в единицах поставщика, имена версий,
// причины недоступности модели. Формулировки — дословно из макета image-editor-v1.

import type { ImageEditCatalog, ImageEditModel, ImageEditOp, ImageEditProvider } from '../../api/imageEditor';
import { AUTO_MODEL } from '../../api/imageEditor';

export const plural = (n: number, one: string, few: string, many: string) => {
  // Дробные суммы («1.5 кредита») согласуются по форме «few»
  if (!Number.isInteger(n)) return few;
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
};

export const variantsWord = (n: number) => `${n} ${plural(n, 'вариант', 'варианта', 'вариантов')}`;

// «$0.12» у fal, «6 кредитов» у Higgsfield
export function money(amount: number, unit: string): string {
  if (unit === 'usd') return `$${amount.toFixed(2)}`;
  const n = Math.round(amount * 100) / 100;
  return `${n} ${plural(n, 'кредит', 'кредита', 'кредитов')}`;
}

// «≈ $0.12 · 3 варианта»; сумма неизвестна — честно так и пишем
export function priceText(amount: number | null | undefined, unit: string, approx: boolean, count: number): string {
  if (amount == null) return `Цена станет известна после запуска · ${variantsWord(count)}`;
  return `${approx ? '≈ ' : ''}${money(amount, unit)} · ${variantsWord(count)}`;
}

export const providerHint = (p: ImageEditProvider) =>
  p.priceUnit === 'usd' ? 'оплата в долларах' : p.priceUnit === 'credits' ? 'оплата в кредитах' : '';

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
