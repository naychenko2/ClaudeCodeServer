import * as signalR from '@microsoft/signalr';
import { readStoredToken } from './offline';
import { projectRouteOf } from './deviceAgent';
import { agentHub, agentHubIfConnected, onAgentHubMessage } from './agentHub';

// Терминал локального проекта живёт в агенте устройства (хаб /hubs/agent), серверного — на
// сервере. Решение по проекту берётся из deviceAgent (projectRouteOf); вызовы по id терминала
// находят проект по памяти ниже — её пополняет всё, что вернуло TerminalInfo.
const terminalProjects = new Map<string, string>()

function remember<T extends TerminalInfo | null>(t: T): T {
  if (t) terminalProjects.set(t.id, t.projectId)
  return t
}

const viaAgent = (projectId: string | undefined): projectId is string =>
  !!projectId && projectRouteOf(projectId) === 'agent'

let connection: signalR.HubConnection | null = null

function getToken(): string {
  // readStoredToken() отсеивает мусор в storage («null»/«undefined»), иначе
  // SignalR отправил бы ?access_token=null на /hubs/terminal
  return readStoredToken() ?? ''
}

function getConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/terminal', { accessTokenFactory: getToken })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build()
  }
  return connection
}

let startPromise: Promise<void> | null = null

/** Дождаться Connected-состояния хаба */
async function ensureConnected(): Promise<void> {
  const conn = getConnection()
  const state = conn.state
  if (state === signalR.HubConnectionState.Connected) return
  if (state === signalR.HubConnectionState.Disconnected) {
    if (!startPromise) {
      startPromise = conn.start().finally(() => { startPromise = null })
    }
    await startPromise
  } else {
    // Connecting или Reconnecting — ждём
    await new Promise<void>((resolve, reject) => {
      const timer = setInterval(() => {
        const s = conn.state
        if (s === signalR.HubConnectionState.Connected) { clearInterval(timer); resolve() }
        else if (s === signalR.HubConnectionState.Disconnected) { clearInterval(timer); reject(new Error('SignalR disconnected')) }
      }, 100)
      setTimeout(() => { clearInterval(timer); reject(new Error('SignalR connect timeout')) }, 8000)
    })
  }
}

export interface TerminalInfo {
  id: string
  projectId: string
  name: string
  status: string
  shell: string | null
}

// Хаб, в котором живёт терминал проекта: подключённый, с ожиданием связи
async function hubFor(projectId: string | undefined): Promise<signalR.HubConnection> {
  if (viaAgent(projectId)) return agentHub(projectId)
  await ensureConnected()
  return getConnection()
}

// То же без ожидания: для ввода и ресайза пропавшую связь не ждём
function connectedHubFor(terminalId: string): signalR.HubConnection | null {
  const projectId = terminalProjects.get(terminalId)
  if (viaAgent(projectId)) return agentHubIfConnected(projectId)
  const conn = getConnection()
  return conn.state === signalR.HubConnectionState.Connected ? conn : null
}

export async function createTerminal(projectId: string, name?: string, cols = 80, rows = 24): Promise<TerminalInfo> {
  const conn = await hubFor(projectId)
  return remember(await conn.invoke<TerminalInfo>('CreateTerminal', projectId, cols, rows, name ?? null))
}

export async function connectTerminal(terminalId: string): Promise<TerminalInfo | null> {
  const conn = await hubFor(terminalProjects.get(terminalId))
  return remember(await conn.invoke<TerminalInfo | null>('ConnectTerminal', terminalId))
}

export async function listTerminals(projectId: string): Promise<TerminalInfo[]> {
  const conn = await hubFor(projectId)
  const list = await conn.invoke<TerminalInfo[]>('ListTerminals', projectId)
  list.forEach(remember)
  return list
}

export async function stopTerminal(terminalId: string): Promise<void> {
  const conn = await hubFor(terminalProjects.get(terminalId))
  await conn.invoke('StopTerminal', terminalId)
}

export async function renameTerminal(terminalId: string, name: string): Promise<TerminalInfo | null> {
  const conn = await hubFor(terminalProjects.get(terminalId))
  return remember(await conn.invoke<TerminalInfo | null>('RenameTerminal', terminalId, name))
}

export async function sendTerminalInput(terminalId: string, data: string): Promise<void> {
  const conn = connectedHubFor(terminalId)
  if (!conn) return
  // fire-and-forget: терминал мог закрыться (гонка) — сервер отклонит вызов, ввод игнорируем
  try { await conn.invoke('TerminalInput', terminalId, data) } catch { /* закрыт/чужой */ }
}

export async function resizeTerminal(terminalId: string, cols: number, rows: number): Promise<void> {
  const conn = connectedHubFor(terminalId)
  if (!conn) return
  try { await conn.invoke('TerminalResize', terminalId, cols, rows) } catch { /* закрыт/чужой */ }
}

type TerminalMsgHandler = (msg: {
  type: string
  data?: string
  isError?: boolean
  status?: string
  exitCode?: number
  terminalId?: string
  name?: string
}) => void

// События терминалов и сервера, и агентов: фильтр по terminalId — у подписчика
export function onTerminalMessage(handler: TerminalMsgHandler): () => void {
  const conn = getConnection()
  conn.on('message', handler)
  const offAgent = onAgentHubMessage(msg => handler(msg as unknown as Parameters<TerminalMsgHandler>[0]))
  return () => { conn.off('message', handler); offAgent() }
}
