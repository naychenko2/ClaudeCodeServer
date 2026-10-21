import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { AlertTriangle, MoreHorizontal, Plug, RefreshCw, ShieldAlert, X } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, EmptyState, IconButton, Menu, MenuItem, MenuSep, Modal, Notice, PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { getEffectiveTheme, subscribeThemeMode } from '../../lib/themeMode';
import { useProjectFeature } from '../../lib/projectCapabilities';
import { ProjectFeature, type Project } from '../../types';
import { api, type PluginInfo, type PluginPanelView } from '../../lib/api';
import { ConsentDialog, type PluginConsentData } from './ConsentDialog';
import {
  BRIDGE_VERSION, READY_TIMEOUT_MS, MAX_PANEL_FILE_BYTES,
  buildInitMessage, buildPanelSrcdoc, buildResultMessage, buildThemeMessage,
  bridgeErrorText, clampContentHeight, PANEL_CONTENT_MAX_HEIGHT, deniedKeyOf, panelWindowOf, validateFrameMessage,
  type BridgeErrorCode, type FrameMessage, type HostMessage,
} from './bridge';
import type { PanelTokens } from './panelSrcdoc';
import { humanizeId, panelDisplayName } from './panelNaming';

// === Токены панели из темы хоста (S8) ===
// Панель — sandbox-iframe без allow-same-origin: --c-* хоста до неё не доходят.
// Поэтому хост читает СВОИ computed --c-* (текущая тема) и мапит в --cc-* панели.
// Единственный источник правды по палитре — тема хоста (lib/theme.css, обе темы);
// в панели нет ручной копии. Каждый --cc-* ниже есть в теме хоста (критерий S8.3).
const CC_TOKEN_MAP: Record<string, string> = {
  '--cc-bg':        '--c-bg-card',
  '--cc-fg':        '--c-text-primary',
  '--cc-fg-muted':  '--c-text-muted',
  '--cc-accent':    '--c-accent',
  '--cc-accent-fg': '--c-on-accent',
  '--cc-border':    '--c-border',
  '--cc-bg-inset':  '--c-bg-inset',
  '--cc-success':   '--c-success',
  '--cc-warning':   '--c-warning',
  '--cc-danger':    '--c-danger',
  '--cc-info':      '--c-info',
};

// Чтец токенов текущей темы: getComputedStyle(<html>) несёт активную тему
// (data-theme проставляет themeMode.ts), поэтому getPropertyValue('--c-*') даёт
// значение именно текущей темы. В node-тесте (без CSS) document/getComputedStyle
// заглушены — возвращаем {} (токены не критичны для логики моста).
function readHostTokens(): PanelTokens {
  if (typeof document === 'undefined' || typeof getComputedStyle !== 'function') return {};
  const cs = getComputedStyle(document.documentElement);
  const tokens: PanelTokens = {};
  for (const cc of Object.keys(CC_TOKEN_MAP)) {
    const v = cs.getPropertyValue(CC_TOKEN_MAP[cc]).trim();
    if (v) tokens[cc] = v;
  }
  return tokens;
}

// === Панель плагина (S5): одна панель = одна карточка ===
// Десктоп: карточка — обычная панель рельса (PanelZone рисует её через PanelShell),
// поэтому PluginFrame НЕ рисует свой Island/IslandHeader — шапка карточки и кнопка
// «Скрыть панель» принадлежат PanelShell, а контролы (перезагрузка, «Права…»)
// телепортируются в шапку через PanelHeaderSlot.
// Мобила: вкладки-панелей нет, вкладка «Плагины» одна — PluginFrame сам рисует
// Island + шапку и переключатель, какую панель плагина показать (useHasPanelHeader
// = false → свой Island, как и раньше).
//
// Тело — sandbox-iframe (sandbox="allow-scripts" БЕЗ allow-same-origin) по мосту v1:
// хост забирает panel/index.html по REST (user JWT), вставляет CSP и шим plugin-kit
// в srcdoc. JWT и стор хоста во фрейм не передаются — панель видит только
// init/result/theme/context и отвечает ready/call/resize (ADR-031, инвариант 1).

