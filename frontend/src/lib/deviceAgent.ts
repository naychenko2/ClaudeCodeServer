// Клиент localhost-API агента устройства (ADR-016 §5, задачи 4.4 и 4.4б). Файлы, git, сервисы
// и навыки локального проекта браузер берёт не у сервера, а у агента на своей машине: маршруты и
// форма ответов те же, что у контроллеров сервера, меняется только база адреса и способ входа — вместо
// JWT сервера короткий билет, который выдаёт сервер (POST /api/projects/{id}/device-agent/ticket).
//
// «Сервер или агент» решает матрица (projectFilesRoute в projectCapabilities.ts); здесь только
// память о решении по id проекта — api.ts знает id, а не сам проект.

import { useEffect, useSyncExternalStore } from 'react';
import type { Project } from '../types';
import { request } from './offline';
import { projectFilesRoute, type ProjectFilesRoute } from './projectCapabilities';
import { agentServesPath } from './deviceAgentRoutes';

export const DEVICE_AGENT_DEFAULT_PORT = 47318;
const DEFAULT_TICKET_HEADER = 'X-Agent-Ticket';
// Билет живёт до 5 минут; перевыпускаем, когда до конца осталось меньше минуты
const TICKET_REFRESH_MARGIN_MS = 60_000;
const TICKET_KEEPALIVE_MS = 60_000;
const AGENT_TIMEOUT_MS = 30_000;

export const DEVICE_AGENT_UNSUPPORTED_TEXT = 'У локального проекта это действие пока недоступно: агент устройства его не умеет';

// ---------- маршрут проекта ----------

interface ProjectRouting { route: ProjectFilesRoute; showHidden: boolean }
const routing = new Map<string, ProjectRouting>();

// Запомнить, куда ходят файлы проекта. Зовётся на каждом ответе сервера с проектом (api.ts)
// и из панелей (useDeviceAgent) — у кого проект на руках, тот и обновляет.
export function noteProject(project: Project | null | undefined): void {
  if (!project) return;
  routing.set(project.id, { route: projectFilesRoute(project), showHidden: project.showHiddenFiles === true });
}

export function noteProjects(projects: Project[]): void {
  for (const p of projects) noteProject(p);
}

export function projectRouteOf(projectId: string): ProjectFilesRoute {
  return routing.get(projectId)?.route ?? 'server';
}

// ---------- состояние связи с агентом ----------

export type DeviceAgentStatus =
  | { kind: 'checking' }
  | { kind: 'ready' }
  // Агент на этой машине не отвечает: не запущен или открыто не с машины проекта
  | { kind: 'unreachable' }
  // Сервер не выдал билет (флаг выключен, устройство офлайн, нет доступа)
  | { kind: 'refused'; reason: string }
  // Агент ответил, но билет не принял: это агент другого устройства или корень не разрешён
  | { kind: 'rejected'; reason: string };

const CHECKING: DeviceAgentStatus = { kind: 'checking' };
const statuses = new Map<string, DeviceAgentStatus>();
const listeners = new Set<() => void>();

function setStatus(projectId: string, status: DeviceAgentStatus): void {
  const prev = statuses.get(projectId);
  if (prev && prev.kind === status.kind && ('reason' in prev ? prev.reason : null) === ('reason' in status ? status.reason : null)) return;
  statuses.set(projectId, status);
  listeners.forEach(l => l());
}

export function getDeviceAgentStatus(projectId: string): DeviceAgentStatus {
  return statuses.get(projectId) ?? CHECKING;
}

export class DeviceAgentError extends Error {
  readonly kind: 'unreachable' | 'refused' | 'rejected' | 'unsupported';
  readonly status?: number;
  constructor(kind: DeviceAgentError['kind'], message: string, status?: number) {
    super(message);
    this.name = 'DeviceAgentError';
    this.kind = kind;
    this.status = status;
  }
}

// ---------- билет ----------

interface AgentTicket { ticket: string; expiresAt: number; port: number; header: string }
const tickets = new Map<string, AgentTicket>();
const pendingTickets = new Map<string, Promise<AgentTicket>>();

function ticketFresh(t: AgentTicket | undefined): t is AgentTicket {
  return !!t && t.expiresAt - Date.now() > TICKET_REFRESH_MARGIN_MS;
}

async function issueTicket(projectId: string): Promise<AgentTicket> {
  try {
    const r = await request<{ ticket: string; expiresAt: string; port?: number; header?: string }>(
      `/projects/${encodeURIComponent(projectId)}/device-agent/ticket`, { method: 'POST', live: true });
    const t: AgentTicket = {
      ticket: r.ticket,
      expiresAt: Date.parse(r.expiresAt),
      port: r.port ?? DEVICE_AGENT_DEFAULT_PORT,
      header: r.header ?? DEFAULT_TICKET_HEADER,
    };
    tickets.set(projectId, t);
    return t;
  } catch (e) {
    const status = (e as { status?: number } | null)?.status;
    const reason = e instanceof Error && e.message ? e.message : 'Сервер не выдал доступ к агенту устройства';
    tickets.delete(projectId);
    setStatus(projectId, { kind: 'refused', reason });
    throw new DeviceAgentError('refused', reason, status);
  }
}

