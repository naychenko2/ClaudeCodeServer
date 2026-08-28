import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../../lib/api';
import { C } from '../../lib/design';
import { relTime } from '../../lib/gitFormat';
import type {
  McpBuiltinServer, McpCatalogRevision, McpProbeResult, McpServer, McpServerUpsert, Persona, Project,
} from '../../types';

// Popup входа + таймер опроса «окно закрыли» на сервер — не состояние React: перекладывать
// Window/interval id в setState незачем, а очистка при повторном «Войти» и при получении
// postMessage должна быть синхронной
interface OAuthWindow { win: Window; timer: number; }

// Владелец состояния раздела «MCP-серверы» — один на всю модалку: список серверов,
// наблюдения встроенных, проекты и персоны нужны сразу нескольким вкладкам
// («Серверы» подписывает карточку строкой «кому доступен», «Доступ» рисует те же данные
// матрицей). Паттерн — useProviderData из соседнего раздела «Поставщики моделей».

// === Статусы ===
// Набор значений задаёт бэк (McpServerStatuses): connected | failed | needs-auth | unknown.
// «Не проверялся» — не ошибка, а «ещё не спрашивали»: самый частый статус на старте.
export interface McpStatusTone {
  label: string;
  dot: string;
  text: string;
}

export function mcpStatusTone(status?: string | null): McpStatusTone {
  switch (status) {
    case 'connected': return { label: 'Работает', dot: C.success, text: C.successText };
    case 'needs-auth': return { label: 'Нужен вход', dot: C.warning, text: C.warningText };
    case 'failed': return { label: 'Не отвечает', dot: C.danger, text: C.dangerText };
    default: return { label: 'Не проверялся', dot: C.textMuted, text: C.textMuted };
  }
}

export function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}

// Подпись под карточкой: статус + чем он подтверждён. Число инструментов знает только
// свежая проба (в сторе наблюдений его нет) — поэтому она передаётся отдельно.
export function mcpStatusLine(status: McpServer['status'], probe?: McpProbeResult): string {
  if (!status || status.status === 'unknown') return 'Не проверялся · нажмите «Проверить»';
  if (status.status === 'needs-auth') return 'Сервер требует входа';
  const tone = mcpStatusTone(status.status);
  const parts = [tone.label];
  const tools = probe?.toolCount;
  if (status.status === 'connected' && tools) {
    parts.push(`${tools} ${plural(tools, 'инструмент', 'инструмента', 'инструментов')}`);
  }
  if (status.error) parts.push(status.error);
  const when = relTime(status.observedAt);
  if (when) parts.push(status.source === 'probe' ? `проверен ${when}` : `наблюдался в чатах ${when}`);
  return parts.join(' · ');
}

function formatExpiry(iso?: string | null): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
}

// Строка статуса сервера с OAuth-авторизацией, пока по нему не было ни одной пробы/хода:
// «сервер требует входа» до логина, «вход выполнен · токен действует до …» сразу после —
// не дожидаясь следующей пробы (сразу после успешного входа bx.status снова «не проверялся»,
// иначе карточка на секунду соврала бы, что вход снова нужен)
export function mcpAuthLine(server: McpServer, probe?: McpProbeResult): string {
  const status = server.status?.status;
  const isOAuth = server.auth.kind === 'oauth2';
  if (isOAuth && (status == null || status === 'unknown' || status === 'needs-auth')) {
    if (server.auth.hasTokens) {
      const expiry = formatExpiry(server.auth.expiresAt);
      return expiry ? `Вход выполнен · токен действует до ${expiry}` : 'Вход выполнен';
    }
    return 'Сервер требует входа';
  }
  return mcpStatusLine(server.status, probe);
}

// Строка «кому доступен» на карточке: главный сценарий фичи — человек, выдавший сервер
// одной персоне, через неделю должен понимать, почему у остальных он «не работает».
export function accessSummary(personasOff: number, projectsOff: number): string {
  if (personasOff === 0 && projectsOff === 0) return 'Доступен всем персонам и во всех проектах';
  const parts: string[] = [];
  if (personasOff > 0) {
    parts.push(`у ${personasOff} ${plural(personasOff, 'персоны', 'персон', 'персон')}`);
  }
  if (projectsOff > 0) {
    parts.push(`в ${projectsOff} ${plural(projectsOff, 'проекте', 'проектах', 'проектах')}`);
  }
  return `Выключен ${parts.join(' и ')}`;
}

