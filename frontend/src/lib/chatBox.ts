import { useLayoutEffect, type RefObject } from 'react';

// Геометрия области прокрутки чата.
//
// Прокручивается НЕ вся ширина центра, а колонка сообщений: иначе полоса рисуется
// по краю широкого центра и на большом экране висит в сотне пикселей от текста.
// Значит место под полосу зарезервировано справа ВНУТРИ коробки — и коробка
// перестаёт быть симметричной: слева колонка упирается в край, справа между ней и
// краем стоит полоса. Колонка съезжает влево от середины окна и расходится с
// композером, который центрируется сам по себе.
//
// Лечится равным паддингом слева: полоса справа, столько же пустого слева —
// колонка снова ровно посередине коробки, а коробка ровно посередине окна.
//
// Ширину полосы не угадываем (она разная у систем, тем и настроек «наложением»),
// а меряем: при scrollbar-gutter: stable место под неё зарезервировано всегда,
// поэтому offsetWidth − clientWidth — величина устойчивая, а не мигающая от
// появления полосы. Заранее и с запасом та же величина живёт в design.ts как
// CHAT_SCROLLBAR_W — раскладке (useCenterOffset) полная потребность ленты нужна
// ДО замеров.
//
// contentWidth — ширина колонки сообщений (CHAT_MAX_W).
export function chatBox(contentWidth: number, scrollbarW: number): { maxWidth: number; paddingLeft: number } {
  return {
    // отступ слева + сама колонка + зарезервированное место под полосу
    maxWidth: contentWidth + scrollbarW * 2,
    paddingLeft: scrollbarW,
  };
}

// Имена переменных, которые читает разметка области прокрутки
export const VAR_W = '--cc-chat-box-w';
export const VAR_PAD = '--cc-chat-box-pad';

// Вешается на элемент области прокрутки. Значения ставим прямой мутацией, а не
// через состояние: они зависят от собственной ширины элемента, и проход через
// рендер давал бы лишний кадр с несмещённой колонкой.
//
// Пишем в CSS-ПЕРЕМЕННЫЕ, а не прямо в maxWidth/paddingLeft: те остаются за
// разметкой (на узком экране у колонки свои значения), и спор двух хозяев за
// одно свойство кончался бы тем, что при переходе на мобильную ширину уборка
// эффекта стирала бы только что выставленную React'ом ширину.
export function useChatBox(ref: RefObject<HTMLDivElement | null>, contentWidth: number, enabled: boolean) {
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const clear = () => { el.style.removeProperty(VAR_W); el.style.removeProperty(VAR_PAD); };
    if (!enabled) { clear(); return; }
    const apply = () => {
      const box = chatBox(contentWidth, el.offsetWidth - el.clientWidth);
      // Идемпотентно: после установки ширины замер полосы не меняется, поэтому
      // наблюдатель не гоняется за собственным результатом
      el.style.setProperty(VAR_W, `${box.maxWidth}px`);
      el.style.setProperty(VAR_PAD, `${box.paddingLeft}px`);
    };
    apply();
    const ro = new ResizeObserver(apply);
    ro.observe(el);
    return () => { ro.disconnect(); clear(); };
  }, [ref, contentWidth, enabled]);
}