export function getAgentTicket(projectId: string, force = false): Promise<AgentTicket> {
  const cached = tickets.get(projectId);
  if (!force && ticketFresh(cached)) return Promise.resolve(cached);
  const pending = pendingTickets.get(projectId);
  if (pending) return pending;
  const p = issueTicket(projectId).finally(() => pendingTickets.delete(projectId));
  pendingTickets.set(projectId, p);
  return p;
}

function agentBase(t: AgentTicket): string {
  return `http://127.0.0.1:${t.port}`;
}

// ---------- запросы к агенту ----------

async function agentFetch(projectId: string, pathAndQuery: string, init: RequestInit, retried = false): Promise<Response> {
  const t = await getAgentTicket(projectId);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), AGENT_TIMEOUT_MS);
  let res: Response;
  try {
    // JWT сервера агенту не шлём никогда: у него свой билет в отдельном заголовке
    res = await fetch(`${agentBase(t)}/api/projects/${encodeURIComponent(projectId)}/${pathAndQuery}`, {
      method: init.method,
      body: init.body,
      signal: controller.signal,
      headers: {
        // FormData тип с границей ставит сам браузер
        ...(init.body && !(init.body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}),
        [t.header]: t.ticket,
      },
    });
  } catch {
    setStatus(projectId, { kind: 'unreachable' });
    throw new DeviceAgentError('unreachable', 'Агент устройства не отвечает на этом компьютере');
  } finally {
    clearTimeout(timer);
  }
  if (res.status === 401) {
    // Билет протух раньше срока (перезапуск агента, часы) — один перевыпуск и повтор
    if (!retried) {
      tickets.delete(projectId);
      return agentFetch(projectId, pathAndQuery, init, true);
    }
    const body = await res.json().catch(() => null) as { error?: string } | null;
    const reason = body?.error ?? 'Агент на этом компьютере не принял доступ к проекту';
    setStatus(projectId, { kind: 'rejected', reason });
    throw new DeviceAgentError('rejected', reason, 401);
  }
  return res;
}

// Запрос к агенту с тем же контрактом ошибок, что request() в offline.ts: Error с status и body.
export async function agentRequest<T>(projectId: string, pathAndQuery: string, init: RequestInit = {}): Promise<T> {
  const res = await agentFetch(projectId, pathAndQuery, init);
  setStatus(projectId, { kind: 'ready' });
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: res.statusText })) as { error?: string };
    const httpErr = new Error(err.error ?? res.statusText) as Error & { status?: number; body?: unknown };
    httpErr.status = res.status;
    httpErr.body = err;
    throw httpErr;
  }
  const text = res.status === 204 ? '' : await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

// Путь к рабочим подсистемам проекта: /projects/{id}/files…, git…, services, preview…,
// launch-config, skills, agents…
const PROJECT_FILES_URL = /^\/projects\/([^/?]+)\/((?:files|git|services|preview|launch-config|skills|agents)(?:\/[^?]*)?)(\?.*)?$/;

// Замена request() для маршрутов файлов, git, сервисов и навыков: у локального проекта запрос уходит в агента,
// у серверного — как раньше. Маршрут, которого агент не умеет, отказывает понятным текстом
// до сети, а не падает там 404.
export function projectRequest<T>(url: string, options?: RequestInit & { timeoutMs?: number; live?: boolean }): Promise<T> {
  const m = PROJECT_FILES_URL.exec(url);
  if (!m) return request<T>(url, options);
  const projectId = decodeURIComponent(m[1]);
  if (projectRouteOf(projectId) !== 'agent') return request<T>(url, options);

  const method = (options?.method ?? 'GET').toUpperCase();
  const path = m[2];
  if (!agentServesPath(method, path))
    return Promise.reject(new DeviceAgentError('unsupported', DEVICE_AGENT_UNSUPPORTED_TEXT));
  let query = m[3] ?? '';
  // Настроек проекта у агента нет: показ скрытых файлов едет параметром запроса
  if (method === 'GET' && (path === 'files' || path === 'files/tree') && !/[?&]showHidden=/.test(query)
      && routing.get(projectId)?.showHidden)
    query += `${query ? '&' : '?'}showHidden=true`;
  return agentRequest<T>(projectId, path + query, { method, body: options?.body });
}

