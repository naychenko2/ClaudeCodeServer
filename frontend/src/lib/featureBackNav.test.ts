// Стор «← назад» (featureBackNav): откуда пришли в ключевой чат, в памяти вкладки.
// Чистая логика (без DOM): запись/сброс/чтение по текущему чату.
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { recordBackNav, recordFeatureReturn, clearBackNav, getBackNav, backNavFrom, normalizeBackTarget, __test } from './featureBackNav';

beforeEach(() => __test.reset());

describe('featureBackNav', () => {
  it('recordBackNav: записывает from→to; backNavFrom(to) = from, backNavFrom(другой) = null', () => {
    recordBackNav('chat-exec', 'chat-key');
    expect(getBackNav()).toEqual({ from: 'chat-exec', fromKind: 'chat', to: { kind: 'chat', id: 'chat-key' } });
    expect(backNavFrom('chat-key')).toBe('chat-exec');
    expect(backNavFrom('other')).toBeNull();
    expect(backNavFrom(null)).toBeNull();
  });

  it('recordBackNav(from, from) — не переход: указатель не ставится', () => {
    recordBackNav('chat-a', 'chat-a');
    expect(getBackNav()).toBeNull();
    expect(backNavFrom('chat-a')).toBeNull();
  });

  it('recordBackNav с пустым from/to — не записывается', () => {
    recordBackNav('', 'chat-key');
    expect(getBackNav()).toBeNull();
    recordBackNav('chat-exec', '');
    expect(getBackNav()).toBeNull();
  });

  it('clearBackNav: сбрасывает; повторный clear — идемпотентен', () => {
    recordBackNav('chat-exec', 'chat-key');
    clearBackNav();
    expect(getBackNav()).toBeNull();
    expect(backNavFrom('chat-key')).toBeNull();
    clearBackNav(); // не падает
    expect(getBackNav()).toBeNull();
  });

  it('смена проекта: clearBackNav сбрасывает указатель (не перескакивает через границу проекта)', () => {
    // Указатель из старого проекта (чат → фича) записан; WorkspacePage при перемонтировании
    // (смена проекта, key={project.id}) зовёт clearBackNav, чтобы он не применился к новому.
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    expect(getBackNav(), 'указатель не записан').not.toBeNull();
    clearBackNav();
    expect(getBackNav(), 'указатель из старого проекта не сброшен').toBeNull();
  });

  it('recordBackNav поверх прежнего — заменяет указатель (один «← назад»)', () => {
    recordBackNav('chat-a', 'chat-key');
    recordBackNav('chat-b', 'chat-key');
    expect(getBackNav()).toEqual({ from: 'chat-b', fromKind: 'chat', to: { kind: 'chat', id: 'chat-key' } });
    expect(backNavFrom('chat-key')).toBe('chat-b');
  });

  it('backNavFrom: только когда to === текущий чат (иначе null, даже если from совпадает)', () => {
    recordBackNav('chat-exec', 'chat-key');
    // from = 'chat-exec', но мы в 'chat-exec' (это from, не to) → null.
    expect(backNavFrom('chat-exec')).toBeNull();
  });
});

describe('featureBackNav: цель перехода (chat | feature) и нормализация старой строки', () => {
  beforeEach(() => __test.reset());

  it('normalizeBackTarget: старая строка (id чата) → { kind: chat, id }', () => {
    expect(normalizeBackTarget('chat-key')).toEqual({ kind: 'chat', id: 'chat-key' });
  });

  it('normalizeBackTarget: объект { kind, id } — как есть (feature)', () => {
    expect(normalizeBackTarget({ kind: 'feature', id: 'f1' })).toEqual({ kind: 'feature', id: 'f1' });
  });

  it('recordBackNav с целем-фичей: to = { kind: feature, id }; backNavFrom(чата) = null', () => {
    recordBackNav('chat-exec', { kind: 'feature', id: 'f1' });
    expect(getBackNav()).toEqual({ from: 'chat-exec', fromKind: 'chat', to: { kind: 'feature', id: 'f1' } });
    // Страница фичи — не чат: «← назад» по chat-id не совпадает (backNavFrom ищет to-чат).
    expect(backNavFrom('f1')).toBeNull();
    expect(backNavFrom('chat-exec')).toBeNull();
  });

  it('«чат → тот же чат» (to.kind=chat, id=from) — не переход', () => {
    recordBackNav('chat-a', 'chat-a');
    expect(getBackNav()).toBeNull();
  });

  it('«чат → фича с тем же id» — переход (разные поверхности, guard не срабатывает)', () => {
    recordBackNav('f1', { kind: 'feature', id: 'f1' });
    expect(getBackNav()).toEqual({ from: 'f1', fromKind: 'chat', to: { kind: 'feature', id: 'f1' } });
  });
});

