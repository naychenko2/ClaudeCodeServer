import { useLayoutEffect, type RefObject } from 'react';
import { CHAT_GUTTER_L } from './design';

// Боковое поле слева от колонки сообщений (CHAT_GUTTER_L). Прокручивается именно
// колонка сообщений — чтобы полоса шла вплотную к тексту, а не по краю широкого
// центра, — поэтому поле живёт ВНУТРИ области прокрутки, её левым паддингом.
//
// Но тогда коробка перестаёт быть симметричной: слева паддинг, справа полоса, и
// сообщения уезжают вправо от середины окна, расходясь с композером, который
// центрируется сам по себе. Разницу и снимает правый внешний отступ.
//
// Раньше это поле было жёлобом в 52px под значок ожидания («домик» с кольцами):
// значок выносили ЗА левый край сообщений отрицательным отступом, а область
// прокрутки режет всё, что вылезло за её границы, — вот жёлоб и держали. Теперь
// значок стоит в потоке (см. ChatPanel), и поле нужно только под размах колец.
//
// Ширину полосы не угадываем (она разная у систем, тем и настроек «наложением»),
// а меряем: при scrollbar-gutter: stable место под неё зарезервировано всегда,
// поэтому offsetWidth − clientWidth — величина устойчивая, а не мигающая от
// появления полосы.

// Ширина поля — размерный токен (CHAT_GUTTER_L в design.ts): от неё же там
// считается CHAT_COLUMN_W — полная горизонтальная потребность ленты, которую
// раскладка спрашивает у токенов, а не у этого модуля.
//
// Геометрия области прокрутки: ширина коробки и внешний отступ справа,
// компенсирующий поле. contentWidth — ширина колонки сообщений (CHAT_MAX_W).
export function gutterBox(contentWidth: number, scrollbarW: number): { maxWidth: number; marginRight: number } {
  return {
    // боковое поле + сама колонка + зарезервированное место под полосу
    maxWidth: contentWidth + CHAT_GUTTER_L + scrollbarW,
    // коробка шире колонки на поле слева и полосу справа; чтобы её СОДЕРЖИМОЕ
    // осталось по центру окна, коробку сдвигаем влево на половину разницы —
    // внешний отступ справа делает это ровно вдвое меньшим сдвигом
    marginRight: Math.max(0, CHAT_GUTTER_L - scrollbarW),
  };
}

// Правый паддинг ленты, когда центрировать нечего (колонка стены): коробка занимает
// всю ширину колонки, компенсировать перекос внешним отступом некуда — значит поле
// справа надо добирать паддингом. Полоса прокрутки уже съедает часть (её место
// зарезервировано scrollbar-gutter), поэтому досчитываем ровно остаток: полоса +
// паддинг = CHAT_GUTTER_L, и поля слева и справа равны при любой ширине полосы,
// включая полосы-накладки нулевой ширины.
export function gutterPadRight(scrollbarW: number): number {
  return Math.max(0, CHAT_GUTTER_L - scrollbarW);
}

// Имена переменных, которые читает разметка области прокрутки
export const VAR_W = '--cc-chat-box-w';
export const VAR_SHIFT = '--cc-chat-box-shift';
export const VAR_PAD_R = '--cc-chat-box-pad-r';

// Вешается на элемент области прокрутки. Значения ставим прямой мутацией, а не
// через состояние: они зависят от собственной ширины элемента, и проход через
// рендер давал бы лишний кадр с несмещённой колонкой.
//
// Пишем в CSS-ПЕРЕМЕННЫЕ, а не прямо в maxWidth/marginRight: те остаются за
// разметкой (на узком экране у колонки свои значения), и спор двух хозяев за
// одно свойство кончался бы тем, что при переходе на мобильную ширину уборка
// эффекта стирала бы только что выставленную React'ом ширину.
//
// Режимы: 'center' — обычный чат, лента у́же своего места и центрируется в окне;
// 'pad' — колонка стены, лента занимает всю ширину, центрировать нечего, нужен
// только равный правый паддинг; 'off' — мобила, там поля задаёт сама разметка.
export type ChatGutterMode = 'center' | 'pad' | 'off';

export function useChatGutter(ref: RefObject<HTMLDivElement | null>, contentWidth: number, mode: ChatGutterMode) {
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const clear = () => {
      el.style.removeProperty(VAR_W);
      el.style.removeProperty(VAR_SHIFT);
      el.style.removeProperty(VAR_PAD_R);
    };
    if (mode === 'off') { clear(); return; }
    const apply = () => {
      // Замер устойчив к тому, что мы сами делаем: clientWidth считается ВМЕСТЕ с
      // паддингами, поэтому offsetWidth − clientWidth = рамка + резерв под полосу и
      // от выставленного нами VAR_PAD_R не зависит — наблюдатель не гоняется за
      // собственным результатом ни в одном из режимов
      const scrollbarW = el.offsetWidth - el.clientWidth;
      if (mode === 'pad') {
        el.style.setProperty(VAR_PAD_R, `${gutterPadRight(scrollbarW)}px`);
        return;
      }
      const box = gutterBox(contentWidth, scrollbarW);
      el.style.setProperty(VAR_W, `${box.maxWidth}px`);
      el.style.setProperty(VAR_SHIFT, `${box.marginRight}px`);
    };
    apply();
    const ro = new ResizeObserver(apply);
    ro.observe(el);
    return () => { ro.disconnect(); clear(); };
  }, [ref, contentWidth, mode]);
}
