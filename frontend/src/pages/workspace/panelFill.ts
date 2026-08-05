// Канал «панели нужна вся высота колонки» — от самой панели к зоне.
//
// Обычная панель у центра стоит по контенту (см. panelStretched в PanelZone), и это
// правильно: список чатов или дерево документов не должны растягиваться на весь
// экран ради пустого низа. Но у некоторых панелей потребность в высоте зависит от их
// СОБСТВЕННОГО состояния: у «Документации» нижняя зона превью включается тумблером —
// с ней панель обязана дотянуться до низа (иначе превью размером в ладонь), без неё
// это просто список.
//
// Раньше такие панели перечислялись реестром (FULL_HEIGHT_KEYS в panelCatalog), и
// «Документация» тянулась до низа ВСЕГДА, даже когда показывала один список. Реестр
// этого знать и не мог: состояние тумблера живёт внутри панели.
//
// Механика та же, что у слота контролов в шапке (PanelHeaderSlot): панель объявляет
// потребность у себя, зона слушает — прокидывать колбэки через владельца контента
// (WorkspacePage) не нужно.
import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';

// Функция-приёмник: её даёт зона на каждую свою панель. null — панель нарисована вне
// зоны (мобильный экран, витрина ui-kit): запрос просто некому услышать.
export const PanelFillContext = createContext<((need: boolean) => void) | null>(null);

// Хук для ПАНЕЛИ: сообщить зоне, нужна ли ей сейчас вся высота.
export function useRequestPanelFill(need: boolean) {
  const request = useContext(PanelFillContext);
  useEffect(() => {
    if (!request) return;
    request(need);
    // Панель ушла с экрана (закрыли, переехала в соседнюю зону) — требование снимаем,
    // иначе колонка держала бы высоту под панель, которой в ней уже нет
    return () => request(false);
  }, [request, need]);
}

// Хук для ЗОНЫ: текущие требования по ключам панелей и стабильная фабрика приёмников.
// Стабильная — иначе эффект панели перезапускался бы на каждый рендер зоны.
export function usePanelFillRequests<K extends string>(): [
  Partial<Record<K, boolean>>,
  (k: K) => (need: boolean) => void,
] {
  const [wanted, setWanted] = useState<Partial<Record<K, boolean>>>({});
  const sinks = useRef(new Map<K, (need: boolean) => void>());

  const sinkFor = useCallback((k: K) => {
    const known = sinks.current.get(k);
    if (known) return known;
    const sink = (need: boolean) => setWanted(cur => (
      !!cur[k] === need ? cur : { ...cur, [k]: need }
    ));
    sinks.current.set(k, sink);
    return sink;
  }, []);

  return [wanted, sinkFor];
}
