// Чистка памяти контекста чата: удаление чата и выход из аккаунта. Вынесено отдельно от
// стора, чтобы api.ts звал одну функцию, а новая память не требовала правки вызывающих.

import { forgetActionMemory, resetActionMemory } from './actionMemory';
import { clearContextReturn, resetContextReturns } from './contextReturn';
import { forgetChatContext, resetChatContext } from './store';

export function forgetChatContextSession(sessionId: string) {
  forgetActionMemory(sessionId);
  clearContextReturn(sessionId);
  forgetChatContext(sessionId);
}

export function resetChatContextMemory() {
  resetActionMemory();
  resetContextReturns();
  resetChatContext();
}
