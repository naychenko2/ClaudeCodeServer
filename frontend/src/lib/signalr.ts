import * as signalR from '@microsoft/signalr';
import type { ServerMessage } from '../types';

let connection: signalR.HubConnection | null = null;

export function getConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/session')
      .withAutomaticReconnect()
      .build();
  }
  return connection;
}

let _startPromise: Promise<void> | null = null;

export async function ensureConnected(): Promise<signalR.HubConnection> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    if (!_startPromise) {
      _startPromise = conn.start().finally(() => { _startPromise = null; });
    }
    await _startPromise;
  } else if (conn.state === signalR.HubConnectionState.Connecting ||
             conn.state === signalR.HubConnectionState.Reconnecting) {
    // ждём пока не подключится
    await new Promise<void>((resolve, reject) => {
      const timer = setInterval(() => {
        if (conn.state === signalR.HubConnectionState.Connected) {
          clearInterval(timer);
          resolve();
        } else if (conn.state === signalR.HubConnectionState.Disconnected) {
          clearInterval(timer);
          reject(new Error('SignalR disconnected while waiting'));
        }
      }, 50);
    });
  }
  return conn;
}

export async function joinSession(sessionId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('JoinSession', sessionId);
}

export async function leaveSession(sessionId: string): Promise<void> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Connected) {
    await conn.invoke('LeaveSession', sessionId);
  }
}

export async function sendMessage(sessionId: string, text: string, attachedPaths: string[] = []): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('SendMessage', sessionId, text, attachedPaths);
}

export async function respondPermission(
  sessionId: string,
  requestId: string,
  behavior: 'allow' | 'deny',
): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('RespondPermission', sessionId, requestId, behavior);
}

export async function interruptSession(sessionId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('Interrupt', sessionId);
}

export function onMessage(handler: (msg: ServerMessage) => void): () => void {
  const conn = getConnection();
  conn.on('message', handler);
  return () => conn.off('message', handler);
}