// Та же строка карточки, но для allow-модели (флаг mcp-allowlist): показываем, кому
// сервер РЕАЛЬНО выдан, а не кого он обходит — «нигде не включён» здесь норма, а не поломка.
export function accessSummaryOn(personasOn: number, projectsOn: number, outsideOn: boolean): string {
  if (personasOn === 0 && projectsOn === 0 && !outsideOn) return 'Доступ не выдан';
  const parts: string[] = [];
  if (projectsOn > 0) parts.push(`${projectsOn} ${plural(projectsOn, 'проект', 'проекта', 'проектов')}`);
  if (outsideOn) parts.push('чаты вне проектов');
  if (personasOn > 0) parts.push(`${personasOn} ${plural(personasOn, 'персона', 'персоны', 'персон')}`);
  return `Работает: ${parts.join(' · ')}`;
}

// Персона выключила сервер Off-привязкой «mcp:<ключ>» (по умолчанию сервер ей доступен)
export function personaOffFor(persona: Persona, serverKey: string): boolean {
  const target = `mcp:${serverKey}`.toLowerCase();
  return (persona.bindings ?? []).some(b =>
    b.type === 'tool' && b.mode === 'off' && b.target.toLowerCase() === target);
}

// Персоне явно выдан доступ (allow-модель, флаг mcp-allowlist): привязка «mcp:<ключ>»
// с Mode != Off. Та же таблица привязок, что читает студия персоны и деливери хода.
export function personaBindingFor(persona: Persona, serverKey: string) {
  const target = `mcp:${serverKey}`.toLowerCase();
  return (persona.bindings ?? []).find(b => b.type === 'tool' && b.target.toLowerCase() === target);
}

export function personaGrantedFor(persona: Persona, serverKey: string): boolean {
  const b = personaBindingFor(persona, serverKey);
  return !!b && b.mode !== 'off';
}

export interface McpData {
  servers: McpServer[] | null;      // null — ещё грузится
  builtin: McpBuiltinServer[];
  projects: Project[];
  personas: Persona[];
  error: string | null;
  setError: (e: string | null) => void;
  // Идёт проба этой карточки: ожидание локальное, остальной список живёт дальше
  checking: Record<string, boolean>;
  probes: Record<string, McpProbeResult>;
  reload: () => void;
  setEnabled: (server: McpServer, enabled: boolean) => void;
  // Проба каталожной stdio-записи у local-владельца требует подтверждения: бэк
  // отдаёт 400 с {requiresConfirmation, command}. Вызывающий рисует диалог с этой
  // строкой запуска и по согласию зовёт confirmProbe. 'done' — проба закончилась:
  // либо успех, либо ошибка, которая уже легла в error
  probe: (server: McpServer) => Promise<
    { kind: 'done' } | { kind: 'needsConfirmation'; command: string }
  >;
  confirmProbe: (server: McpServer) => Promise<void>;
  remove: (server: McpServer) => Promise<void>;
  save: (id: string | null, data: McpServerUpsert) => Promise<McpServer>;
  importJson: (fragment: unknown) => Promise<{ created: McpServer[]; skipped: { key: string; reason: string }[] }>;
  // Allow-модель: выдача доступа, а не исключение из него. Включение каталожной
  // stdio-записи у local-владельца возвращает needsConfirmation — вызывающий
  // показывает диалог с командой и зовёт confirmSetProjectOn по согласию
  setProjectOn: (project: Project, serverKey: string, on: boolean) => Promise<
    { kind: 'ok' } | { kind: 'needsConfirmation'; servers: { key: string; command: string }[] }
  >;
  confirmSetProjectOn: (project: Project, serverKey: string) => Promise<void>;
  projectsOnCount: (serverKey: string) => number;
  personasOnCount: (serverKey: string) => number;
  grantPersona: (persona: Persona, serverKey: string) => Promise<void>;
  revokePersona: (persona: Persona, serverKey: string) => Promise<void>;
  // Вход по OAuth (волна 7). oauthPending[serverId] — открыто окно провайдера, ждём
  // postMessage; oauthNotice[serverId] — сообщение под карточкой (окно закрыли / отказ)
  oauthPending: Record<string, boolean>;
  oauthNotice: Record<string, string>;
  startOAuth: (server: McpServer, clientId?: string) => Promise<void>;
  // true — код принят; false — отказ (сообщение уже легло в oauthNotice), вызывающий
  // решает, чистить ли форму — на отказе поле с кодом должно остаться для повтора
  completeOAuth: (server: McpServer, code: string) => Promise<boolean>;
  dismissOAuthNotice: (server: McpServer) => void;
  // Ревизия каталожных записей (волна 2): ключ — McpCatalogRef.name. Карточка сервера
  // ищет ревизию по своему catalogRef.name. checkFailed: запрос целиком не дошёл —
  // показываем «проверить не удалось» нейтрально у всех карточек с catalogRef (НЕ
  // пугаем отзывом — реестр в preview, лежать ему не запрещено)
  revisions: Record<string, McpCatalogRevision>;
  revisionsCheckFailed: boolean;
}

