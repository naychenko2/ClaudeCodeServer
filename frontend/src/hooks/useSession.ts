import { useEffect, useRef, useState, useCallback } from 'react';
import type { ChatItem, ServerMessage } from '../types';
import {
  joinSession,
  leaveSession,
  onMessage,
  sendMessage,
  respondPermission,
  interruptSession,
} from '../lib/signalr';

export function useSession(sessionId: string | null) {
  // Кэш items по sessionId — сохраняется при переключении между сессиями
  const itemsCacheRef = useRef<Map<string, ChatItem[]>>(new Map());
  const sessionIdRef = useRef<string | null>(sessionId);
  sessionIdRef.current = sessionId;

  const [items, setItems] = useState<ChatItem[]>([]);
  const [isWaiting, setIsWaiting] = useState(false);
  const [isJoined, setIsJoined] = useState(false);
  const unsubRef = useRef<(() => void) | null>(null);

  // Обновляет state и синхронно пишет в кэш для текущего sessionId
  const setItemsAndCache = useCallback((updater: ChatItem[] | ((prev: ChatItem[]) => ChatItem[])) => {
    setItems(prev => {
      const next = typeof updater === 'function' ? updater(prev) : updater;
      const sid = sessionIdRef.current;
      if (sid) itemsCacheRef.current.set(sid, next);
      return next;
    });
  }, []);

  useEffect(() => {
    if (!sessionId) return;

    // Восстанавливаем историю из кэша (или начинаем с пустого)
    setItems(itemsCacheRef.current.get(sessionId) ?? []);
    setIsJoined(false);
    let cancelled = false;
    joinSession(sessionId).then(() => { if (!cancelled) setIsJoined(true); });

    unsubRef.current = onMessage((msg: ServerMessage) => {
      switch (msg.type) {
        case 'session_started':
          if (!msg.isResume)
            setItemsAndCache(prev => [...prev, { kind: 'session_started', model: msg.model, mode: msg.mode }]);
          break;
        case 'text_delta':
          setItemsAndCache(prev => {
            const last = prev[prev.length - 1];
            if (last?.kind === 'text') {
              return [...prev.slice(0, -1), { kind: 'text', text: last.text + msg.text }];
            }
            return [...prev, { kind: 'text', text: msg.text }];
          });
          break;
        case 'thinking_delta':
          setItemsAndCache(prev => {
            const last = prev[prev.length - 1];
            if (last?.kind === 'thinking') {
              return [...prev.slice(0, -1), { ...last, text: last.text + msg.text }];
            }
            return [...prev, { kind: 'thinking', text: msg.text, expanded: false }];
          });
          break;
        case 'tool_use':
          setItemsAndCache(prev => [...prev, { kind: 'tool_use', id: msg.id, name: msg.name, input: msg.input }]);
          break;
        case 'tool_result':
          setItemsAndCache(prev => prev.map(item =>
            item.kind === 'tool_use' && item.id === msg.toolUseId
              ? { ...item, result: msg.content, isError: msg.isError }
              : item
          ));
          break;
        case 'permission_request':
          setIsWaiting(true);
          setItemsAndCache(prev => [...prev, {
            kind: 'permission_request',
            requestId: msg.requestId,
            toolName: msg.toolName,
            toolInput: msg.toolInput,
            resolved: false,
          }]);
          break;
        case 'file_changed':
          setItemsAndCache(prev => [...prev, { kind: 'file_changed', path: msg.path, added: msg.added, removed: msg.removed }]);
          break;
        case 'result':
          setIsWaiting(false);
          setItemsAndCache(prev => [...prev, { kind: 'result', subtype: msg.subtype, durationMs: msg.durationMs, numTurns: msg.numTurns }]);
          break;
        case 'error':
          setIsWaiting(false);
          setItemsAndCache(prev => [...prev, { kind: 'error', text: msg.text, canRetry: true }]);
          break;
        case 'exited':
          setIsWaiting(false);
          break;
      }
    });

    return () => {
      cancelled = true;
      unsubRef.current?.();
      leaveSession(sessionId);
    };
  }, [sessionId]);

  const send = useCallback(async (text: string, attachedPaths: string[] = []) => {
    if (!sessionId) return;
    setItemsAndCache(prev => [...prev, { kind: 'user_message', text, attachedPaths }]);
    setIsWaiting(true);
    await sendMessage(sessionId, text, attachedPaths);
  }, [sessionId]);

  const allowPermission = useCallback(async (requestId: string) => {
    if (!sessionId) return;
    setIsWaiting(false);
    setItemsAndCache(prev => prev.map(item =>
      item.kind === 'permission_request' && item.requestId === requestId
        ? { ...item, resolved: true } : item
    ));
    await respondPermission(sessionId, requestId, 'allow');
  }, [sessionId]);

  const denyPermission = useCallback(async (requestId: string) => {
    if (!sessionId) return;
    setIsWaiting(false);
    setItemsAndCache(prev => prev.map(item =>
      item.kind === 'permission_request' && item.requestId === requestId
        ? { ...item, resolved: true } : item
    ));
    await respondPermission(sessionId, requestId, 'deny');
  }, [sessionId]);

  const interrupt = useCallback(() => {
    if (!sessionId) return;
    interruptSession(sessionId);
  }, [sessionId]);

  const toggleThinking = useCallback((index: number) => {
    setItemsAndCache(prev => prev.map((item, i) =>
      i === index && item.kind === 'thinking' ? { ...item, expanded: !item.expanded } : item
    ));
  }, []);

  return { items, isWaiting, isJoined, send, allowPermission, denyPermission, interrupt, toggleThinking };
}