// GC-F11: снимок вида «Хронологии» (этап · карточка · прокрутка) — его берёт
// recordFeatureReturn у зарегистрированного поставщика, а не у вызывающих.
// Мутации (в отчёте): (1) не звать поставщика в recordFeatureReturn → «пишет снимок»
// краснеет; (2) не чистить pending в recordFeatureReturn → «новый переход невалидирует»
// краснеет; (3) не брать featureId в consume → «для другой фичи false» краснеет.
//
// GC-F11R2 (дефект 7d399549): прокрутка — scrollTop внутреннего контейнера страницы
// (FeaturePage.tsx:366), не window.scrollY; гашение снимка — только подтверждённое
// применение (commitFeatureViewApplied), а не при чтении/применении. Мутации:
// (4) чистить pending при применении, не перенося в lastApplied → «commit переносит»
// краснеет; (6) не чистить lastApplied в recordFeatureReturn → «новый переход гасит
// подтверждённый» краснеет.
//
// GC-F11R3 (мажоры ревью GC-R-P5): подтверждение идемпотентно по seq (повторный вызов
// с тем же seq — no-op: StrictMode дважды зовёт эффекты монтирования, прежнее
// «закрытие окна повторным подтверждением» съедало окно до реального remount); окно
// расходует стабильный кадр после применения (scheduleConsume).
// GC-F11R4 (мажор ревью GC-R-P6): размонтирование ВКЛАДКИ — не сигнал ухода: пока
// вкладка не смонтирована, расход откладывается — окно достаётся remount-инстансу при
// любой скорости ответа сервера. Окно гасит уход со СТРАНИЦЫ (registerFeaturePage,
// отложенная проверка: мнимый unmount→mount StrictMode её отменяет). Мутации:
// (5) гасить lastApplied при повторном подтверждении с тем же seq (старый механизм) →
// «повторное подтверждение идемпотентно» краснеет; (7) никогда не расходовать окно
// (нет scheduleConsume) → «окно расходует стабильный кадр» краснеет; (8) вернуть
// зависимость расхода от размонтирования вкладки (прежний _unmountStamp) → «размонтирование
// вкладки не отменяет расход» краснеет; (9) страница не гасит окно при уходе → «уход со
// страницы гасит…» краснеет; (10) гасить окно сразу при cleanup страницы → «мнимый
// unmount→mount не гасит» краснеет; (11) чужой featureId тоже гасится проверкой →
// «чужая страница ничего не гасит» краснеет.
describe('featureBackNav: снимок вида (GC-F11)', () => {
  it('поставщик зарегистрирован → recordFeatureReturn пишет снимок в указатель', () => {
    const off = __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1234 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(getBackNav()?.view, 'снимка в указателе нет').toEqual({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1234 });
    off();
  });

  it('поставщика нет (или он для другой фичи) → снимка нет', () => {
    const off = __test.registerFeatureViewProvider('other', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 7 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(getBackNav()?.view ?? null, 'чужой поставщик дал снимок').toBeNull();
    off();
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(getBackNav()?.view ?? null, 'после снятия снимок остался').toBeNull();
  });

  it('снятие старого монтирования не стирает новую регистрацию (identity-suffix)', () => {
    const offOld = __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'old', expandedKey: null, scrollTop: 0 }));
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'new', expandedKey: null, scrollTop: 0 }));
    offOld();
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(getBackNav()?.view?.stageKey, 'старое снятие стёрло новую регистрацию').toBe('new');
  });

  it('consume переносит снимок в pending; чтение самого по себе его не гасит', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1234 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1'), 'снимка не было').toBe(true);
    const p = __test.pendingFeatureView('f1');
    expect(p, 'pending не взят').not.toBeNull();
    expect(p!.view).toEqual({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1234 });
    // Чтение (инициализация состояния вкладки) ничего не гасит: повторное чтение —
    // тот же снимок (remount вкладки читает его же).
    expect(__test.pendingFeatureView('f1'), 'чтение погасило pending').not.toBeNull();
    expect(__test.lastAppliedFeatureView('f1'), 'lastApplied поставлен без подтверждения').toBeNull();
  });

  it('pending переживает clearBackNav: указатель погасен, снимок цел (дефект 4c4645e2)', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1234 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    __test.consumeFeatureViewReturn('f1');
    clearBackNav(); // обработчик cc-open-feature гасит указатель ПОСЛЕ consume
    expect(getBackNav(), 'указатель не погашен').toBeNull();
    expect(__test.pendingFeatureView('f1'), 'снимок погас вместе с указателем').not.toBeNull();
  });

  it('consume без снимка или для другой фичи → false, pending не ставится', () => {
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича'); // поставщика нет → view нет
    expect(__test.consumeFeatureViewReturn('f1'), 'пустой consume дал true').toBe(false);
    expect(__test.pendingFeatureView('f1')).toBeNull();
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: null, expandedKey: null, scrollTop: 0 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('other'), 'чужая фича приняла снимок').toBe(false);
    expect(__test.pendingFeatureView('other'), 'pending для чужой фичи').toBeNull();
    expect(__test.pendingFeatureView('f1'), 'pending поставлен без consume своей фичи').toBeNull();
  });

  it('новый переход невалидирует незабранный pending', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 0 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1')).toBe(true);
    recordFeatureReturn('f1', { kind: 'chat', id: 'c2' }, 'Фича'); // новый переход
    expect(__test.pendingFeatureView('f1'), 'старый pending пережил новый переход').toBeNull();
  });

  it('переход без поставщика (recordBackNav) снимка не несёт', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1 }));
    recordBackNav('chat-a', { kind: 'feature', id: 'f1' });
    expect(getBackNav()?.view ?? null, 'chat-переход получил снимок').toBeNull();
  });
});

