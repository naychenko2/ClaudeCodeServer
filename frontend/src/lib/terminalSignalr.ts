import * as signalR from '@microsoft/signalr'

let connection: signalR.HubConnection | null = null

function getToken(): string {
  return localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token') || ''
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

export async function createTerminal(projectId: string, name?: string, cols = 80, rows = 24): Promise<TerminalInfo> {
  await ensureConnected()
  return getConnection().invoke('CreateTerminal', projectId, cols, rows, name ?? null)
}

export async function connectTerminal(terminalId: string): Promise<TerminalInfo | null> {
  await ensureConnected()
  return getConnection().invoke('ConnectTerminal', terminalId)
}

export async function listTerminals(projectId: string): Promise<TerminalInfo[]> {
  await ensureConnected()
  return getConnection().invoke('ListTerminals', projectId)
}

export async function stopTerminal(terminalId: string): Promise<void> {
  const conn = getConnection()
  if (conn.state !== signalR.HubConnectionState.Connected) await ensureConnected()
  await conn.invoke('StopTerminal', terminalId)
}

export async function sendTerminalInput(terminalId: string, data: string): Promise<void> {
  const conn = getConnection()
  if (conn.state !== signalR.HubConnectionState.Connected) return
  await conn.invoke('TerminalInput', terminalId, data)
}

export async function resizeTerminal(terminalId: string, cols: number, rows: number): Promise<void> {
  const conn = getConnection()
  if (conn.state !== signalR.HubConnectionState.Connected) return
  await conn.invoke('TerminalResize', terminalId, cols, rows)
}

type TerminalMsgHandler = (msg: {
  type: string
  data?: string
  isError?: boolean
  status?: string
  exitCode?: number
  terminalId?: string
}) => void

export function onTerminalMessage(handler: TerminalMsgHandler): () => void {
  const conn = getConnection()
  conn.on('message', handler)
  return () => conn.off('message', handler)
}