export function useMcpData(): McpData {
  const [servers, setServers] = useState<McpServer[] | null>(null);
  const [builtin, setBuiltin] = useState<McpBuiltinServer[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [personas, setPersonas] = useState<Persona[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState<Record<string, boolean>>({});
  const [probes, setProbes] = useState<Record<string, McpProbeResult>>({});

  // Порядковый номер записи на сущность: тумблеры бьют пачкой (человек щёлкает подряд),
  // ответы приходят не в том порядке — без счётчика устаревший ответ перезаписывал бы
  // актуальное состояние. Тот же приём, что в handleSaveLayer «Поставщиков моделей».
  const saveSeq = useRef<Record<string, number>>({});

  const loadServers = useCallback(() => {
    api.mcp.list()
      .then(list => setServers(list))
      .catch(e => { setServers([]); setError(msg(e, 'Не удалось загрузить список серверов')); });
    api.mcp.builtin()
      .then(list => setBuiltin(list))
      .catch(() => { /* плитки встроенных не критичны — покажем без них */ });
  }, []);

  useEffect(() => {
    loadServers();
    api.projects.list().then(setProjects).catch(() => { /* матрица доступа покажет пусто */ });
    api.personas.list().then(setPersonas).catch(() => { /* обзор персон покажет пусто */ });
  }, [loadServers]);

  // Ревизия каталожных записей — ОТДЕЛЬНЫЙ эффект (не внутри loadServers). Следит за
  // servers: когда приходит свежий список, отбирает уникальные name из catalogRef и
  // зовёт api.mcp.catalogRevisions. TTL 1 час на фронте: модалка могла открываться
  // и закрываться — повторно в течение часа не переспрашиваем. На мутациях (save,
  // remove) ключевой набор имён может измениться — invalidate вызывается явно ниже
  useEffect(() => {
    if (servers === null) return;
    const names = new Set<string>();
    for (const s of servers) {
      const n = s.catalogRef?.name;
      if (n) names.add(n);
    }
    if (names.size === 0) {
      setRevisions({});
      setRevisionsCheckFailed(false);
      return;
    }
    const ONE_HOUR = 60 * 60 * 1000;
    if (Date.now() - revisionsCheckedAt.current < ONE_HOUR) return;
    const seq = ++revisionsSeq.current;
    api.mcp.catalogRevisions([...names]).then(res => {
      if (revisionsSeq.current !== seq) return; // устаревший ответ
      revisionsCheckedAt.current = Date.now();
      const next: Record<string, McpCatalogRevision> = {};
      // Бэк возвращает items только по тем именам, по которым что-то нашлось.
      // «Пропавшие» (реестр ответил, но конкретно эту запись не нашёл) считаем
      // как отсутствие ревизии: никакой плашки на карточке не рисуем, чтобы
      // «проверить не удалось» и «запись пропала из реестра» не путались. missing
      // как поле DTO больше нет — фронт выводит «нет ответа по этой записи» из
      // пустого ключа в next
      for (const r of res.items ?? []) next[r.name] = r;
      setRevisions(next);
      setRevisionsCheckFailed(!!res.checkFailed);
    }).catch(() => {
      if (revisionsSeq.current !== seq) return;
      revisionsCheckedAt.current = Date.now();
      // Общий отказ батча: НЕ молчим (отзыв тут не рисуем, но нейтральная пометка
      // «проверить не удалось» у карточек с catalogRef полезна — иначе человек
      // решит, что всё ок, и не догадается проверить руками)
      setRevisionsCheckFailed(true);
    });
  }, [servers]);

  const replace = (updated: McpServer) =>
    setServers(list => list?.map(s => (s.id === updated.id ? updated : s)) ?? list);

  // После входа обновляем только эту карточку (новый статус, срок токена), не трогая
  // остальной список — полная перезагрузка сбросила бы независимое состояние других карточек
  const refreshOne = useCallback(
    (id: string) => api.mcp.get(id).then(replace).catch(() => loadServers()),
    [loadServers],
  );

  const setEnabled = (server: McpServer, enabled: boolean) => {
    const seq = (saveSeq.current[server.id] ?? 0) + 1;
    saveSeq.current[server.id] = seq;
    const prev = server;
    replace({ ...server, enabled });
    setError(null);
    api.mcp.setEnabled(server.id, enabled)
      .then(saved => { if (saveSeq.current[server.id] === seq) replace(saved); })
      .catch(e => {
        if (saveSeq.current[server.id] !== seq) return;
        replace(prev);
        setError(msg(e, 'Не удалось сохранить'));
      });
  };

  // Проба «по кнопке». Каталожная stdio-запись у local-владельца запустится на машине
  // человека, поэтому бэк отказывает без подтверждения (400 + requiresConfirmation +
  // полная строка запуска). Возвращаем это вызывающему вместо общей плашки «Проверка
  // не удалась»: та читалась как «сервер не отвечает», хотя разрешения просто не спросили
  const runProbe = async (server: McpServer, confirmed: boolean): Promise<
    { kind: 'done' } | { kind: 'needsConfirmation'; command: string }
  > => {
    if (checking[server.id]) return { kind: 'done' };
    setChecking(c => ({ ...c, [server.id]: true }));
    setError(null);
    try {
      const result = await api.mcp.probe(server.id, confirmed ? { confirmed: true } : undefined);
      setProbes(p => ({ ...p, [server.id]: result }));
      // Наблюдение уже записано на бэке: тот же результат кладём в карточку,
      // чтобы статус и время проверки обновились без перезапроса списка
      replace({
        ...server,
        status: {
          status: result.status,
          observedAt: new Date().toISOString(),
          source: 'probe',
          sessionId: null,
          error: result.error ?? null,
        },
      });
      return { kind: 'done' };
    } catch (e) {
      const body = (e as { body?: { requiresConfirmation?: boolean; command?: string } } | null)?.body;
      if (!confirmed && body?.requiresConfirmation) {
        // Ошибку НЕ показываем: отказа не было, был вопрос — его задаёт диалог
        return { kind: 'needsConfirmation', command: body.command ?? '' };
      }
      setError(msg(e, 'Проверка не удалась'));
      return { kind: 'done' };
    } finally {
      setChecking(c => ({ ...c, [server.id]: false }));
    }
  };

  const probe = (server: McpServer) => runProbe(server, false);

  // Повторная проба с confirmed=true — после согласия в диалоге
  const confirmProbe = async (server: McpServer) => { await runProbe(server, true); };

  // state сервера (не React-стейт, ключ oauth/start) → id записи, ждущей ответа окна входа
  const [oauthSessions, setOauthSessions] = useState<Record<string, { key: string; state: string }>>({});
  const [oauthNotice, setOauthNotice] = useState<Record<string, string>>({});
  const oauthWindows = useRef<Record<string, OAuthWindow>>({});

  // Ревизия каталожных записей в реестре (волна 2). Запрос идёт ОТДЕЛЬНО от списка
  // серверов и ПОСЛЕ его отрисовки: реестр в статусе preview может лежать, а раздел
  // обязан открываться. TTL кэша — час: фронт сам не дёргает ревизию чаще, даже если
  // модалка переоткроется. Инвалидация — любая мутация, которая могла поменять состав
  // каталожных записей (save/remove/reload)
  const [revisions, setRevisions] = useState<Record<string, McpCatalogRevision>>({});
  const [revisionsCheckFailed, setRevisionsCheckFailed] = useState(false);
  const revisionsCheckedAt = useRef<number>(0);
  // Защита от гонок: параллельные перезагрузки списка могли бы стартовать два запроса,
  // и более старый ответ перезаписал бы свежий. Тот же приём, что в probe/setEnabled
  const revisionsSeq = useRef(0);

  const stopOAuthPoll = (id: string) => {
    const w = oauthWindows.current[id];
    if (w) { window.clearInterval(w.timer); delete oauthWindows.current[id]; }
  };

  const startOAuth = async (server: McpServer, clientId?: string) => {
    dismissOAuthNotice(server);
    stopOAuthPoll(server.id);
    try {
      const started = await api.mcp.oauthStart(server.id, clientId);
      const win = window.open(started.authorizeUrl, 'mcp-oauth', 'width=520,height=720');
      if (!win) {
        setOauthNotice(n => ({ ...n, [server.id]: 'Не удалось открыть окно входа — разрешите всплывающие окна и попробуйте снова' }));
        return;
      }
      setOauthSessions(p => ({ ...p, [server.id]: { key: server.key, state: started.state } }));
      const timer = window.setInterval(() => {
        if (!win.closed) return;
        window.clearInterval(timer);
        delete oauthWindows.current[server.id];
        setOauthSessions(p => {
          if (!p[server.id]) return p; // уже разрешилось сообщением от callback-страницы
          const next = { ...p };
          delete next[server.id];
          return next;
        });
        setOauthNotice(n => ({ ...n, [server.id]: 'Окно закрыто — вход не завершён' }));
      }, 500);
      oauthWindows.current[server.id] = { win, timer };
    } catch (e) {
      setOauthNotice(n => ({ ...n, [server.id]: msg(e, 'Не удалось начать вход') }));
    }
  };

  const completeOAuth = async (server: McpServer, code: string): Promise<boolean> => {
    const session = oauthSessions[server.id];
    if (!session) {
      setOauthNotice(n => ({ ...n, [server.id]: 'Сессия входа истекла — нажмите «Войти» заново' }));
      return false;
    }
    try {
      await api.mcp.oauthComplete(server.id, session.state, code.trim());
      stopOAuthPoll(server.id);
      setOauthSessions(p => { const n = { ...p }; delete n[server.id]; return n; });
      setOauthNotice(n => { const c = { ...n }; delete c[server.id]; return c; });
      refreshOne(server.id);
      return true;
    } catch (e) {
      setOauthNotice(n => ({ ...n, [server.id]: msg(e, 'Код не принят') }));
      return false;
    }
  };

  const dismissOAuthNotice = (server: McpServer) =>
    setOauthNotice(n => { if (!(server.id in n)) return n; const c = { ...n }; delete c[server.id]; return c; });

  // Ответ окна провайдера: страница /api/mcp/oauth/callback шлёт postMessage опубликовавшему
  // окну и закрывается сама. Сверяем по ключу сервера — он в сообщении есть, id pending-записи нет
  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      const payload = e.data as { type?: string; ok?: boolean; key?: string; error?: string } | null;
      if (!payload || payload.type !== 'mcp-oauth') return;
      const id = Object.keys(oauthSessions).find(sid => oauthSessions[sid].key === payload.key);
      if (!id) return;
      stopOAuthPoll(id);
      setOauthSessions(p => { const n = { ...p }; delete n[id]; return n; });
      if (payload.ok) {
        setOauthNotice(n => { if (!(id in n)) return n; const c = { ...n }; delete c[id]; return c; });
        refreshOne(id);
      } else {
        setOauthNotice(n => ({ ...n, [id]: payload.error || 'Вход не удался' }));
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [oauthSessions, refreshOne]);

  const remove = async (server: McpServer) => {
    await api.mcp.delete(server.id);
    setServers(list => list?.filter(s => s.id !== server.id) ?? list);
    // Сервер мог быть выключен в проектах — deny-list на бэке чистится вместе с записью
    // только у персон, поэтому список проектов перечитываем
    api.projects.list().then(setProjects).catch(() => { /* не критично */ });
    // Удаление каталожной записи могло изменить набор имён — следующий рендер списка
    // сходит в реестр. Сбрасываем метку времени, чтобы эффект не ждал положенный час
    revisionsCheckedAt.current = 0;
  };

  const save = async (id: string | null, data: McpServerUpsert) => {
    const saved = id ? await api.mcp.update(id, data) : await api.mcp.create(data);
    setServers(list => {
      if (!list) return [saved];
      return id ? list.map(s => (s.id === saved.id ? saved : s)) : [...list, saved];
    });
    // Правка каталожной записи могла сменить version/name (или удалить catalogRef вовсе).
    // Невалидируем — следующий рендер сходит в реестр. Создание новой записи с catalogRef
    // тоже меняет набор имён, та же логика
    revisionsCheckedAt.current = 0;
    return saved;
  };

  const importJson = async (fragment: unknown) => {
    const result = await api.mcp.import(fragment);
    if (result.created.length > 0) loadServers();
    return result;
  };

  // Allow-list проекта правится тем же PUT /api/projects/{id}, что и остальные настройки.
  // Включение каталожной stdio-записи у local-владельца требует подтверждения: бэк
  // отдаёт 400 с {requiresConfirmation, servers[{key, command}]}. Резолв промиса —
  // способ синхронизировать UI вызывающего (McpAccessTab) с решением бэка, не
  // заводя второй стор pending-подтверждений
  const setProjectOn = (project: Project, serverKey: string, on: boolean): Promise<
    { kind: 'ok' } | { kind: 'needsConfirmation'; servers: { key: string; command: string }[] }
  > => {
    const current = project.mcpServersOn ?? [];
    const next = on
      ? [...current.filter(k => k !== serverKey), serverKey]
      : current.filter(k => k !== serverKey);
    const seq = (saveSeq.current[project.id] ?? 0) + 1;
    saveSeq.current[project.id] = seq;
    setProjects(list => list.map(p => (p.id === project.id ? { ...p, mcpServersOn: next } : p)));
    setError(null);
    return api.projects.update(project.id, { mcpServersOn: next })
      .then(saved => {
        if (saveSeq.current[project.id] !== seq) return { kind: 'ok' as const };
        setProjects(list => list.map(p => (p.id === saved.id ? saved : p)));
        return { kind: 'ok' as const };
      })
      .catch((e: unknown) => {
        if (saveSeq.current[project.id] !== seq) throw e;
        setProjects(list => list.map(p => (p.id === project.id ? project : p)));
        const body = (e as { body?: { requiresConfirmation?: boolean; servers?: { key: string; command: string }[] } } | null)?.body;
        if (body?.requiresConfirmation && body.servers) {
          // Откатываем оптимистичный апдейт: сервер ещё не включён, решение — за человеком
          return { kind: 'needsConfirmation' as const, servers: body.servers };
        }
        setError(msg(e, 'Не удалось сохранить'));
        throw e;
      });
  };

  // Повторный запрос с mcpCatalogConfirmed=true — после согласия в диалоге
  const confirmSetProjectOn = (project: Project, serverKey: string): Promise<void> => {
    const current = project.mcpServersOn ?? [];
    const next = [...current.filter(k => k !== serverKey), serverKey];
    const seq = (saveSeq.current[project.id] ?? 0) + 1;
    saveSeq.current[project.id] = seq;
    return api.projects.update(project.id, { mcpServersOn: next, mcpCatalogConfirmed: true })
      .then(saved => {
        if (saveSeq.current[project.id] !== seq) return;
        setProjects(list => list.map(p => (p.id === saved.id ? saved : p)));
      })
      .catch((e: unknown) => {
        if (saveSeq.current[project.id] !== seq) throw e;
        setError(msg(e, 'Не удалось сохранить'));
      });
  };

  const projectsOnCount = (serverKey: string) =>
    projects.filter(p => (p.mcpServersOn ?? []).includes(serverKey)).length;

  const personasOnCount = (serverKey: string) =>
    personas.filter(p => personaGrantedFor(p, serverKey)).length;

  // Выдача/отзыв доступа персоне — та же Tool-привязка «mcp:<ключ>», что правит студия
  // персоны (PersonaBindingsService): второго источника истины у allow-модели не заводим.
  const grantPersona = async (persona: Persona, serverKey: string) => {
    const target = `mcp:${serverKey}`;
    const existing = personaBindingFor(persona, serverKey);
    const saved = existing
      ? await api.personas.updateBinding(persona.id, existing.id, { type: 'tool', target, mode: 'always' })
      : await api.personas.addBinding(persona.id, { type: 'tool', target, mode: 'always' });
    setPersonas(list => list.map(p => {
      if (p.id !== persona.id) return p;
      const bindings = (p.bindings ?? []).filter(b => b.id !== saved.id);
      return { ...p, bindings: [...bindings, saved] };
    }));
  };

  const revokePersona = async (persona: Persona, serverKey: string) => {
    const existing = personaBindingFor(persona, serverKey);
    if (!existing) return;
    await api.personas.removeBinding(persona.id, existing.id);
    setPersonas(list => list.map(p => (p.id === persona.id
      ? { ...p, bindings: (p.bindings ?? []).filter(b => b.id !== existing.id) }
      : p)));
  };

  const oauthPending: Record<string, boolean> = {};
  for (const id of Object.keys(oauthSessions)) oauthPending[id] = true;

  return {
    servers, builtin, projects, personas, error, setError,
    checking, probes, reload: loadServers,
    setEnabled, probe, confirmProbe, remove, save, importJson,
    setProjectOn, confirmSetProjectOn, projectsOnCount, personasOnCount, grantPersona, revokePersona,
    oauthPending, oauthNotice, startOAuth, completeOAuth, dismissOAuthNotice,
    revisions, revisionsCheckFailed,
  };
}

function msg(e: unknown, fallback: string): string {
  return e instanceof Error && e.message ? e.message : fallback;
}