// GC-F11R2 (дефект 7d399549): канал подтверждения применения. Вкладка применяет
// снимок ПРИ ИНИЦИАЛИЗАЦИИ своего состояния (remount страницы 'loading'→'ok') и
// подтверждает применение post-mount; подтверждённый снимок переживает remount.
// GC-F11R3: подтверждение идемпотентно по seq; окно расходует стабильный кадр после
// применения (таймер CONSUME_MS без размонтирования вкладки), а не подтверждение.
describe('featureBackNav: подтверждение применения снимка (GC-F11R2)', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); __test.reset(); });
  it('commit переносит pending в lastApplied: remount читает тот же снимок', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1234 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1')).toBe(true);
    const p = __test.pendingFeatureView('f1')!;
    __test.commitFeatureViewApplied('f1', p.seq);
    expect(__test.pendingFeatureView('f1'), 'pending не перенесён').toBeNull();
    const la = __test.lastAppliedFeatureView('f1');
    expect(la, 'подтверждённый снимок не переживает remount').not.toBeNull();
    expect(la!.seq, 'seq подтверждённого снимка').toBe(p.seq);
    expect(la!.view, 'содержимое подтверждённого снимка').toEqual({ stageKey: 'prod', expandedKey: 'ST-F9', scrollTop: 1234 });
  });

  it('чужой featureId/seq ничего не гасят (защита от чужого инстанса)', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 5 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1')).toBe(true);
    const p = __test.pendingFeatureView('f1')!;
    __test.commitFeatureViewApplied('other', p.seq);
    __test.commitFeatureViewApplied('f1', p.seq + 1);
    expect(__test.pendingFeatureView('f1'), 'чужой commit погасил pending').not.toBeNull();
    expect(__test.lastAppliedFeatureView('f1'), 'чужой commit поставил lastApplied').toBeNull();
  });

  it('повторное подтверждение того же seq идемпотентно: окно НЕ закрывается подтверждением (GC-F11R3)', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 5 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1')).toBe(true);
    const seq = __test.pendingFeatureView('f1')!.seq;
    __test.commitFeatureViewApplied('f1', seq); // первый вызов: pending → lastApplied
    expect(__test.lastAppliedFeatureView('f1'), 'первое подтверждение не поставило lastApplied').not.toBeNull();
    // Двойной эффект StrictMode / remount-инстанс: тот же seq — no-op. Прежнее «закрытие
    // окна повторным подтверждением» съедало окно до реального remount (мажор GC-R-P5).
    __test.commitFeatureViewApplied('f1', seq);
    expect(__test.lastAppliedFeatureView('f1'), 'повторное подтверждение с тем же seq закрыло окно').not.toBeNull();
    expect(__test.pendingFeatureView('f1'), 'повторное подтверждение тронуло pending').toBeNull();
  });

  it('окно расходует стабильный кадр после применения: после применения без remount канал пуст (GC-F11R3)', () => {
    const off = __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 5 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1')).toBe(true);
    __test.commitFeatureViewApplied('f1', __test.pendingFeatureView('f1')!.seq);
    expect(__test.lastAppliedFeatureView('f1'), 'окно не поставлено применением').not.toBeNull();
    // Вкладка не размонтирована (off() не вызван): применение стабильно → окно расходует
    // свой отложенный кадр. Без расхода снимок «залипает» до следующего открытия (мажор 2).
    vi.advanceTimersByTime(__test.CONSUME_MS);
    expect(__test.lastAppliedFeatureView('f1'), 'окно залипло после стабильного применения').toBeNull();
    off();
  });

  it('размонтирование вкладки не отменяет расход: окно достаётся remount-инстансу и расходуется его стабильным кадром (GC-F11R4)', () => {
    const off = __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 5 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1')).toBe(true);
    __test.commitFeatureViewApplied('f1', __test.pendingFeatureView('f1')!.seq);
    expect(__test.lastAppliedFeatureView('f1'), 'окно не поставлено применением').not.toBeNull();
    // Снятие регистрации = размонтирование вкладки ('loading'→'ok' remount, медленный
    // ответ): расход ОТКЛАДЫВАЕТСЯ, а не отменяется — окно живо, его читает следующий
    // инстанс (прежний механизм отменял расход безвозвратно: без remount окно жило до
    // нового перехода — мажор GC-R-P6).
    off();
    vi.advanceTimersByTime(__test.CONSUME_MS);
    expect(__test.lastAppliedFeatureView('f1'), 'размонтирование вкладки погасило окно').not.toBeNull();
    // Remount (без нового подтверждения): отложенный расход срабатывает на стабильном
    // кадре — окно закрыт. В прежнем механизме таймер здесь уже мёртв: залипание навсегда.
    const off2 = __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 5 }));
    vi.advanceTimersByTime(__test.CONSUME_MS);
    expect(__test.lastAppliedFeatureView('f1'), 'отложенный расход не сработал после remount').toBeNull();
    off2();
  });

  it('новый переход гасит и подтверждённый снимок (old lastApplied не для новой страницы)', () => {
    __test.registerFeatureViewProvider('f1', () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 5 }));
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1')).toBe(true);
    __test.commitFeatureViewApplied('f1', __test.pendingFeatureView('f1')!.seq);
    expect(__test.lastAppliedFeatureView('f1')).not.toBeNull();
    recordFeatureReturn('f1', { kind: 'chat', id: 'c2' }, 'Фича'); // новый переход
    expect(__test.lastAppliedFeatureView('f1'), 'подтверждённый снимок пережил новый переход').toBeNull();
  });
});

