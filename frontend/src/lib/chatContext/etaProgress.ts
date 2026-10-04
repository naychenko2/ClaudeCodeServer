// Прогресс запуска по ожидаемой длительности: поставщики процентов не присылают, поэтому доля считается
// от времени. Внутри прогона не выше 95 %, пока он не кончился; в очереди полоса стоит. Формула та же, что у
// полосы карточки (`progressPercent` вертикали картинок), здесь — для кнопки запуска и низа панели «Контекст».

const DEFAULT_ETA = 30;

export interface EtaState { run?: number | null; runs?: number | null; etaSeconds?: number | null; queued?: boolean }

// Доля 0..1 на момент now; started — начало текущего прогона
export function etaFraction(s: EtaState, startedAt: number, now: number): number {
  const total = Math.max(1, s.runs ?? 1);
  const current = Math.min(total, Math.max(1, s.run ?? 1));
  const eta = Math.max(1, s.etaSeconds ?? DEFAULT_ETA);
  const share = s.queued ? 0 : Math.min(0.95, Math.max(0, now - startedAt) / 1000 / eta);
  return (current - 1 + share) / total;
}

// Тикает раз в 500 мс; update вызывают события шины, stop — на итоге. emit получает долю 0..1
export function etaTicker(emit: (fraction: number) => void) {
  let state: EtaState = {};
  let startedAt = Date.now();
  const tick = () => emit(etaFraction(state, startedAt, Date.now()));
  const timer = setInterval(tick, 500);
  return {
    // running: true — отсчёт текущего прогона начинается заново
    update(next: EtaState, restart = false) {
      state = { ...state, ...next };
      if (restart) startedAt = Date.now();
      tick();
    },
    stop() { clearInterval(timer); },
  };
}
