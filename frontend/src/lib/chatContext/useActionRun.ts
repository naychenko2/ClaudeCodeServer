// Хук единой точки запуска (ADR-023 §Д2): `useActionRun(sessionId, ctx)` — подпись, цена, состояние и
// `run` для кнопки поля ввода И низа панели «Контекст». Обе зовут этот хук с одним состоянием и
// получают одну подпись; сборка — buildActionRun (actionRun.ts), здесь только подписки и цена по запросу.

import { useEffect, useSyncExternalStore } from 'react';
import { useActionMemoryVersion } from './actionMemory';
import { buildActionRun, ensureQuote, getRunVersion, subscribeRun } from './actionRun';
import { getKindApi } from './registry';
import { useChatContext } from './store';
import type { ActionRun, ContextKindCtx } from './types';

// Цена по тексту: пока человек печатает, запросы не шлём на каждую букву
const QUOTE_DEBOUNCE_MS = 300;

// sessionId = null — хост выключен (флаг строки контекста): стор контекста не читается и не грузится
export function useActionRun(sessionId: string | null, ctx: ContextKindCtx): ActionRun {
  const context = useChatContext(sessionId);
  useActionMemoryVersion();
  useSyncExternalStore(subscribeRun, getRunVersion, getRunVersion);
  const { primary, refs, revision } = context;
  const api = primary ? getKindApi(primary.kind) : null;
  const built = buildActionRun({ sessionId: sessionId ?? '', api: sessionId ? api : null, ctx, primary, refs, revision });
  const { req, scope } = built;
  const reqKey = req ? JSON.stringify(req) : null;
  useEffect(() => {
    if (!sessionId || !api || !req) return;
    const t = setTimeout(() => { void ensureQuote(sessionId, scope, api, ctx, req); }, QUOTE_DEBOUNCE_MS);
    return () => clearTimeout(t);
    // req и ctx собираются заново каждый рендер: следим за их содержимым, а не ссылкой
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, scope, reqKey, api, ctx.projectId, ctx.isMobile]);
  return built;
}
