// Пауза периодического опроса на скрытой вкладке + догон при возврате видимости.
//
// Сценарий OOM: вкладка открыта на час с фоновыми агентами, а опросы списков (сессии,
// чаты, сводка, awaiting, активность) тикают в фоне и жгут CPU/сеть, пока человек
// смотрит в другую вкладку. document.hidden — таймер тикает, но снимок пропускается;
// при возврате видимости один немедленный снимок догоняет всё, что пропустили.
//
// isTabHidden идёт через document, но с guard на typeof: модуль живёт в node-тестах,
// где document может не быть (там и пауза, и догон просто не работают — деградация).

export function isTabHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden';
}

// Обёртка над setInterval: tick вызывается, только когда вкладка видима. При возврате
// видимости — один немедленный tick (догон). Возвращает функцию снятия (clearInterval
// + отписка от visibilitychange). tick должен быть идемпотентным снимком (fetch).
export function startVisiblePoll(tick: () => void, intervalMs: number): () => void {
  const id = setInterval(() => { if (!isTabHidden()) tick(); }, intervalMs);
  const onVis = () => { if (!isTabHidden()) tick(); };
  if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVis);
  return () => {
    clearInterval(id);
    if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', onVis);
  };
}

// Троттлинг рефетча по событию (status_changed): пачка событий за короткий срок — один
// fetch, а не по одному на событие. Leading-edge: первый в серии уходит сразу, следующие
// в пределах minGapMs пропускаются, но в конце окна уходит один хвостовой вызов — иначе
// ход, начавшийся и закончившийся в пределах окна, терял бы последний status_changed
// (точка активности/статус в списке врут до следующего опроса). Возвращает [tick, dispose]:
// tick — вызов на событие, dispose — снять хвостовой таймер при снятии подписки.
export function throttleRefetch(tick: () => void, minGapMs: number): [() => void, () => void] {
  let last = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;
  const call = () => {
    const now = Date.now();
    if (now - last < minGapMs) {
      // В окне: не дублируем, но взводим хвостовой вызов в конец окна (разово)
      if (timer === null) {
        timer = setTimeout(() => { timer = null; last = Date.now(); tick(); }, minGapMs - (now - last));
      }
      return;
    }
    last = now;
    tick();
  };
  const dispose = () => { if (timer !== null) { clearTimeout(timer); timer = null; } };
  return [call, dispose];
}
