import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { AlertTriangle, MoreHorizontal, Plug, RefreshCw, ShieldAlert, X } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Island, IslandHeader } from '../../components/ui/Island';
import { Button, IconButton, Menu, MenuItem, MenuSep, Modal, Notice } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { getEffectiveTheme, subscribeThemeMode } from '../../lib/themeMode';
import { useProjectFeature } from '../../lib/projectCapabilities';
import { ProjectFeature, type Project } from '../../types';
import { api, type PluginInfo, type PluginPanelView } from '../../lib/api';
import { useInstalledPlugins } from './useInstalledPlugins';
import { ConsentDialog, type PluginConsentData } from './ConsentDialog';
import {
  BRIDGE_VERSION, READY_TIMEOUT_MS, MAX_PANEL_FILE_BYTES,
  buildInitMessage, buildPanelSrcdoc, buildResultMessage, buildThemeMessage,
  bridgeErrorText, clampPanelHeight, deniedKeyOf, panelWindowOf, validateFrameMessage,
  type BridgeErrorCode, type FrameMessage, type HostMessage,
} from './bridge';

// === Слот панели плагина в sidebar (экран 3 макетов, ADR-031 §3, флаг plugins-mvp) ===
// Остров «Панели плагинов» в боковой колонке. Тело — sandbox-iframe
// (sandbox="allow-scripts" БЕЗ allow-same-origin) по мосту v1: хост забирает
// panel/index.html по REST (user JWT), вставляет CSP и шим plugin-kit в srcdoc.
// JWT и стор хоста во фрейм не передаются — панель видит только init/result/theme/
// context и отвечает ready/call/resize (ADR-031, инвариант 1).
//
// Гейт V5: слота нет, пока нет ни одного установленного плагина (WorkspacePage).
// Панель берём у ПЕРВОГО установленного и ВКЛЮЧЕННОГО плагина: выключенный
// не монтирует панели (бэк отвечает 409), панелей у плагина может не быть (404) —
// тогда пустое состояние.

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

// Панель, которую показывает слот: плагин + конкретная панель (её entry). У плагина
// может быть несколько панелей — переключатель выбирает панель, а не только плагин.
type PanelTarget = { plugin: PluginInfo; panel: PluginPanelView };
// Ключ панели: id плагина + id панели (id панели уникален в пределах пакета)
const targetKey = (t: PanelTarget) => `${t.plugin.id}::${t.panel.id}`;

// Мини-плитка 20×20 с инициалами (шапка острова, макет экран 3).
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