// GC-F11R4 (мажор ревью GC-R-P6): окно возврата живёт только ради того открытия фичи,
// ради которого снято. Регистрация страницы (registerFeaturePage — зовёт FeaturePage):
// cleanup ставит ОТЛОЖЕННУЮ проверку (setTimeout(0)) — повторная регистрация с тем же
// featureId её отменяет (мнимый unmount→mount StrictMode, main.tsx:14), реальный уход
// гасит pending и lastApplied этой фичи и её таймер расхода. Мутации: (9) «страница не
// гасит окно при уходе» → первый тест краснеет; (10) «гасить окно сразу при cleanup
// страницы» → тест StrictMode краснеет; (11) «проверка гасит и чужой featureId» →
// последний тест краснеет.
describe('featureBackNav: окно возврата и страница (GC-F11R4)', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); __test.reset(); });

  const provider = () => ({ stageKey: 'prod', expandedKey: null, scrollTop: 5 });
  // Живое окно: переход → забор чипом → подтверждение применением (последний шаг — опционален).
  function liveWindow(commit: boolean) {
    __test.registerFeatureViewProvider('f1', provider);
    recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    expect(__test.consumeFeatureViewReturn('f1'), 'снимка для забора нет').toBe(true);
    if (commit) __test.commitFeatureViewApplied('f1', __test.pendingFeatureView('f1')!.seq);
  }

  it('уход со страницы гасит подтверждённое окно: отложенная проверка после cleanup', () => {
    const offPage = __test.registerFeaturePage('f1');
    liveWindow(true);
    expect(__test.lastAppliedFeatureView('f1'), 'окно не поставлено применением').not.toBeNull();
    offPage(); // закрытие страницы (вкладка снята вместе с ней) — это уход, не remount
    vi.advanceTimersByTime(0); // отложенная проверка
    expect(__test.lastAppliedFeatureView('f1'), 'окно пережило уход со страницы').toBeNull();
  });

  it('уход со страницы до подтверждения гасит pending', () => {
    const offPage = __test.registerFeaturePage('f1');
    liveWindow(false); // чип забрал снимок, вкладка его ещё не применила
    offPage();
    vi.advanceTimersByTime(0);
    expect(__test.pendingFeatureView('f1'), 'pending пережил уход со страницы').toBeNull();
  });

  it('мнимый unmount→mount страницы (StrictMode) окно не гасит: повторная регистрация отменяет проверку', () => {
    const offPage = __test.registerFeaturePage('f1');
    liveWindow(true);
    offPage(); // мнимый unmount (cleanup StrictMode)
    const offPage2 = __test.registerFeaturePage('f1'); // мнимый mount — та же фича: проверка снята
    vi.advanceTimersByTime(0);
    expect(__test.lastAppliedFeatureView('f1'), 'мнимый unmount погасил окно').not.toBeNull();
    // Реальный уход после этого — гасит (иначе окно жило бы до нового перехода).
    offPage2();
    vi.advanceTimersByTime(0);
    expect(__test.lastAppliedFeatureView('f1'), 'реальный уход не погасил окно').toBeNull();
  });

  it('чужая страница ничего не гасит; закрытие своей страницы при открытой чужой — гасит', () => {
    const offPage = __test.registerFeaturePage('f1');
    liveWindow(true);
    const offOther = __test.registerFeaturePage('other'); // чужая фича: её проверка касается только 'other'
    offOther();
    vi.advanceTimersByTime(0);
    expect(__test.lastAppliedFeatureView('f1'), 'чужая страница погасила это окно').not.toBeNull();
    // Реальный уход из своей фичи (открыта чужая): проверка не находит страницу 'f1' → гасит.
    offPage();
    vi.advanceTimersByTime(0);
    expect(__test.lastAppliedFeatureView('f1'), 'уход при открытой чужой фиче не погасил окно').toBeNull();
  });
});
