import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { AlertTriangle, MoreHorizontal, RefreshCw, ShieldAlert, X } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Island, IslandHeader } from '../../components/ui/Island';
import { Button, IconButton, Menu, MenuItem, MenuSep, Modal, Notice, PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
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

// Человекочитаемое имя из id пакета {publisher}/{slug} (та же логика, что в
// PluginsProjectSection: slug → слова с большой буквы).
function humanizeId(id: string): string {
  const slug = id.split('/').pop() ?? id;
  return slug.split('-').filter(Boolean)
    .map(w => w[0].toUpperCase() + w.slice(1))
    .join(' ') || id;
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

// Мини-плитка 20×20 с инициалами (шапка острова, макет экран 3) — только в
// мобильном режиме, где PluginFrame рисует свой Island.
function MiniTile({ name }: { name: string }) {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const initials = parts.length === 0 ? '?'
    : parts.length === 1 ? parts[0].slice(0, 2).toUpperCase()
      : (parts[0][0] + parts[1][0]).toUpperCase();
  return (
    <div style={{
      width: 20, height: 20, borderRadius: R.sm, flexShrink: 0,
      background: C.accentLight, color: C.accent,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      fontWeight: 700, fontSize: FS.xs,
    }}>
      {initials}
    </div>
  );
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

export function PluginFrame({ project, plugin, panel, onOpenPlugins, onClose }: {
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

  const name = humanizeId(plugin.id);
  // Заголовок панели: из манифеста, иначе — имя плагина
  const panelTitle = panel.title || name;

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
    return buildPanelSrcdoc(fetch.html, theme);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- theme намерено вне deps
  }, [fetch]);

  // Ключ iframe: пересборка srcdoc (тема) НЕ перемонтирует (key не меняется),
  // «Перезагрузить» и смена панели (плагин+панель) — да (reloadKey / targetKey).
  const targetKey = `${plugin.id}::${panel.id}`;
  const frameKey = `${targetKey}#${reloadKey}`;

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
        // Упёрлись в потолок — хвост контента отрезан: одна строка в журнал моста
        // («Подробнее»), чтобы автор панели увидел потерю, а не искал её молча.
        if (msg.height > PANEL_CONTENT_MAX_HEIGHT) {
          logEvent(`× высота панели ${Math.round(msg.height)} px обрезана до ${PANEL_CONTENT_MAX_HEIGHT} px`);
        }
        scheduleHeight();
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- актуальное через stateRef
  }, [frameKey, postToFrame, logEvent, handleCall]);

  // === Смена темы: сообщением, без перемонтирования (ADR-031 §3.3) ===
  useEffect(() => {
    if (bridge === 'alive') postToFrame(buildThemeMessage(theme));
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
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: SP.lg,
          display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm, textAlign: 'center',
        }}>
          <div style={{ fontWeight: 600, fontSize: FS.base, color: C.textHeading }}>Панель не найдена</div>
          <div style={{ fontSize: FS.sm, color: C.textMuted }}>У плагина «{name}» панель «{panel.id}» не найдена (404).</div>
          {onOpenPlugins && (
            <Button variant="dashed" size="sm" onClick={onOpenPlugins}>Открыть «Плагины»</Button>
          )}
        </div>
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
          <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', overflowX: 'hidden' }}>
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
          {deniedKey && bridge === 'alive' && (
            <Notice
              tone="warning" icon={ShieldAlert}
              title={`Нет права ${deniedKey}`}
              style={{ position: 'absolute', top: SP.sm, left: SP.sm, right: SP.sm }}
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
        </div>
      );
    }
    // absent: панель не найдена или не объявлена
    return (
      <div style={{
        border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: SP.lg,
        display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm, textAlign: 'center',
      }}>
        <div style={{ fontWeight: 600, fontSize: FS.base, color: C.textHeading }}>Панель не найдена</div>
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>У плагина «{name}» панель «{panel.id}» не объявлена.</div>
        {onOpenPlugins && (
          <Button variant="dashed" size="sm" onClick={onOpenPlugins}>Открыть «Плагины»</Button>
        )}
      </div>
    );
  })();

  // === Десктоп: в PanelShell — только тело + контролы порталом в шапку ===
  if (inShell) {
    return (
      <>
        <PanelHeaderSlot>
          {headerControls}
        </PanelHeaderSlot>
        <div style={{
          flex: 1, minHeight: 0, overflow: 'auto',
          background: C.bgCard, padding: SP.md,
          display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          {body}
        </div>
        {/* Футер: права + версия (макет, экран 3; на мобиле скрыт) */}
        {!isMobile && (
          <div style={{
            flexShrink: 0, padding: '6px 12px',
            background: C.bgInset, borderTop: `1px solid ${C.borderLight}`,
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {`Права: ${plugin.granted.length > 0 ? plugin.granted.join(', ') : 'не выданы'} · v${plugin.version}`}
          </div>
        )}
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

  // === Мобила: свой Island + шапка (переключатель панели + контролы) ===
  return (
    <Island>
      <IslandHeader
        icon={<MiniTile name={name} />}
        title={panelTitle}
        actions={headerControls}
      />

      <div style={{
        flex: 1, minHeight: 0, overflow: 'auto',
        background: C.bgCard, padding: SP.md,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
      }}>
        {body}
      </div>

      {/* Футер: права + версия (макет, экран 3) */}
      <div style={{
        flexShrink: 0, padding: '6px 12px',
        background: C.bgInset, borderTop: `1px solid ${C.borderLight}`,
        fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {`Права: ${plugin.granted.length > 0 ? plugin.granted.join(', ') : 'не выданы'} · v${plugin.version}`}
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
    </Island>
  );
}