// Ключ фрейма: (плагин, панель) + версия плагина + НАБОР выданных прав +
// счётчик перезагрузки. Версия/права в ключе (S7): обновился плагин (новая
// версия) или выдано/снято право — фрейм пересоздаётся, иначе панель
// продолжала бы показывать старую версию/права. Права сортируются: набор тот
// же при любом порядке в DTO — перемонтирования без причины не плодим.
// Смена темы в key не входит (тема — сообщением, см. srcdoc).
export function pluginFrameKey(plugin: PluginInfo, panel: PluginPanelView, reloadKey: number): string {
  return `${plugin.id}::${panel.id}#${plugin.version}#${[...plugin.granted].sort().join(',')}#${reloadKey}`;
}

// Данные диалога «Права…» в режиме «изменить» из записи установки (та же
// логика, что в PluginsProjectSection: вклады в DTO бэка нет, required — false).
function toConsentData(info: PluginInfo): PluginConsentData {
  return {
    id: info.id,
    name: humanizeId(info.id),
    version: info.version,
    path: info.source.startsWith('upload:') ? info.source.slice('upload:'.length) : undefined,
    sha256: info.sha256 ? info.sha256.slice(0, 8) : undefined,
    contributions: [],
    requested: info.requested,
    secrets: info.secrets.map(s => ({ name: s.name, required: false, set: s.set })),
    granted: info.granted,
  };
}

// Скелет загрузки: 4 полосы (макет, «ЗАГРУЗКА (ДО 1-ГО СООБЩЕНИЯ МОСТА)»).
function LoadingSkeleton() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {[0, 1, 2, 3].map(i => (
        <div key={i} style={{ height: 14, borderRadius: R.sm, background: C.bgInset, opacity: 0.6 + i * 0.1 }} />
      ))}
    </div>
  );
}

// Загрузка panel/index.html: absent (нет включённых плагинов) / fetching /
// noPanel (у плагина нет панели, 404) / failed (остальные ошибки) / html.
type FetchState =
  | { kind: 'absent' }
  | { kind: 'fetching' }
  | { kind: 'noPanel' }
  | { kind: 'failed'; message: string }
  | { kind: 'html'; html: string };

// Состояние моста: waiting (iframe смонтирован, ждём ready) / alive (ready
// получен, init отправлен) / код ошибки (bridge_timeout и др.).
type BridgeState = 'waiting' | 'alive' | BridgeErrorCode;

// Потолок одновременных вызовов одного фрейма и размер args (ADR-031 §3.4:
// «числа фиксирует шаг 3.2 в CLAUDE.md вертикали» — зафиксировано там).
const MAX_INFLIGHT_CALLS = 8;
const MAX_ARGS_BYTES = 64 * 1024;

