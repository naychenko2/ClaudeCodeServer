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

async function ensureConnected(): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    if (!startPromise) {
      startPromise = conn.start().finally(() => { startPromise = null })
    }
    await startPromise
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
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Disconnected) await ensureConnected()
  return conn.invoke('CreateTerminal', projectId, cols, rows, name ?? null)
}

export async function connectTerminal(terminalId: string): Promise<TerminalInfo | null> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Disconnected) await ensureConnected()
  return conn.invoke('ConnectTerminal', terminalId)
}

export async function listTerminals(projectId: string): Promise<TerminalInfo[]> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Disconnected) await ensureConnected()
  return conn.invoke('ListTerminals', projectId)
}

export async function stopTerminal(terminalId: string): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Connected) {
    await conn.invoke('StopTerminal', terminalId)
  }
}

export async function sendTerminalInput(terminalId: string, data: string): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Connected) {
    await conn.invoke('TerminalInput', terminalId, data)
  }
}

export async function resizeTerminal(terminalId: string, cols: number, rows: number): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Connected) {
    await conn.invoke('TerminalResize', terminalId, cols, rows)
  }
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
