// Чистая логика виджета «Стена» на дашборде: счётчик мест, видимость кнопки
// автосбора и перевод статуса чата в точку. Вынесено из компонента, потому что
// рендер-тестов в проекте нет (vitest c environment: 'node', без jsdom), а
// проверять эти правила надо — они продуктовые, а не оформительские.
import type { Session } from '../../types';
import type { ActivityStatus } from '../../lib/projectActivity';
import { MAX_CHATS } from '../wall/wallStore';

export interface WallWidgetView {
  // «3 из 5» — сколько колонок собрано из потолка набора
  counterText: string;
  // Набор пуст: показываем объяснение, что такое стена
  empty: boolean;
  // Есть и свободные места, и кандидаты — предлагаем автосбор
  showSuggest: boolean;
  suggestCount: number;
  freeSlots: number;
}

export function wallWidgetView(chatCount: number, candidateCount: number): WallWidgetView {
  // Отрицательные и раздутые значения не должны просачиваться в подпись:
  // состав приходит с сервера, а он лениво чистит мёртвые id
  const count = Math.max(0, Math.min(MAX_CHATS, chatCount));
  const freeSlots = MAX_CHATS - count;
  const suggestCount = Math.max(0, Math.min(freeSlots, candidateCount));
  return {
    counterText: `${count} из ${MAX_CHATS}`,
    empty: count === 0,
    showSuggest: suggestCount > 0,
    suggestCount,
    freeSlots,
  };
}

// Статус чата → точка строки. Две системы координат: у сессии семь состояний
// (Session['status']), у точки — три (ActivityStatus). Перевод:
//  • waiting — ждёт ответа человека, самый сильный сигнал;
//  • starting/working — ход идёт прямо сейчас;
//  • всё остальное (active, finished, orphaned, error) живой точки не даёт —
//    тогда показываем непрочитанность, если она есть.
// error НАРОЧНО не подсвечиваем красным: на дашборде это читалось бы как «сломалось
// сейчас», хотя ход давно завершён — про ошибку скажет сам чат, когда его откроют.
// unread вычисляет вызывающий (hasUnread трогает localStorage) — так функция
// остаётся чистой и проверяемой.
export function wallRowStatus(status: Session['status'] | string, unread: boolean): ActivityStatus | null {
  if (status === 'waiting') return 'waiting';
  if (status === 'starting' || status === 'working') return 'working';
  return unread ? 'unread' : null;
}
