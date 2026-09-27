// Хост iframe с редактором Viaduct и мост postMessage к шиму (scripts/viaduct-shim.js).
// Фрейм в песочнице sandbox="allow-scripts allow-downloads" БЕЗ allow-same-origin:
// origin непрозрачный, ни куки, ни токена, ни хранилища CCS он не видит — модель
// приезжает сюда сообщением init, а обратно уходит сообщениями save. Сообщения
// принимаем только от окна СВОЕГО фрейма (e.source) и только с меткой протокола.
//
// Протокол (v=1) — в шапке шима: ready → init; dirty / save / prefs; focus / flush.
// Настройки вида (c4-*-ключи) в файл модели не пишутся: живут в localStorage этой
// страницы под viaduct-prefs:{projectId} (план встраивания, раздел «Хранение»).
import { useEffect, useRef } from 'react';
import { C } from 'aihome_shell/kit';

export const VIADUCT_SRC = '/modules/viaduct/index.html';
const PROTOCOL = 1;
// Рукопожатие: фрейм не прислал ready за это время — «редактор не загрузился»
// (onerror у iframe на 404 внутри фрейма не срабатывает)
const HANDSHAKE_MS = 20_000;

export type FrameFailure = 'not_installed' | 'timeout';

// Чей это фрейм: проект и номер монтажа. Сохранение, пришедшее после смены проекта или
// пересоздания фрейма (отложенная правка на pagehide), стор по этой метке отбрасывает
export interface FrameStamp {
  projectId: string;
  frameKey: number;
}

interface Props {
  projectId: string;
  frameKey: number;
  model: string | null;
  theme: 'light' | 'dark';
  focus: { id: string; tick: number } | null;
  onDirty: (from: FrameStamp) => void;
  onSave: (value: string, from: FrameStamp) => void;
  onStarted?: () => void;
  onFailed: (why: FrameFailure) => void;
  onWindow?: (w: Window | null) => void;
}

interface ShimMessage {
  source: 'viaduct-shim';
  v: number;
  type: 'ready' | 'started' | 'dirty' | 'save' | 'prefs';
  value?: unknown;
  prefs?: unknown;
}

function prefsKey(projectId: string) { return `viaduct-prefs:${projectId}`; }

function readPrefs(projectId: string): Record<string, string> {
  try {
    const raw = localStorage.getItem(prefsKey(projectId));
    const parsed = raw ? JSON.parse(raw) : null;
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch { return {}; }
}

export function ViaductFrame({ projectId, frameKey, model, theme, focus, onDirty, onSave, onStarted, onFailed, onWindow }: Props) {
  const ref = useRef<HTMLIFrameElement>(null);
  // Колбэки и входные данные — в ref: обработчик сообщений живёт весь срок фрейма,
  // а модель в init должна быть той, с которой фрейм смонтирован
  const cb = useRef({ onDirty, onSave, onStarted, onFailed });
  cb.current = { onDirty, onSave, onStarted, onFailed };
  const initRef = useRef({ model, theme, focus });
  const startedRef = useRef(false);
  const lastFocusTick = useRef(focus?.tick ?? 0);

  useEffect(() => {
    const win = ref.current?.contentWindow ?? null;
    onWindow?.(win);
    let initSent = false;
    let alive = true;
    // Метка монтажа: эффект живёт один монтаж, значения на нём не меняются
    const stamp: FrameStamp = { projectId, frameKey };

    // Модуль не установлен — отвечаем сразу, не ожидая таймаута рукопожатия
    fetch(VIADUCT_SRC, { method: 'HEAD', cache: 'no-store' })
      .then(r => { if (alive && r.status === 404) cb.current.onFailed('not_installed'); })
      .catch(() => { /* сеть — пусть решает таймаут рукопожатия */ });
    const timer = setTimeout(() => { if (alive && !initSent) cb.current.onFailed('timeout'); }, HANDSHAKE_MS);

    const onMessage = (e: MessageEvent) => {
      // Фрейм без allow-same-origin шлёт с непрозрачного origin — иное значит чужой отправитель
      if (!win || e.source !== win || e.origin !== 'null') return;
      const msg = e.data as ShimMessage | null;
      if (!msg || msg.source !== 'viaduct-shim' || msg.v !== PROTOCOL) return;
      switch (msg.type) {
        case 'ready': {
          if (initSent) return;
          initSent = true;
          clearTimeout(timer);
          const init = initRef.current;
          win.postMessage({
            source: 'ccs-host', v: PROTOCOL, type: 'init',
            model: init.model, prefs: readPrefs(projectId), theme: init.theme, focus: init.focus?.id ?? null,
          }, '*');
          return;
        }
        case 'started':
          startedRef.current = true;
          cb.current.onStarted?.();
          return;
        case 'dirty':
          cb.current.onDirty(stamp);
          return;
        case 'save':
          if (typeof msg.value === 'string') cb.current.onSave(msg.value, stamp);
          return;
        case 'prefs':
          if (msg.prefs && typeof msg.prefs === 'object') {
            try { localStorage.setItem(prefsKey(projectId), JSON.stringify(msg.prefs)); } catch { /* квота — не критично */ }
          }
          return;
      }
    };
    window.addEventListener('message', onMessage);
    return () => {
      alive = false;
      clearTimeout(timer);
      onWindow?.(null);
      // Слушатель снимаем с задержкой: на выгрузке фрейма шим отдаёт отложенную
      // правку (pagehide → save), и она не должна пролететь мимо
      setTimeout(() => window.removeEventListener('message', onMessage), 2000);
    };
    // Фрейм живёт один монтаж: смена модели/проекта — это новый key у компонента
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Фокус после старта — командой моста (до старта он уехал в init)
  useEffect(() => {
    if (!focus || focus.tick === lastFocusTick.current) return;
    lastFocusTick.current = focus.tick;
    if (!startedRef.current) { initRef.current = { ...initRef.current, focus }; return; }
    ref.current?.contentWindow?.postMessage({ source: 'ccs-host', v: PROTOCOL, type: 'focus', elementId: focus.id }, '*');
  }, [focus]);

  return (
    <iframe
      ref={ref}
      src={VIADUCT_SRC}
      title="Редактор архитектуры Viaduct"
      sandbox="allow-scripts allow-downloads"
      referrerPolicy="no-referrer"
      style={{ flex: 1, width: '100%', minHeight: 0, border: 'none', display: 'block', background: C.bgCard }}
    />
  );
}
