// Логика мобильной раскладки трёх экранов «Специальностей» (волна 4).
//
// Назначение — единая точка правды для значений раскладки, которые в макете
// (docs/mockups/personas-specialties/mobile.html, design-notes.md §6) заданы
// для ширины 360–420px. Все три экрана (SpecialtyListView / SpecialtyRoleView
// / SpecialtyEditView) должны использовать эти значения вместо собственных
// magic-numbers: иначе на мобиле каждый экран сворачивается по-своему и
// критерии задачи (горизонтальной прокрутки нет, счётчик 1024 не обрезается,
// хвостовые чипы строки роли не сжимают имя) расходятся по экранам.
//
// Подключение:
//   import { useSpecialtiesLayout } from '...specialties/layout';
//   const layout = useSpecialtiesLayout();           // значения для текущего breakpoint
//   const desktop = getSpecialtiesLayout(false);     // десктопная раскладка
//
// Файл импортирует './specialties.mobile.css' как side-effect — это самый
// простой способ доставить стили в bundle: любой импорт из './layout'
// автоматически подтянет и CSS. Сами компоненты (<RolePresetsBlock>,
// <RolePeopleSlice>, SpecialtyListView/RoleView/EditView) импортируют
// хелперы из этого файла, поэтому CSS попадает в граф зависимостей
// продукта без отдельной правки main.tsx.

import './specialties.mobile.css';
import { useIsMobile } from '../../lib/breakpoints';

// === Тип раскладки ===
//
// Поля — все размеры/значения, которые меняются между мобилой и десктопом
// (design-notes.md §6). Не используем `as const` на значениях, потому что
// мобильный и десктопный наборы должны иметь РАЗНЫЕ литералы в одних и тех
// же полях (12 vs 16, 24 vs 28 и т.п.) — узкие типы литералов от `as const`
// мешают присвоению.
export interface SpecialtiesLayout {
  // Общая карточка (визитка роли / редактирование)
  cardPad: number;             // padding белой карточки в px
  // Заголовки
  pageTitleSize: number;       // h1.page
  pageTitleSizeMobile: number; // псевдоним для перехода с десктопа (совпадает с pageTitleSize на мобиле)
  roleTitleSize: number;       // h2.role
  roleTitleSizeMobile: number; // псевдоним
  // Hero-секция визитки/формы: значок + текст
  heroIconSize: number;
  heroIconSizeMobile: number;
  heroAlign: 'flex-start' | 'center';
  heroDirection: 'row' | 'column';
  heroTextAlign: 'left' | 'center';
  heroJustify: 'flex-start' | 'center';
  // Bigfield («Название роли» в форме) — крупный serif-инпут
  bigFieldFontSize: number;
  bigFieldFontSizeMobile: number;
  // Pill-переключатель слоёв
  pillWidth: 'auto' | '100%';
  pillFlex: string;
  pillWrap: boolean;
  // Кнопки в шапке визитки/формы
  headerGap: number;
  // Сетка «Модели по уровням» (factgrid)
  factGridMin: number;
  factGridMinMobile: number;
  // Счётчик пресета (P23, 1024)
  counterSize: number;
  // Допуск модалки-шторки (confirm): без неё — модалка центрируется
  sheetSafeArea: boolean;
}

// === Размеры десктопной раскладки ===
//
// Дублируют текущие magic-numbers из SpecialtyListView / SpecialtyRoleView /
// SpecialtyEditView, чтобы три экрана впервые начали опираться на один
// источник. Десктопные значения совпадают с мобильными «кроме точек
// перелома» design-notes.md §6, и они же остаются на ширине > 600px.
//
// Числа приведены в px (для fontSize и borderRadius) либо в unitless
// значениях, которые потребитель кладёт в style как есть.
export const SPECIALTIES_DESKTOP_LAYOUT: SpecialtiesLayout = {
  cardPad: 16,
  pageTitleSize: 28,
  pageTitleSizeMobile: 24,
  roleTitleSize: 22,
  roleTitleSizeMobile: 20,
  heroIconSize: 80,
  heroIconSizeMobile: 64,
  heroAlign: 'flex-start',
  heroDirection: 'row',
  heroTextAlign: 'left',
  heroJustify: 'flex-start',
  bigFieldFontSize: 24,
  bigFieldFontSizeMobile: 21,
  pillWidth: 'auto',
  pillFlex: '0 0 auto',
  pillWrap: false,
  headerGap: 8,
  factGridMin: 150,
  factGridMinMobile: 120,
  counterSize: 11,
  sheetSafeArea: false,
};

// === Размеры мобильной раскладки (≤ 600 px) ===
//
// Сняты с mobile.html (mobile.html:128-181) и design-notes.md §6. Единственная
// «булева» точка перелома, без диапазонов @media внутри мобильного набора —
// правила те же, что в index.html под селектором `.phone .…`, только без
// префикса, потому что на мобиле `.phone` всегда на `.stage`.
export const SPECIALTIES_MOBILE_LAYOUT: SpecialtiesLayout = {
  cardPad: 12,
  pageTitleSize: 24,
  pageTitleSizeMobile: 24,
  roleTitleSize: 20,
  roleTitleSizeMobile: 20,
  heroIconSize: 64,
  heroIconSizeMobile: 64,
  heroAlign: 'center',
  heroDirection: 'column',
  heroTextAlign: 'center',
  heroJustify: 'center',
  bigFieldFontSize: 21,
  bigFieldFontSizeMobile: 21,
  pillWidth: '100%',
  pillFlex: '1 1 auto',
  pillWrap: true,
  headerGap: 8,
  factGridMin: 120,
  factGridMinMobile: 120,
  counterSize: 11,
  sheetSafeArea: true,
};

// === Хук «раскладка под текущую ширину» ===
//
// Принимает то, что умеет `useIsMobile`, и возвращает один из двух
// «зафиксированных» наборов значений. Тип — общий (`SpecialtiesLayout`),
// поэтому потребителю не нужны тернарники на каждом поле: один объект,
// один spread в style. На SSR / первом рендере до mount `useIsMobile`
// возвращает false — мобильная раскладка появится сразу после первого
// `matchMedia`-события, без сдвигов макета «с десктопа на мобайл».
export function useSpecialtiesLayout(): SpecialtiesLayout {
  const isMobile = useIsMobile();
  return isMobile ? SPECIALTIES_MOBILE_LAYOUT : SPECIALTIES_DESKTOP_LAYOUT;
}

// === Чистая функция (для редьюсеров, селекторов, мест без хука) ===
//
// Удобно там, где нельзя позвать `useIsMobile` — например, в
// `useMemo`‑зависимостях или в местах, где ширина нужна как «входной
// параметр» (генерация className, выбор варианта Mobile/Desktop в
// резолвере). По контракту совпадает с хуком: один и тот же флаг —
// один и тот же набор.
export function getSpecialtiesLayout(isMobile: boolean): SpecialtiesLayout {
  return isMobile ? SPECIALTIES_MOBILE_LAYOUT : SPECIALTIES_DESKTOP_LAYOUT;
}

// === Префикс классов для интеграции с mobile.css ===
//
// CSS-правила в specialties.mobile.css цепляются к `.spec-*` классам.
// Компоненты добавляют их через `className` рядом со своими inline-стилями
// — никаких CSS-модулей, никакого Tailwind, всё в дизайн-системе проекта.
// Префикс зафиксирован, чтобы случайная строка из соседнего модуля не
// подцепилась к мобильным правилам.
export const SPEC_CSS_PREFIX = 'spec';