// Проверка, что маршрут проекта с этим методом доступен сейчас (для прямых fetch вне request).
export function assertServerRoute(projectId: string, method: string, path: string): void {
  if (projectRouteOf(projectId) === 'agent' && !agentServesPath(method, path))
    throw new DeviceAgentError('unsupported', DEVICE_AGENT_UNSUPPORTED_TEXT);
}

// ---------- поток: узкий билет на один путь ----------

// Маршрут выдачи и параметр — DeviceAgentApi.StreamTicketRoute/StreamTicketQuery. Маршрут
// вне контракта файлов сервера, поэтому в deviceAgentRoutes его нет
const STREAM_TICKET_ROUTE = 'agent/stream-ticket';
const STREAM_TICKET_QUERY = 'streamTicket';
// Узкий билет живёт ≤60 с; отдаём элементу, только если ему осталось больше этого запаса
const STREAM_REUSE_MARGIN_MS = 15_000;

interface StreamGrant { url: string; expiresAt: number }
const streamGrants = new Map<string, StreamGrant>();
const pendingStreams = new Map<string, Promise<string>>();
const streamKey = (projectId: string, path: string) => `${projectId}\n${path}`;

function streamFresh(g: StreamGrant | undefined): g is StreamGrant {
  return !!g && g.expiresAt - Date.now() > STREAM_REUSE_MARGIN_MS;
}

// URL отдачи файла потоком для <img>/<video>/<audio> — ЕДИНСТВЕННАЯ точка на фронте. Тег не
// шлёт заголовков, поэтому билет едет в запросе, но основной билет проекта туда не кладётся
// никогда (история, логи, Referer): агент по основному билету в заголовке выдаёт узкий —
// ≤60 с, на один путь, принимается только в GET files/stream. force — перевыпустить, даже
// если прежний ещё числится живым (элемент получил на нём отказ).
export function agentStreamUrl(projectId: string, path: string, force = false): Promise<string> {
  const key = streamKey(projectId, path);
  const cached = streamGrants.get(key);
  if (!force && streamFresh(cached)) return Promise.resolve(cached.url);
  const pending = pendingStreams.get(key);
  if (pending) return pending;
  const p = (async () => {
    const r = await agentRequest<{ streamTicket: string; expiresAt: string }>(projectId, STREAM_TICKET_ROUTE, {
      method: 'POST', body: JSON.stringify({ path }),
    });
    const t = await getAgentTicket(projectId);
    const params = new URLSearchParams({ path, [STREAM_TICKET_QUERY]: r.streamTicket });
    const url = `${agentBase(t)}/api/projects/${encodeURIComponent(projectId)}/files/stream?${params}`;
    streamGrants.set(key, { url, expiresAt: Date.parse(r.expiresAt) });
    return url;
  })().finally(() => pendingStreams.delete(key));
  pendingStreams.set(key, p);
  return p;
}

// ---------- хаб агента: узкий билет на подключение ----------

// Маршрут выдачи — DeviceAgentApi.HubTicketRoute. Билет только на рукопожатие (≤60 с, один
// проект): клиент SignalR кладёт его в access_token, основной билет туда не годится
const HUB_TICKET_ROUTE = 'agent/hub-ticket';
const HUB_PATH = '/hubs/agent';

interface HubGrant { ticket: string; expiresAt: number }
const hubGrants = new Map<string, HubGrant>();
const pendingHubTickets = new Map<string, Promise<string>>();

// Билет хаба — ЕДИНСТВЕННАЯ точка на фронте. Negotiate и сам WebSocket зовут фабрику токена
// подряд, поэтому свежий билет отдаётся повторно, пока ему осталось больше запаса.
export function agentHubTicket(projectId: string): Promise<string> {
  const cached = hubGrants.get(projectId);
  if (cached && cached.expiresAt - Date.now() > STREAM_REUSE_MARGIN_MS) return Promise.resolve(cached.ticket);
  const pending = pendingHubTickets.get(projectId);
  if (pending) return pending;
  const p = agentRequest<{ hubTicket: string; expiresAt: string }>(projectId, HUB_TICKET_ROUTE, { method: 'POST' })
    .then(r => {
      hubGrants.set(projectId, { ticket: r.hubTicket, expiresAt: Date.parse(r.expiresAt) });
      return r.hubTicket;
    })
    .finally(() => pendingHubTickets.delete(projectId));
  pendingHubTickets.set(projectId, p);
  return p;
}

// Адрес хаба агента: порт — из основного билета, как у API
export async function agentHubUrl(projectId: string): Promise<string> {
  return `${agentBase(await getAgentTicket(projectId))}${HUB_PATH}`;
}

// ---------- превью: билет на отдельный порт ----------