export function PluginFrame({ project, plugin, panel, onOpenPlugins, onClose, showTitle = true }: {
  project: Project;
  // Конкретный плагин и его конкретная панель: одна панель = одна карточка (S5).
  // На мобиле (свой Island) панель выбирает переключатель; на десктопе карточка
  // уже одна панель, и переключатель не нужен.
  plugin: PluginInfo;
  panel: PluginPanelView;
  // «Открыть в «Плагинах»» — настройки проекта (секция плагинов)
  onOpenPlugins?: () => void;
  // «Скрыть панель» — закрыть панельную зону (на десктопе дублирует крестик PanelShell)
  onClose?: () => void;
  // Мобила: рисовать ли строку-заголовок в toolbar. Когда панелей несколько,
  // заголовок даёт активный сегмент переключателя (PluginPanelsSwitcher) — тогда
  // showTitle=false, чтобы имя не дублировалось.
  showTitle?: boolean;
}) {
  const isMobile = useIsMobile();
  const pluginsAvail = useProjectFeature(project, ProjectFeature.Plugins);
  // Есть ли над панелью шапка (десктоп — PanelShell, мобила — свой Island).
  // useHasPanelHeader: true → контролы в PanelHeaderSlot, false → свой Island.
  const inShell = useHasPanelHeader();
  // Эффективная тема хоста: в init и в theme-сообщениях (без перемонтирования)
  const theme = useSyncExternalStore(subscribeThemeMode, getEffectiveTheme, getEffectiveTheme);

  const [menuOpen, setMenuOpen] = useState(false);
  const [fetch, setFetch] = useState<FetchState>({ kind: 'fetching' });
  const [bridge, setBridge] = useState<BridgeState>('waiting');
  const [height, setHeight] = useState<number | null>(null);
  const [deniedKey, setDeniedKey] = useState<string | null>(null);
  const [log, setLog] = useState<string[]>([]);
  const [logOpen, setLogOpen] = useState(false);
  const [consent, setConsent] = useState<PluginConsentData | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  // Контент панели длиннее потолка (PANEL_CONTENT_MAX_HEIGHT) — показана только верхняя
  // часть. Хост раньше писал об этом только в журнал моста; человек об обрезке не узнавал.
  const [clamped, setClamped] = useState(false);

  const name = humanizeId(plugin.id);
  // Заголовок панели: единый вывод (title из манифеста, иначе имя плагина)
  const panelTitle = panelDisplayName(plugin, panel);

  const iframeRef = useRef<HTMLIFrameElement | null>(null);
  // init отправлен в текущем монтировании (повторный ready игнорируется, ADR §3.3)
  const initedRef = useRef(false);
  // Вызовы в полёте: потолок MAX_INFLIGHT_CALLS (лишний — result `limit`)
  const inflightRef = useRef(0);
  // Высота контента: resize → pending, применяем не чаще раза за кадр (rAF). Панель
  // шлёт resize часто (при каждом изменении контента); без rAF setHeight дёргался бы
  // на каждое сообщение. rAF — в браузере, setTimeout 16мс — в node-тесте (без rAF).
  const pendingHeightRef = useRef<number | null>(null);
  const rafPendingRef = useRef(false);
  // Актуальные значения для message-обработчика без пересоздания подписки
  const stateRef = useRef({ theme, project, isMobile, plugin: null as PluginInfo | null, bridge, reloadKey });
  stateRef.current = { theme, project, isMobile, plugin, bridge, reloadKey };

  // Применить накопленную высоту контента (не чаще раза за кадр).
  const applyPendingHeight = useCallback(() => {
    rafPendingRef.current = false;
    if (pendingHeightRef.current != null) {
      setHeight(pendingHeightRef.current);
      pendingHeightRef.current = null;
    }
  }, []);
  // Запланировать применение: пока rAF в полёте — новые resize только обновляют pending.
  const scheduleHeight = useCallback(() => {
    if (rafPendingRef.current) return;
    rafPendingRef.current = true;
    if (typeof window.requestAnimationFrame === 'function') {
      window.requestAnimationFrame(() => applyPendingHeight());
    } else {
      setTimeout(applyPendingHeight, 16);
    }
  }, [applyPendingHeight]);

  // Журнал моста (макет, «Подробнее»): последние 20 событий, человекочитаемо
  const logEvent = useCallback((line: string) => {
    setLog(l => [...l.slice(-19), line]);
  }, []);

  // Отправить сообщение хоста во фрейм: адресат — конкретное окно, targetOrigin
  // '*' (непрозрачный origin назвать нельзя, ADR-031 §3.2)
  // Адресат — окно ПАНЕЛИ (frames[0] оболочки), не оболочка: init/result/theme/context
  // уходят в панель, а e.source при приёме сверяется с тем же окном (ADR-031 §3.6).
  const postToFrame = useCallback((msg: HostMessage) => {
    const win = panelWindowOf(iframeRef.current);
    if (win) win.postMessage(msg, '*');
    logEvent(`→ ${msg.type}${'id' in msg ? ` ${msg.id}` : ''}`);
  }, [logEvent]);

  // === Забор HTML панели (REST, user JWT) + сброс состояния моста ===
  // entry — из манифеста панели (contributes.panels[].entry); маршрут
  // {**path} — catch-all, слэши entry остаются литеральными.
  useEffect(() => {
    let cancelled = false;
    initedRef.current = false;
    inflightRef.current = 0;
    // pendingHeightRef сбрасываем: отложенная высота ПРОШЛОЙ панели (rAF ещё в полёте)
    // не должна прилететь в новый фрейм при смене панели/перезагрузке.
    pendingHeightRef.current = null;
    setBridge('waiting');
    setHeight(null);
    setDeniedKey(null);
    setClamped(false);
    setLog([]);
    if (!plugin || !panel) {
      setFetch({ kind: 'absent' });
      return;
    }
    setFetch({ kind: 'fetching' });
    api.plugins.panelHtml(plugin.id, plugin.version, panel.entry)
      .then(html => {
        if (cancelled) return;
        setFetch({ kind: 'html', html });
        logEvent(`← ${panel.entry} загружен`);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        const status = (e as { status?: number })?.status;
        if (status === 404) {
          setFetch({ kind: 'noPanel' });
          logEvent('← панель не найдена (404)');
        } else {
          setFetch({ kind: 'failed', message: e instanceof Error ? e.message : 'Не удалось загрузить панель' });
          logEvent(`← ошибка загрузки: ${e instanceof Error ? e.message : 'неизвестно'}`);
        }
      });
    return () => { cancelled = true; };
    // deps — примитивы (id/version/entry), а не объект plugin/panel: useMemo не
    // кэширует в мини-раннере тестов, и объект был бы новой ссылкой каждый render →
    // эффект зря сбрасывал fetch. В реальном React useMemo стабильна, но строки
    // корректны и там (перезапуск только при смене панели/версии/entry).
  }, [plugin?.id, plugin?.version, panel?.entry, reloadKey, logEvent]);

  // srcdoc: оболочка (CSP + iframe) + внутренний документ (CSP + шим + html панели).
  // Пересобирается при смене панели/версии, но НЕ при смене темы: тема доходит
  // сообщением (шим проставляет data-cc-theme, см. эффект ниже), состояние панели
  // держится. Панель больше MAX_PANEL_FILE_BYTES — srcdoc null (показываем ошибку).
  // theme — тема первого кадра (ADR-031 §3.3): берётся из замыкания, в deps не
  // входит, чтобы смена темы НЕ перемонтировала панель (только init/theme сообщением).
  const srcdoc = useMemo(() => {
    if (fetch.kind !== 'html') return null;
    // Размер — в БАЙТАХ (TextEncoder), не в символах: 100 000 русских символов
    // ≈ 200 КиБ байт, но .length = 100 000 (символы) — потолок 256 КиБ не срабатывал.
    if (new TextEncoder().encode(fetch.html).length > MAX_PANEL_FILE_BYTES) return null;
    // Токены текущей темы — first-paint (без вспышки): тема намерено вне deps,
    // поэтому srcdoc (и токены) пересобираются только при смене панели, не при смене темы.
    return buildPanelSrcdoc(fetch.html, theme, readHostTokens());
    // eslint-disable-next-line react-hooks/exhaustive-deps -- theme намерено вне deps
  }, [fetch]);

  // Ключ iframe: пересборка srcdoc (тема) НЕ перемонтирует (key не меняется),
  // «Перезагрузить» и смена панели (плагин+панель) — да (reloadKey / targetKey).
  const frameKey = pluginFrameKey(plugin, panel, reloadKey);

  // === Таймаут ready: 3 с после монтирования iframe → bridge_timeout ===
  useEffect(() => {
    if (fetch.kind !== 'html' || bridge !== 'waiting') return;
    const t = setTimeout(() => {
      setBridge('bridge_timeout');
      logEvent('× ready не пришёл за 3 с (bridge_timeout)');
    }, READY_TIMEOUT_MS);
    return () => clearTimeout(t);
  }, [fetch, bridge, frameKey, logEvent]);

  // === call: POST /api/plugins/{id}/call (user JWT), ответ — result с тем же id ===
  const handleCall = useCallback(async (msg: Extract<FrameMessage, { type: 'call' }>) => {
    const s = stateRef.current;
    const p = s.plugin;
    if (!p) return;
    // Потолки (ADR-031 §3.4): число вызовов в полёте и размер args
    if (inflightRef.current >= MAX_INFLIGHT_CALLS) {
      postToFrame(buildResultMessage(msg.id, false, { code: 'limit', message: 'Слишком много одновременных вызовов' }));
      return;
    }
    let args = msg.args;
    try {
      if (JSON.stringify(args).length > MAX_ARGS_BYTES) {
        postToFrame(buildResultMessage(msg.id, false, { code: 'limit', message: 'Аргументы слишком большие' }));
        return;
      }
    } catch {
      args = {};
    }
    inflightRef.current++;
    logEvent(`← call ${msg.id} ${msg.op}`);
    try {
      // Тело HTTP-запроса: {capability, args, projectId} (контракт бэкенда, PluginCallContractTests).
      // `op` — внутреннее имя поля моста (FrameMessage.call.op); в HTTP оно уходит как `capability`.
      // projectId — проект слота (null — вне проекта); context/op в тело не попадают.
      const res = await api.plugins.call(p.id, {
        capability: msg.op,
        args,
        projectId: s.project.id ?? null,
      });
      if (res.ok) {
        postToFrame(buildResultMessage(msg.id, true, res.result));
      } else {
        postToFrame(buildResultMessage(msg.id, false, res.error ?? { code: 'upstream', message: 'Сервер не ответил' }));
        // Отказ по праву — плашка «Нет права {key}» поверх контента (не ломает панель)
        if (res.error?.code === 'denied') {
          const key = deniedKeyOf(res.error.message);
          if (key) setDeniedKey(key);
        }
      }
    } catch (e) {
      // Сам запрос к серверу не дошёл (сеть/гейт) — для панели это upstream
      postToFrame(buildResultMessage(msg.id, false, {
        code: 'upstream',
        message: e instanceof Error ? e.message : 'Сервер не ответил',
      }));
    } finally {
      inflightRef.current--;
    }
  }, [postToFrame, logEvent]);

  // === Приём сообщений фрейма (ADR-031 §3.2: все четыре условия) ===
  // e.source сверяется с окном ПАНЕЛИ (frames[0] оболочки): сообщения от самой
  // оболочки и от чужих окон отбрасываются.
  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      const msg = validateFrameMessage(e, panelWindowOf(iframeRef.current));
      if (!msg) return; // всё невалидное — тихий игнор
      const s = stateRef.current;
      if (msg.type === 'ready') {
        if (s.bridge !== 'waiting' || initedRef.current) return; // повторный ready
        initedRef.current = true;
        if (s.plugin) {
          postToFrame(buildInitMessage({
            plugin: { id: s.plugin.id, version: s.plugin.version },
            slot: 'sidebar',
            context: { projectId: s.project.id },
            theme: s.theme,
            locale: typeof navigator !== 'undefined' ? navigator.language : '',
            granted: s.plugin.granted,
            // Токены текущей темы: панель применяет их (applyTokens), сама палитру не знает
            tokens: readHostTokens(),
          }));
        }
        setBridge('alive');
        logEvent('← ready → init');
      } else if (msg.type === 'call') {
        void handleCall(msg);
      } else if (msg.type === 'resize') {
        // resize = высота контента (ADR-031 §3.3): хост ставит её фрейму (порог 48,
        // страховочный потолок 20 000), применяя не чаще раза за кадр; последнее
        // значение побеждает. Прокручивает обёртка фрейма (overflow:auto ниже).
        pendingHeightRef.current = clampContentHeight(msg.height);
        // Упёрлись в потолок — хвост контента отрезан: строка в журнал моста (автор
        // панели) + видимая плашка (человек), иначе об обрезке никто бы не узнал.
        if (msg.height > PANEL_CONTENT_MAX_HEIGHT) {
          logEvent(`× высота панели ${Math.round(msg.height)} px обрезана до ${PANEL_CONTENT_MAX_HEIGHT} px`);
          setClamped(true);
        }
        scheduleHeight();
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- актуальное через stateRef
  }, [frameKey, postToFrame, logEvent, handleCall]);

  // === Смена темы: сообщением, без перемонтирования (ADR-031 §3.3) ===
  // Токены НОВОЙ темы: шим обновляет <style id="cc-tokens">, панель перекрашивается
  // без перезагрузки. readHostTokens() здесь читает уже НОВУЮ тему (data-theme
  // обновлён themeMode.ts до emit, а эффект идёт после re-render).
  useEffect(() => {
    if (bridge === 'alive') postToFrame(buildThemeMessage(theme, readHostTokens()));
  }, [theme, bridge, postToFrame]);

  // «Перезагрузить»: заново забрать html и перемонтировать iframe
  const reload = useCallback(() => setReloadKey(k => k + 1), []);

  // Выдать/изменить права (диалог согласия в режиме «изменить»)
  const submitConsent = async (permissions: string[], secrets: { name: string; value: string }[]) => {
    await api.plugins.grant(plugin.id, permissions, secrets);
    setConsent(null);
  };

  // === Гейты: без фичи плагинов слота нет вовсе (V5) ===
  if (!pluginsAvail) return null;

  // === Шапка: плитка + имя + (reload | точка) + ⋯ ===
  const isBridgeError = bridge === 'bridge_timeout' || bridge === 'bridge_bad_message' || bridge === 'bridge_version';
  // Iframe монтируется, как только есть html (и в waiting, и в alive): шим внутри
  // шлёт ready, и только host отвечает init. Скелет до ready — оверлеем поверх.
  const showIframe = fetch.kind === 'html' && srcdoc !== null;

  // Контролы шапки: перезагрузка + меню «Права…»/«Открыть в «Плагинах»».
  // На десктопе — порталом в PanelShell (PanelHeaderSlot), на мобиле — прямо в
  // шапке своего Island. «Скрыть панель» — у PanelShell (десктоп) или onClose (мобила).
  const headerControls = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 2 }}>
      {isBridgeError && (
        <span style={{ width: 8, height: 8, borderRadius: '50%', background: C.danger, flexShrink: 0 }} />
      )}
      {bridge === 'alive' && (
        <IconButton size="xs" tone="muted" ariaLabel="Перезагрузить панель" onClick={reload}>
          <RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      )}
      <div style={{ position: 'relative' }}>
        <IconButton size="xs" tone="muted" ariaLabel="Меню панели" onClick={() => setMenuOpen(o => !o)}>
          <MoreHorizontal size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        {menuOpen && (
          <Menu onClose={() => setMenuOpen(false)} align="right" top={-2}>
            <MenuItem label="Права…" onClick={() => { setMenuOpen(false); setConsent(toConsentData(plugin)); }} />
            {onOpenPlugins && <MenuItem label="Открыть в «Плагинах»" onClick={() => { setMenuOpen(false); onOpenPlugins(); }} />}
            {(onOpenPlugins || onClose) && <MenuSep />}
            {onClose && <MenuItem label="Скрыть панель" onClick={() => { setMenuOpen(false); onClose(); }} />}
          </Menu>
        )}
      </div>
    </div>
  );

  // Тело по состояниям макета (экран 3)
  const body = (() => {
    if (fetch.kind === 'noPanel') {
      return (
        <EmptyState
          compact
          icon={<Plug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Панель не найдена"
          subtitle={`У плагина «${name}» панель «${panel.id}» не найдена (404).`}
          action={onOpenPlugins ? <Button variant="dashed" size="sm" onClick={onOpenPlugins}>Открыть «Плагины»</Button> : undefined}
        />
      );
    }
    if (fetch.kind === 'failed') {
      return (
        <Notice tone="danger" icon={AlertTriangle} title="Не удалось загрузить панель">
          {fetch.message}
        </Notice>
      );
    }
    if (fetch.kind === 'fetching') {
      return <LoadingSkeleton />;
    }
    // Панель больше потолка (256 КиБ): не монтируем, показываем понятную ошибку.
    // Размер — в БАЙТАХ (TextEncoder), не в символах: 100 000 русских символов
    // ≈ 200 КиБ байт, но .length = 100 000 (символы) — потолок 256 КиБ не срабатывал.
    if (fetch.kind === 'html' && new TextEncoder().encode(fetch.html).length > MAX_PANEL_FILE_BYTES) {
      return (
        <Notice tone="warning" icon={AlertTriangle} title="Панель слишком большая">
          Файл панели больше 256 КиБ — панель не смонтирована.
        </Notice>
      );
    }
    if (isBridgeError) {
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <Notice tone="danger" icon={AlertTriangle} title="Панель не отвечает">
            {bridgeErrorText(bridge as BridgeErrorCode)}
          </Notice>
          <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
            {bridge} · v{BRIDGE_VERSION}
          </div>
          <div style={{ display: 'flex', gap: SP.sm }}>
            <Button variant="ghost" size="sm" leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={reload}>
              Перезагрузить
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setLogOpen(true)}>Подробнее</Button>
          </div>
        </div>
      );
    }
    if (showIframe) {
      return (
        // Внешний div держит оверлеи (скелет, плашка отказа) — они не уезжают при
        // прокрутке. Скролл-контейнер ВНУТРИ него: фрейм растянут под контент (высота
        // из resize, может быть выше карточки), внутренний документ не скроллится
        // (html{overflow:hidden}), поэтому колесо уходит сюда — в документ хоста, где
        // действует наш стиль полос прокрутки (index.css).
        <div style={{ position: 'relative', flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          {/* Обрезка высоты: плашка НАД контентом (не оверлей) — человек видит, что
              панель длиннее 20 000 px и показан только её верх */}
          {clamped && (
            <Notice tone="info" icon={AlertTriangle} title="Панель длиннее 20 000 px" style={{ flexShrink: 0, marginBottom: SP.xs }}>
              Показан только верх.
            </Notice>
          )}
          {/* Отказ в праве: плашка НАД контентом (не оверлей) — не накрывает первые
              строки панели, как раньше (position:absolute поверх скролла) */}
          {deniedKey && bridge === 'alive' && (
            <Notice
              tone="warning" icon={ShieldAlert}
              title={`Нет права ${deniedKey}`}
              style={{ flexShrink: 0, marginBottom: SP.xs }}
              action={
                <IconButton size="xs" tone="muted" ariaLabel="Закрыть" onClick={() => setDeniedKey(null)}>
                  <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
              }
            >
              Панель вызвала операцию, право на которую не выдано.{' '}
              <button
                onClick={() => setConsent(toConsentData(plugin))}
                style={{
                  background: 'none', border: 'none', padding: 0, cursor: 'pointer',
                  color: 'inherit', textDecoration: 'underline', fontFamily: 'inherit', fontSize: 'inherit',
                }}
              >
                Выдать…
              </button>
            </Notice>
          )}
          {/* Скролл-контейнер — КРАЙ карточки (как у соседних панелей): поле живёт
              внутри него, поэтому полоса прокрутки у края, а не в 12px от него. */}
          <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', overflowX: 'hidden', padding: '4px 12px 16px' }}>
          <iframe
            ref={iframeRef}
            key={frameKey}
            srcDoc={srcdoc}
            sandbox="allow-scripts"
            title={`Панель плагина ${name}`}
            // cc-plugin-frame: CSS (index.css) глушит pointer-events, пока панель
            // тащат (body[data-panel-drag]) — иначе iframe глотает drag-события хоста
            className="cc-plugin-frame"
            style={{
              width: '100%',
              height: height ?? '100%',
              border: 'none', display: 'block',
              background: 'transparent',
            }}
          />
          </div>
          {/* Скелет до первого сообщения моста (ready); iframe уже смонтирован,
              шим внутри живёт и шлёт ready — оверлей лишь закрывает контент */}
          {bridge === 'waiting' && (
            <div style={{
              position: 'absolute', inset: 0, background: C.bgCard,
              padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm,
            }}>
              <LoadingSkeleton />
            </div>
          )}
        </div>
      );
    }
    // absent: панель не найдена или не объявлена
    return (
      <EmptyState
        compact
        icon={<Plug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Панель не найдена"
        subtitle={`У плагина «${name}» панель «${panel.id}» не объявлена.`}
        action={onOpenPlugins ? <Button variant="dashed" size="sm" onClick={onOpenPlugins}>Открыть «Плагины»</Button> : undefined}
      />
    );
  })();

  // === Десктоп: в PanelShell — только тело + контролы порталом в шапку ===
  if (inShell) {
    return (
      <>
        <PanelHeaderSlot>
          {headerControls}
        </PanelHeaderSlot>
        {/* Тело: для iframe скролл-контейнер панели сам является краем карточки
            (padding: 0 — поле отдаёт контенту, полоса у края); для не-iframe-состояний
            (ошибка/пусто) поле даёт обёртка. Служебного футера больше нет: права и
            версия — в меню «⋯» / «Подробнее» (журнал моста). */}
        <div style={{
          flex: 1, minHeight: 0, overflow: 'auto',
          background: C.bgCard, padding: showIframe ? 0 : SP.md,
          display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          {body}
        </div>
        {/* Журнал моста («Подробнее»): последние сообщения, моно */}
        {logOpen && (
          <Modal
            width={440}
            title="Журнал моста"
            onClose={() => setLogOpen(false)}
            footer={
              <Button variant="ghost" size="md" fullWidth onClick={() => setLogOpen(false)}>Закрыть</Button>
            }
          >
            <div style={{
              fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
              display: 'flex', flexDirection: 'column', gap: 2,
              maxHeight: 320, overflow: 'auto',
            }}>
              {log.length === 0
                ? <div style={{ color: C.textMuted }}>Событий пока нет</div>
                : log.map((line, i) => <div key={i}>{line}</div>)}
            </div>
          </Modal>
        )}
        {/* Диалог «Права…» (режим «изменить») */}
        {consent && (
          <ConsentDialog
            data={consent}
            mode="modify"
            onClose={() => setConsent(null)}
            onSubmit={submitConsent}
          />
        )}
      </>
    );
  }

  // === Мобила: контент в край (как соседние вкладки), без второй карточки ===
  // Заголовок: единственный — подпись активного сегмента переключателя (PluginPanelsSwitcher,
  // когда панелей несколько); при одной панели — строка-имя в toolbar ниже. Контролы
  // (перезагрузка, «Права…») — в этом же toolbar, а не в шапке-Island (карточка убрана).
  return (
    <>
      {/* Toolbar хоста: заголовок (только когда нет переключателя) + контролы.
          Паддинги — как у toolbar'а PanelShell (components/ui/PanelShell.tsx). */}
      <div style={{
        flexShrink: 0, padding: '8px 10px 9px',
        borderBottom: `1px solid ${C.border}`, background: C.bgPanel,
        display: 'flex', alignItems: 'center', gap: SP.sm,
      }}>
        {showTitle && (
          <span style={{
            fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading,
            flex: '0 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{panelTitle}</span>
        )}
        <div style={{ flex: 1, minWidth: 0 }} />
        {headerControls}
      </div>

      {/* Тело: скролл-контейнер панели = край (как на десктопе), поле отдаёт контенту */}
      <div style={{
        flex: 1, minHeight: 0, overflow: 'auto',
        background: C.bgCard, padding: showIframe ? 0 : SP.md,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
      }}>
        {body}
      </div>

      {/* Журнал моста («Подробнее»): последние сообщения, моно */}
      {logOpen && (
        <Modal
          width={440}
          title="Журнал моста"
          onClose={() => setLogOpen(false)}
          footer={
            <Button variant="ghost" size="md" fullWidth onClick={() => setLogOpen(false)}>Закрыть</Button>
          }
        >
          <div style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
            display: 'flex', flexDirection: 'column', gap: 2,
            maxHeight: 320, overflow: 'auto',
          }}>
            {log.length === 0
              ? <div style={{ color: C.textMuted }}>Событий пока нет</div>
              : log.map((line, i) => <div key={i}>{line}</div>)}
          </div>
        </Modal>
      )}

      {/* Диалог «Права…» (режим «изменить») */}
      {consent && (
        <ConsentDialog
          data={consent}
          mode="modify"
          onClose={() => setConsent(null)}
          onSubmit={submitConsent}
        />
      )}
    </>
  );
}
