// Тесты чистой логики виджета «Стена» на дашборде: счётчик мест, видимость кнопки
// автосбора и перевод статуса чата в точку. Рендер-тестов в проекте нет (vitest
// с environment: 'node'), поэтому правила проверяем на уровне функций.
import { describe, it, expect } from 'vitest';
import type { Session } from '../../../types';
import { MAX_CHATS } from '../../wall/wallStore';
import { wallWidgetView, wallRowStatus } from '../wallWidgetView';

describe('сводка виджета стены', () => {
  it('пустой набор: помечен пустым, счётчик с нуля, все места свободны', () => {
    const v = wallWidgetView(0, 0);
    expect(v.empty).toBe(true);
    expect(v.counterText).toBe(`0 из ${MAX_CHATS}`);
    expect(v.freeSlots).toBe(MAX_CHATS);
    expect(v.showSuggest).toBe(false);
  });

  it('частичный набор: предлагает столько кандидатов, сколько их есть', () => {
    const v = wallWidgetView(2, 3);
    expect(v.empty).toBe(false);
    expect(v.counterText).toBe(`2 из ${MAX_CHATS}`);
    expect(v.freeSlots).toBe(MAX_CHATS - 2);
    expect(v.showSuggest).toBe(true);
    expect(v.suggestCount).toBe(3);
  });

  it('кандидатов больше, чем мест: обещаем ровно столько, сколько влезет', () => {
    const v = wallWidgetView(MAX_CHATS - 1, 4);
    expect(v.suggestCount).toBe(1);
  });

  it('полный набор: кнопки автосбора нет', () => {
    const v = wallWidgetView(MAX_CHATS, 3);
    expect(v.freeSlots).toBe(0);
    expect(v.showSuggest).toBe(false);
    expect(v.suggestCount).toBe(0);
  });

  it('состав сверх потолка не раздувает подпись', () => {
    // Сервер чистит мёртвые id лениво — подпись не должна показывать «7 из 5»
    const v = wallWidgetView(MAX_CHATS + 2, 0);
    expect(v.counterText).toBe(`${MAX_CHATS} из ${MAX_CHATS}`);
    expect(v.freeSlots).toBe(0);
  });
});

describe('точка статуса строки', () => {
  it('ждёт ответа — сильнее всего', () => {
    expect(wallRowStatus('waiting', false)).toBe('waiting');
    // Живой статус важнее непрочитанности
    expect(wallRowStatus('waiting', true)).toBe('waiting');
  });

  it('идущий ход — работает', () => {
    expect(wallRowStatus('starting', false)).toBe('working');
    expect(wallRowStatus('working', false)).toBe('working');
    expect(wallRowStatus('working', true)).toBe('working');
  });

  it('тихие статусы дают точку только при непрочитанном', () => {
    const quiet: Session['status'][] = ['active', 'finished', 'orphaned', 'error'];
    for (const st of quiet) {
      expect(wallRowStatus(st, false)).toBeNull();
      expect(wallRowStatus(st, true)).toBe('unread');
    }
  });

  it('живые фоновые агенты дают точку работы, а не серую непрочитанность', () => {
    // Статус чата при доживающем фоне — active: без признака строка получала бы
    // серую точку «сюда не заходили», хотя работа идёт прямо сейчас
    expect(wallRowStatus('active', false, true)).toBe('working');
    expect(wallRowStatus('active', true, true)).toBe('working');
    // Ожидание человека важнее: там ждут ответа, а фон работает сам
    expect(wallRowStatus('waiting', false, true)).toBe('waiting');
  });

  it('ошибка не подсвечивается как живое состояние', () => {
    // Красная точка на дашборде читалась бы как «сломалось прямо сейчас»
    expect(wallRowStatus('error', false)).toBeNull();
  });

  it('незнакомое значение статуса не роняет строку', () => {
    expect(wallRowStatus('какая-то-новая-фаза', false)).toBeNull();
    expect(wallRowStatus('какая-то-новая-фаза', true)).toBe('unread');
  });
});
