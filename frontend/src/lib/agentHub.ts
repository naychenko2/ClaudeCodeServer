// Хаб агента устройства /hubs/agent (ADR-016, задача 4.4б): терминалы и логи дев-серверов
// локального проекта. Методы те же, что у серверных TerminalHub и SessionHub.JoinPreviewLog,
// события — те же ServerMessage в канале 'message'. Соединение своё на каждый проект: билет хаба
// выдаётся на один проект.

import * as signalR from '@microsoft/signalr';
import type { ServerMessage } from '../types';
import { agentHubTicket, agentHubUrl } from './deviceAgent';

type AgentHubHandler = (msg: ServerMessage) => void;

const handlers = new Set<AgentHubHandler>();
const pending = new Map<string, Promise<signalR.HubConnection>>();
const live = new Map<string, signalR.HubConnection>();

function build(projectId: string, url: string): signalR.HubConnection {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(url, {
      // Каждое (пере)подключение берёт свежий узкий билет: основной в URL не кладём никогда
      accessTokenFactory: () => agentHubTicket(projectId),
      // Агент не разрешает credentials в CORS, а negotiate по умолчанию идёт с ними
      withCredentials: false,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .build();
  conn.on('message', (msg: ServerMessage) => handlers.forEach(h => h(msg)));
  conn.onclose(() => {
    if (live.get(projectId) === conn) live.delete(projectId);
  });
  return conn;
}

function waitConnected(conn: signalR.HubConnection): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setInterval(() => {
      if (conn.state === signalR.HubConnectionState.Connected) { clearInterval(timer); clearTimeout(limit); resolve(); }
      else if (conn.state === signalR.HubConnectionState.Disconnected) { clearInterval(timer); clearTimeout(limit); reject(new Error('Хаб агента отключён')); }
    }, 100);
    const limit = setTimeout(() => { clearInterval(timer); reject(new Error('Хаб агента не ответил')); }, 8000);
  });
}

// Подключённый хаб агента для проекта. Соединение, от которого отказался автоматический
// переподключатель, пересоздаётся заново.
export async function agentHub(projectId: string): Promise<signalR.HubConnection> {
  const current = live.get(projectId);
  if (current?.state === signalR.HubConnectionState.Connected) return current;
  if (current && current.state !== signalR.HubConnectionState.Disconnected) {
    await waitConnected(current);
    return current;
  }
  let p = pending.get(projectId);
  if (!p) {
    p = (async () => {
      const conn = build(projectId, await agentHubUrl(projectId));
      await conn.start();
      live.set(projectId, conn);
      return conn;
    })().finally(() => pending.delete(projectId));
    pending.set(projectId, p);
  }
  return p;
}

// Подключённый хаб без ожидания — для ввода в терминал: пропавшую связь не ждём, ввод теряется
export function agentHubIfConnected(projectId: string): signalR.HubConnection | null {
  const conn = live.get(projectId);
  return conn?.state === signalR.HubConnectionState.Connected ? conn : null;
}

// События всех хабов агента. Подписка соединения не открывает — его открывает первый вызов
// метода (список терминалов, подписка на лог и т.п.).
export function onAgentHubMessage(handler: AgentHubHandler): () => void {
  handlers.add(handler);
  return () => { handlers.delete(handler); };
}

// Для тестов
export function resetAgentHubForTests(): void {
  handlers.clear();
  pending.clear();
  live.clear();
}
