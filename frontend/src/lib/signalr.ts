import * as signalR from '@microsoft/signalr';
import type { ServerMessage } from '../types';
import { notifyOnline, notifyOffline } from './offline';

let connection: signalR.HubConnection | null = null;

export function getConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/session')
      .withAutomaticReconnect({
        // Переподключаемся бесконечно с экспоненциальным откатом, макс 30 сек
        nextRetryDelayInMilliseconds: ctx =>
          Math.min(1000 * Math.pow(2, ctx.previousRetryCount), 30_000),
      })
      .build();
    // Состояние соединения двигает глобальный online/offline флаг
    connection.onreconnecting(() => notifyOffline());
    connection.onreconnected(() => notifyOnline());
    connection.onclose(() => notifyOffline());
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
    // ждём пока не подключится; таймаут — чтобы офлайн (вечный Reconnecting) не висел бесконечно
    await new Promise<void>((resolve, reject) => {
      let waited = 0;
      const timer = setInterval(() => {
        if (conn.state === signalR.HubConnectionState.Connected) {
          clearInterval(timer);
          resolve();
        } else if (conn.state === signalR.HubConnectionState.Disconnected) {
          clearInterval(timer);
          reject(new Error('SignalR disconnected while waiting'));
        } else if ((waited += 50) >= 8000) {
          clearInterval(timer);
          reject(new Error('SignalR connect timeout'));
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

// Watcher: сервер сообщает об изменении файлов проекта (создание/правка/удаление)
export function onFilesChanged(handler: (data: { projectId: string; paths: string[] }) => void): () => void {
  const conn = getConnection();
  conn.on('filesChanged', handler);
  return () => conn.off('filesChanged', handler);
}

export async function joinProject(projectId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('JoinProject', projectId);
}

export async function leaveProject(projectId: string): Promise<void> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Connected)
    await conn.invoke('LeaveProject', projectId);
}

export function onReconnected(callback: () => void): void {
  getConnection().onreconnected(callback);
}
