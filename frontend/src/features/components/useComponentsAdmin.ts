// useComponentsAdmin — состояние панели «Компоненты» (S2c-2): список подсистем,
// запись тумблера строки, состояние этой записи по строке. Только модель и хук:
// вёрстка панели — S2c-3.
//
// Контракт строки (по постановке и макету, решение 10):
//   • ключ строки = Subsystem.key; порядок строк — как пришёл с сервера, не сортируем;
//   • источник правды — ответ сервера: оптимистичного применения нет, строка меняется
//     ТОЛЬКО целым ответом PUT (или следующим GET);
//   • отказ записи — тело `{ code, message }` (400 core_subsystem / 404 / 503
//     write_failed): message отдаётся строке как есть, items не меняются.
//
// Конкурентность:
//   • разные ключи — независимы, у каждой строки своё writeState;
//   • один ключ подряд — второй toggle отклоняется, пока первый 'saving' (в UI тумблер
//     держат disabled; здесь то же правило как защита от двойного клика — запроса нет);
//   • ответ чужой серии не применяется: на каждый ключ свой номер запроса (seq);
//     reload() поднимает номера всех ключей, поэтому ответ полёта, вернувшийся после
//     reload(), отбрасывается (тест toggle_staleResponseAfterReload_ignored).
//
// Сигналов SignalR нет: панель перечитывает себя GET'ом (reload).

import { useCallback, useEffect, useRef, useState } from 'react';
import { subsystemsApi, type Subsystem } from '../../api/subsystems';

// Состояние записи ОДНОЙ строки: 'saving' — запрос в полёте (тумблер disabled),
// { error } — отказ сервера, текст показывается в строке; 'idle' — ничего не происходит.
export type RowWriteState = 'idle' | 'saving' | { error: string };

export interface ComponentsAdmin {
  // Строки в порядке сервера; менять можем только целой строкой из ответа PUT.
  items: Subsystem[];
  loading: boolean;
  loadError: string | null;
  // Перечитать список: снимок сервера делает полёт прежних записей недействительным.
  reload: () => void;
  // Записать тумблер. Никогда не реджектится: отказ живёт в writeState[key],
  // поэтому у вызывающего («по клику», без await) не бывает unhandled rejection.
  toggle: (key: string, enabled: boolean) => Promise<void>;
  writeState: Record<string, RowWriteState>;
}

// Текст отказа строке «как есть». request() при !res.ok бросает Error с .body — телом
// ответа (SubsystemsController.Put: { code, message }); своего message у него нет у
// 404 без тела и сетевого сбоя — тогда берём текст ошибки и, на самый крайний
// случай, общий.
export function writeErrorMessage(e: unknown): string {
  const body = (e as { body?: { message?: unknown } } | null)?.body;
  if (typeof body?.message === 'string' && body.message) return body.message;
  if (e instanceof Error && e.message) return e.message;
  return 'Не удалось сохранить';
}

export function useComponentsAdmin(): ComponentsAdmin {
  const [items, setItems] = useState<Subsystem[]>([]);
  // loading с первого рендера: список ещё не приходил. Пустой список — это ответ
  // «ничего нет», а не «ещё не спросили»: до ответа панели показывать загрузку.
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [writeState, setWriteState] = useState<Record<string, RowWriteState>>({});

  // Решает ref, рендерит state. «Второй toggle того же ключа» обязан увидеть 'saving'
  // первого в ТОМ ЖЕ тике, а React-state в этом тике ещё старый — поэтому источник
  // решения держится в ref и обновляется синхронно.
  const seq = useRef<Record<string, number>>({});
  const writeRef = useRef<Record<string, RowWriteState>>({});
  const loadSeq = useRef(0);
  const alive = useRef(true);

  const setRow = useCallback((key: string, st: RowWriteState) => {
    writeRef.current = { ...writeRef.current, [key]: st };
    setWriteState(writeRef.current);
  }, []);

  const toggle = useCallback(async (key: string, enabled: boolean): Promise<void> => {
    // Та же строка уже в полёте — второй отказываем без запроса.
    if (writeRef.current[key] === 'saving') return;
    const my = (seq.current[key] ?? 0) + 1;
    seq.current[key] = my;
    setRow(key, 'saving');
    try {
      const updated = await subsystemsApi.put(key, enabled);
      // Ответ чужой серии (после reload или позднейшей записи этого ключа) — не применяем:
      // он о состоянии, которого уже нет.
      if (!alive.current || seq.current[key] !== my) return;
      setRow(key, 'idle');
      // Строка = ответ сервера целиком (со своим restartRequired); позиция та же —
      // порядок строк не наша воля.
      setItems(prev => prev.map(s => (s.key === key ? updated : s)));
    } catch (e) {
      if (!alive.current || seq.current[key] !== my) return;
      // Отказ не трогает items: то, что показалось бы «выключенным», сервер не записал.
      setRow(key, { error: writeErrorMessage(e) });
    }
  }, [setRow]);

  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick(t => t + 1), []);

  useEffect(() => {
    alive.current = true;
    // Новый снимок сервера: полёт прежних записей теряет силу (номера поднимаем),
    // состояния строк обнуляем — панель показывает правду этого GET.
    for (const k of Object.keys(seq.current)) seq.current[k] += 1;
    const my = ++loadSeq.current;
    writeRef.current = {};
    setWriteState({});
    setLoading(true);
    setLoadError(null);
    subsystemsApi.get()
      .then(list => {
        if (!alive.current || loadSeq.current !== my) return;
        // Состояние записи каждой строки после снимка — явно 'idle': вызывающему
        // (S2c-3) не надо додумывать отсутствие ключа как «ничего не происходит».
        const idle: Record<string, RowWriteState> = {};
        for (const s of list) idle[s.key] = 'idle';
        writeRef.current = idle;
        setWriteState(idle);
        setItems(list);
        setLoading(false);
      })
      .catch((e: unknown) => {
        if (!alive.current || loadSeq.current !== my) return;
        // items НЕ чистим: прежний список был ответом сервера и остаётся последним
        // известным состоянием; рядом с ним — строка ошибки и «Перечитать» (reload).
        setLoadError(e instanceof Error && e.message ? e.message : 'Не удалось загрузить список');
        setLoading(false);
      });
    return () => { alive.current = false; };
  }, [tick]);

  return { items, loading, loadError, reload, toggle, writeState };
}
