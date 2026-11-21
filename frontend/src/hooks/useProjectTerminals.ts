// Терминалы проекта: список + создание/остановка/переименование + live-подписка.
// Вынесено из WorkspacePage (состояние было прибито к нему, а панель «Терминал»
// понадобилась и стене). Поведение то же, за вычетом воркспейсной навигации:
// выбор активного терминала и мобильные переходы остались у вызывающего.
import { useCallback, useEffect, useState } from 'react';
import * as terminalApi from '../lib/terminalSignalr';

// opts.disabled — экрану не нужны терминалы вовсе (детская поверхность: панели «Терминал»
// нет в снимке, а хаб /hubs/terminal без [ChildAllowed] → 403). Признак знает вызывающий,
// хук его не выдумывает: опция сверху, как opts у useProjectServices.
export function useProjectTerminals(projectId: string, opts?: { disabled?: boolean }) {
  const [terminals, setTerminals] = useState<terminalApi.TerminalInfo[]>([]);
  const [activeTerminalId, setActiveTerminalId] = useState<string | null>(null);
  // opts новый объект на каждый рендер — в deps и эффект идёт флаг, не объект
  const disabled = opts?.disabled ?? false;

  const refresh = useCallback(async () => {
    try { setTerminals(await terminalApi.listTerminals(projectId)); } catch { /* офлайн */ }
  }, [projectId]);

  // Список держит весь экран, которому он нужен: и панелька терминала, и бейджи рельсы
  // живут на нём. (В WorkspacePage в deps эффекта сидел ещё leftTab — лишний refresh при
  // смене вкладки; refresh идемпотентен, сюда эту зависимость не тянем.)
  // disabled — экрану терминалов нет вовсе (детская поверхность): эффект не ставится,
  // ни первичного refresh, ни подписки — оба поднимают связь с /hubs/terminal
  // (listTerminals → ensureConnected), ребёнку она не нужна и не разрешена (Х-W4-34).
  useEffect(() => {
    if (disabled) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- подписка на обновления терминалов и первичный refresh
    void refresh();
    return terminalApi.onTerminalMessage(msg => {
      if (msg.type === 'terminal_status') void refresh();
      else if (msg.type === 'terminal_renamed' && msg.terminalId) {
        setTerminals(prev => prev.map(t => t.id === msg.terminalId ? { ...t, name: msg.name ?? t.name } : t));
      }
    });
  }, [refresh, disabled]);

  const create = useCallback(async () => {
    try {
      const t = await terminalApi.createTerminal(projectId);
      setTerminals(prev => [...prev.filter(x => x.id !== t.id), t]);
      setActiveTerminalId(t.id);
    } catch { /* офлайн */ }
  }, [projectId]);

  const stop = useCallback(async (id: string) => {
    await terminalApi.stopTerminal(id);
    setActiveTerminalId(prev => prev === id ? null : prev);
    void refresh();
  }, [refresh]);

  const rename = useCallback(async (id: string, name: string) => {
    try {
      const updated = await terminalApi.renameTerminal(id, name);
      if (updated) setTerminals(prev => prev.map(t => t.id === id ? updated : t));
    } catch { /* офлайн */ }
  }, []);

  return { terminals, activeTerminalId, setActiveTerminalId, refresh, create, stop, rename };
}