// Маршрут выдачи — DeviceAgentApi.PreviewTicketRoute. Агент отвечает готовым адресом iframe на
// своём порту превью (47319) с билетом в параметре; первая загрузка кладёт его в
// секционированную куку и срезает из адреса, подресурсы дев-сайта идут уже по куке
const PREVIEW_TICKET_ROUTE = 'agent/preview-ticket';
// Билет превью живёт часы; перевыпускаем заранее, чтобы iframe не открылся на истёкшем
const PREVIEW_REUSE_MARGIN_MS = 10 * 60_000;

interface PreviewGrant { url: string; expiresAt: number }
const previewGrants = new Map<string, PreviewGrant>();
const pendingPreviews = new Map<string, Promise<string>>();

// Адрес iframe превью локального проекта — ЕДИНСТВЕННАЯ точка на фронте. force — перевыпустить
// (агент перезапущен и прежний билет не узнаёт).
export function agentPreviewUrl(projectId: string, force = false): Promise<string> {
  const cached = previewGrants.get(projectId);
  if (!force && cached && cached.expiresAt - Date.now() > PREVIEW_REUSE_MARGIN_MS) return Promise.resolve(cached.url);
  const pending = pendingPreviews.get(projectId);
  if (pending) return pending;
  const p = agentRequest<{ previewTicket: string; expiresAt: string; url: string }>(projectId, PREVIEW_TICKET_ROUTE, { method: 'POST' })
    .then(r => {
      previewGrants.set(projectId, { url: r.url, expiresAt: Date.parse(r.expiresAt) });
      return r.url;
    })
    .finally(() => pendingPreviews.delete(projectId));
  pendingPreviews.set(projectId, p);
  return p;
}

// ---------- вложения чата ----------

// Маршрут — DeviceAgentApi.AttachmentsRoute. Вложение локального проекта ложится на машину
// проекта (сервер для него отвечает отказом G1); ответ тот же, что у сервера: путь от корня
const ATTACHMENTS_ROUTE = 'agent/attachments';

export function uploadAgentAttachment(projectId: string, file: File): Promise<{ path: string }> {
  const form = new FormData();
  form.append('file', file);
  return agentRequest<{ path: string }>(projectId, ATTACHMENTS_ROUTE, { method: 'POST', body: form });
}

// ---------- проверка связи ----------

const probes = new Map<string, Promise<void>>();

// Выдать билет и спросить у агента корень проекта: так за один заход видно все три исхода —
// сервер отказал, агента нет, агент не наш.
export function probeDeviceAgent(projectId: string): Promise<void> {
  const running = probes.get(projectId);
  if (running) return running;
  setStatus(projectId, CHECKING);
  const p = agentRequest<unknown>(projectId, 'files?path=')
    .then(() => undefined)
    .catch(e => {
      // Ошибка файловой операции при живом агенте (корня нет на диске и т.п.) — агент наш,
      // но показать нечего: текст агента и есть причина
      if (!(e instanceof DeviceAgentError))
        setStatus(projectId, { kind: 'rejected', reason: e instanceof Error ? e.message : 'Агент не открыл проект' });
    })
    .finally(() => probes.delete(projectId));
  probes.set(projectId, p);
  return p;
}

function subscribe(l: () => void): () => void {
  listeners.add(l);
  return () => { listeners.delete(l); };
}

// Состояние связи с агентом для панели локального проекта. У серверного проекта — всегда
// ready: ему агент не нужен. Пока панель открыта, билет перевыпускается заранее, чтобы
// запросы панели не упирались в выдачу билета.
export function useDeviceAgent(project: Project | null | undefined, enabled = true): DeviceAgentStatus {
  noteProject(project);
  const projectId = project?.id ?? '';
  const viaAgent = enabled && projectFilesRoute(project) === 'agent';
  const status = useSyncExternalStore(subscribe, () => getDeviceAgentStatus(projectId));

  useEffect(() => {
    if (!viaAgent || !projectId) return;
    if (!statuses.has(projectId) || statuses.get(projectId)?.kind !== 'ready') void probeDeviceAgent(projectId);
    const timer = setInterval(() => {
      if (getDeviceAgentStatus(projectId).kind === 'ready') void getAgentTicket(projectId).catch(() => {});
    }, TICKET_KEEPALIVE_MS);
    return () => clearInterval(timer);
  }, [viaAgent, projectId]);

  return viaAgent ? status : READY;
}

const READY: DeviceAgentStatus = { kind: 'ready' };

// Для тестов: сбросить всё запомненное
export function resetDeviceAgentForTests(): void {
  routing.clear();
  statuses.clear();
  tickets.clear();
  pendingTickets.clear();
  probes.clear();
  streamGrants.clear();
  pendingStreams.clear();
  hubGrants.clear();
  pendingHubTickets.clear();
  previewGrants.clear();
  pendingPreviews.clear();
}