export function PluginFrame({ project, onOpenPlugins, onClose }: {
  project: Project;
  // «Открыть в «Плагинах»» — настройки проекта (секция плагинов)
  onOpenPlugins?: () => void;
  // «Скрыть панель» — закрыть панельную зону
  onClose?: () => void;
}) {
  const isMobile = useIsMobile();
  const pluginsAvail = useProjectFeature(project, ProjectFeature.Plugins);
  const { plugins, loading } = useInstalledPlugins(project.id, pluginsAvail);
  // Эффективная тема хоста: в init и в theme-сообщениях (без перемонтирования)
  const theme = useSyncExternalStore(subscribeThemeMode, getEffectiveTheme, getEffectiveTheme);

  const [menuOpen, setMenuOpen] = useState(false);
  const [fetch, setFetch] = useState<FetchState>({ kind: 'absent' });
  const [bridge, setBridge] = useState<BridgeState>('waiting');
  const [height, setHeight] = useState<number | null>(null);
  const [deniedKey, setDeniedKey] = useState<string | null>(null);
  const [log, setLog] = useState<string[]>([]);
  const [logOpen, setLogOpen] = useState(false);
  const [consent, setConsent] = useState<PluginConsentData | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  // Панели — у включённых плагинов, у которых панель ОБЪЯВЛЕНА (contributes.panels):
  // включённый без панели не даёт «панелей нет», если у другого включённого панель есть.
  // Выключенный не монтирует панели (бэк — 409). Плоский список (плагин, панель):
  // у плагина может быть несколько панелей — переключатель выбирает панель, не плагин.
  const panelTargets = useMemo<PanelTarget[]>(() => {
    const out: PanelTarget[] = [];
    for (const p of plugins) {
      if (!p.enabled) continue;
      for (const panel of p.contributes?.panels ?? []) out.push({ plugin: p, panel });
    }
    return out;
  }, [plugins]);
  // Выбранная панель (переключатель, когда панелей несколько); null → первая
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [switcherOpen, setSwitcherOpen] = useState(false);
  const activeTarget = useMemo<PanelTarget | null>(
    () => panelTargets.find(t => targetKey(t) === selectedKey) ?? panelTargets[0] ?? null,
    [panelTargets, selectedKey],
  );
  // Плагин активной панели (id/version/granted) и сама панель (entry для panelHtml)
  const panelPlugin = activeTarget?.plugin ?? null;
  const activePanel = activeTarget?.panel ?? null;
  const activeKey = activeTarget ? targetKey(activeTarget) : null;

  const iframeRef = useRef<HTMLIFrameElement | null>(null);
  // init отправлен в текущем монтировании (повторный ready игнорируется, ADR §3.3)
  const initedRef = useRef(false);
  // Вызовы в полёте: потолок MAX_INFLIGHT_CALLS (лишний — result `limit`)
  const inflightRef = useRef(0);
  // Актуальные значения для message-обработчика без пересоздания подписки
  const stateRef = useRef({ theme, project, isMobile, panelPlugin: null as PluginInfo | null, bridge, reloadKey });
  stateRef.current = { theme, project, isMobile, panelPlugin: panelPlugin, bridge, reloadKey };

  const name = panelPlugin ? humanizeId(panelPlugin.id) : '';

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
  // entry — из манифеста выбранной панели (contributes.panels[].entry); маршрут
  // {**path} — catch-all, слэши entry остаются литеральными.
  useEffect(() => {
    let cancelled = false;
    initedRef.current = false;
    inflightRef.current = 0;
    setBridge('waiting');
    setHeight(null);
    setDeniedKey(null);
    setLog([]);
    if (!activeTarget || !activePanel) {
      setFetch({ kind: 'absent' });
      return;
    }
    setFetch({ kind: 'fetching' });
    api.plugins.panelHtml(activeTarget.plugin.id, activeTarget.plugin.version, activePanel.entry)
      .then(html => {
        if (cancelled) return;
        setFetch({ kind: 'html', html });
        logEvent(`← ${activePanel.entry} загружен`);
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
    // deps — примитивы (id/version/entry), а не объект activeTarget: useMemo не
    // кэширует в мини-раннере тестов, и объект был бы новой ссылкой каждый render →
    // эффект зря сбрасывал fetch. В реальном React useMemo стабильна, но строки
    // корректны и там (перезапуск только при смене панели/версии/entry).
  }, [activeTarget?.plugin.id, activeTarget?.plugin.version, activeTarget?.panel.entry, reloadKey, logEvent]);

  // srcdoc: оболочка (CSP + iframe) + внутренний документ (CSP + шим + html панели).
  // Пересобирается при смене панели/версии, но НЕ при смене темы: тема доходит
  // сообщением (шим проставляет data-cc-theme, см. эффект ниже), состояние панели
  // держится. Панель больше MAX_PANEL_FILE_BYTES — srcdoc null (показываем ошибку).
  const srcdoc = useMemo(() => {
    if (fetch.kind !== 'html') return null;
    if (fetch.html.length > MAX_PANEL_FILE_BYTES) return null;
    return buildPanelSrcdoc(fetch.html);
  }, [fetch]);

  // Ключ iframe: пересборка srcdoc (тема) НЕ перемонтирует (key не меняется),
  // «Перезагрузить» и смена панели (плагин+панель) — да (reloadKey / targetKey).
  const frameKey = `${activeTarget ? targetKey(activeTarget) : 'none'}#${reloadKey}`;

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
    const plugin = s.panelPlugin;
    if (!plugin) return;
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
      const res = await api.plugins.call(plugin.id, {
        op: msg.op,
        args,
        context: { projectId: s.project.id },
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
        if (s.panelPlugin) {
          postToFrame(buildInitMessage({
            plugin: { id: s.panelPlugin.id, version: s.panelPlugin.version },
            slot: 'sidebar',
            context: { projectId: s.project.id },
            theme: s.theme,
            locale: typeof navigator !== 'undefined' ? navigator.language : '',
            granted: s.panelPlugin.granted,
          }));
        }
        setBridge('alive');
        logEvent('← ready → init');
      } else if (msg.type === 'call') {
        void handleCall(msg);
      } else if (msg.type === 'resize') {
        setHeight(clampPanelHeight(msg.height, s.isMobile));
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
    if (!panelPlugin) return;
    await api.plugins.grant(panelPlugin.id, permissions, secrets);
    setConsent(null);
  };

  // === Гейты: без плагинов слота нет вовсе (V5) ===
  if (!pluginsAvail) return null;
  if (!loading && plugins.length === 0) return null;

  // === Шапка: плитка + имя + (reload | точка) + ⋯ ===
  const isBridgeError = bridge === 'bridge_timeout' || bridge === 'bridge_bad_message' || bridge === 'bridge_version';
  // Iframe монтируется, как только есть html (и в waiting, и в alive): шим внутри
  // шлёт ready, и только host отвечает init. Скелет до ready — оверлеем поверх.
  const showIframe = fetch.kind === 'html' && srcdoc !== null;

  const headerActions = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 2 }}>
      {/* Переключатель панели: когда панелей несколько (в т.ч. у одного плагина) —
          выбор, какую показывать; label — название панели, hint — плагин для различения */}
      {panelTargets.length > 1 && (
        <div style={{ position: 'relative' }}>
          <IconButton size="xs" tone="muted" ariaLabel="Выбрать панель" onClick={() => setSwitcherOpen(o => !o)}>
            <Plug size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
          {switcherOpen && (
            <Menu onClose={() => setSwitcherOpen(false)} align="right" top={-2}>
              {panelTargets.map(t => (
                <MenuItem
                  key={targetKey(t)}
                  label={t.panel.title || humanizeId(t.plugin.id)}
                  hint={activeKey === targetKey(t) ? `показывается · ${humanizeId(t.plugin.id)}` : humanizeId(t.plugin.id)}
                  onClick={() => { setSelectedKey(targetKey(t)); setSwitcherOpen(false); }}
                />
              ))}
            </Menu>
          )}
        </div>
      )}
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
            <MenuItem label="Права…" onClick={() => { setMenuOpen(false); if (panelPlugin) setConsent(toConsentData(panelPlugin)); }} />
            {onOpenPlugins && <MenuItem label="Открыть в «Плагинах»" onClick={() => { setMenuOpen(false); onOpenPlugins(); }} />}
            {(panelPlugin || onOpenPlugins) && <MenuSep />}
            {onClose && <MenuItem label="Скрыть панель" onClick={() => { setMenuOpen(false); onClose(); }} />}
          </Menu>
        )}
      </div>
    </div>
  );

  // Тело по состояниям макета (экран 3)
  const body = (() => {
    if (loading) {
      return <LoadingSkeleton />;
    }
    if (fetch.kind === 'noPanel') {
      return (
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: SP.lg,
          display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm, textAlign: 'center',
        }}>
          <div style={{ fontWeight: 600, fontSize: FS.base, color: C.textHeading }}>Здесь будут панели плагинов</div>
          <div style={{ fontSize: FS.sm, color: C.textMuted }}>У установленных плагинов панелей нет.</div>
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
    // Панель больше потолка (256 КиБ): не монтируем, показываем понятную ошибку
    if (fetch.kind === 'html' && fetch.html.length > MAX_PANEL_FILE_BYTES) {
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
        <div style={{ position: 'relative', flex: 1, minHeight: 0, overflow: 'auto' }}>
          <iframe
            ref={iframeRef}
            key={frameKey}
            srcDoc={srcdoc}
            sandbox="allow-scripts"
            title={`Панель плагина ${name}`}
            style={{
              width: '100%',
              height: height ?? '100%',
              border: 'none', display: 'block',
              background: 'transparent',
            }}
          />
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
                onClick={() => { if (panelPlugin) setConsent(toConsentData(panelPlugin)); }}
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
    // absent: плагины есть, но ни один не включён — как «панелей нет»
    return (
      <div style={{
        border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: SP.lg,
        display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm, textAlign: 'center',
      }}>
        <div style={{ fontWeight: 600, fontSize: FS.base, color: C.textHeading }}>Здесь будут панели плагинов</div>
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>У установленных плагинов панелей нет.</div>
        {onOpenPlugins && (
          <Button variant="dashed" size="sm" onClick={onOpenPlugins}>Открыть «Плагины»</Button>
        )}
      </div>
    );
  })();

  return (
    <Island>
      <IslandHeader
        icon={panelPlugin ? <MiniTile name={name} /> : <Plug size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.textMuted }} />}
        title={panelPlugin ? name : 'Панели плагинов'}
        actions={headerActions}
      />

      <div style={{
        flex: 1, minHeight: 0, overflow: 'auto',
        background: C.bgCard, padding: SP.md,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
      }}>
        {body}
      </div>

      {/* Футер: права + версия (макет, экран 3; на мобиле скрыт) */}
      {!isMobile && panelPlugin && (
        <div style={{
          flexShrink: 0, padding: '6px 12px',
          background: C.bgInset, borderTop: `1px solid ${C.borderLight}`,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {`Права: ${panelPlugin.granted.length > 0 ? panelPlugin.granted.join(', ') : 'не выданы'} · v${panelPlugin.version}`}
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
    </Island>
  );
}
