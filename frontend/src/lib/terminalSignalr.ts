import * as signalR from '@microsoft/signalr'

let connection: signalR.HubConnection | null = null

function getToken(): string {
  return localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token') || ''
}

function getConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/terminal', {
        accessTokenFactory: getToken,
      })
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

export async function startTerminal(projectId: string, cols: number, rows: number): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    await ensureConnected()
  }
  await conn.invoke('StartTerminal', projectId, cols, rows)
}

export async function stopTerminal(projectId: string): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Connected) {
    try {
      await conn.invoke('StopTerminal', projectId)
    } catch { /* ignore */ }
  }
}

export async function sendTerminalInput(projectId: string, data: string): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Connected) {
    await conn.invoke('TerminalInput', projectId, data)
  }
}

export async function resizeTerminal(projectId: string, cols: number, rows: number): Promise<void> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Connected) {
    await conn.invoke('TerminalResize', projectId, cols, rows)
  }
}

type TerminalMessageHandler = (msg: { type: string; data?: string; isError?: boolean; status?: string; exitCode?: number }) => void

export function onTerminalMessage(handler: TerminalMessageHandler): () => void {
  const conn = getConnection()
  conn.on('message', handler)
  return () => conn.off('message', handler)
}
