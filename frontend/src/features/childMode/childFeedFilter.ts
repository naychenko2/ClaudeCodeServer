// Ф1d (ADR-041 ред. 8, разрез §14.5): белый список видов детской ленты — чистая функция,
// параметризованная surface.feedKinds (снимок ChildModeAllowList.Surface.FeedKinds,
// приходит полем childSurface в GET /api/auth/me). Семантика перенесена один в один из
// childFeed (ChildLessonChat.tsx:66-95), удалённого в Ф1g:
//  • реплики ребёнка и тьютора и успешный итог хода идут в ленту как есть;
//  • сбой хода целиком (result с ошибкой, error, rate_limit…) — плашка CHILD_TURN_ERROR_TEXT,
//    кроме отказа маршрута (action === child-route-refusal): его текст — обещание ребёнку,
//    показываем как есть через plateText (К9f, дефект 5f7854e7);
//  • ошибка отдельного инструмента плашки НЕ даёт и в ленту не идёт.
// Default-deny: вида нет в белом списке — он не показывается, включая будущие виды.
// Условие «tool_use — только widget_show без ошибки» держится на фронте: бэковый FeedKinds
// перечисляет только виды, без условия (R-Б18a: иначе ребёнок увидел бы ошибки инструментов).

import { isWidgetShow } from '../../components/chat/WidgetView';
import type { ChatItem } from '../../types';

// Сбой хода: сырой текст провайдера («Invalid API key», «проверьте API-ключ») ребёнку не показываем —
// он остаётся в логе сервера. Вместо него — один спокойный текст на «ты». Перенесён 1:1 из
// удалённого ChildLessonChat (Ф1g): штатный ChatPanel (проп childFeedKinds) рисует эту же строку.
export const CHILD_TURN_ERROR_TEXT = 'Помощник сейчас не может ответить. Попробуй чуть позже или позови взрослого.';

// Отказ маршрута (бэк ChildRoute.cs): его текст — обещание ребёнку («Занятие пока недоступно,
// взрослым отправлено уведомление.»), его показываем как есть. Узнаём по action, а не по тексту:
// формулировки живут на бэке и могут меняться (образец — isZoneRefusal, lib/personaZone.ts:86).
// plateText нет → ChatPanel рисует общую CHILD_TURN_ERROR_TEXT (рендер — К9g).
export const CHILD_ROUTE_REFUSAL_ACTION = 'child-route-refusal';

export type ChildFeedEntry = { item: ChatItem; i: number; plate: boolean; plateText?: string };

export function filterChildFeed(items: ChatItem[], feedKinds: string[]): ChildFeedEntry[] {
  const allowed = new Set(feedKinds);
  const out: ChildFeedEntry[] = [];
  items.forEach((item, i) => {
    // Вне белого списка — мимо ленты (default-deny), включая будущие виды
    if (!allowed.has(item.kind)) return;
    switch (item.kind) {
      case 'user_message':
      case 'text':
        out.push({ item, i, plate: false });
        return;
      case 'result':
        out.push({ item, i, plate: item.subtype !== 'success' });
        return;
      case 'error':
        // Отказ маршрута — обещание ребёнку: его текст идёт в плашку как есть (К9f);
        // прочие error — общая плашка без plateText.
        if (item.action === CHILD_ROUTE_REFUSAL_ACTION) {
          out.push({ item, i, plate: true, plateText: item.text });
          return;
        }
        out.push({ item, i, plate: true });
        return;
      case 'error_group':
      case 'rate_limit':
      case 'provider_limit':
      case 'model_switched':
      case 'work_loop_stopped':
      case 'hands_notice':
        out.push({ item, i, plate: true });
        return;
      case 'tool_use':
        // Виджет репетитора (схема/наглядный рисунок) — в ленту на месте своего вызова;
        // сбой любого инструмента — мимо ленты и без плашки (К8b, §8 п.5).
        if (isWidgetShow(item.name) && !item.isError) out.push({ item, i, plate: false });
        return;
      default:
        // Вид в белом списке, но здесь не описан (например file_changed) — не показывается
    }
  });
  return out;
}
