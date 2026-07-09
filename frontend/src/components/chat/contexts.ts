import { createContext, useContext } from 'react';

// Контекст текущего проекта — для резолва локальных путей картинок в сообщениях
export const ChatProjectContext = createContext<{ id: string; rootPath: string } | null>(null);

// Имя ассистента сессии (Claude | DeepSeek | GLM | …) — для строк в UI, чтобы не тащить проп
// через все вложенные компоненты ленты. Провайдится в ChatPanel по session.model.
export const AssistantNameContext = createContext<string>('Ассистент');
export function useAssistantName(): string {
  return useContext(AssistantNameContext);
}

// Контекст точной стоимости генераций fal.ai: requestId → списанная сумма (USD).
// Наполняется из fal_cost-сообщений (backend опрашивает billing-events по request_id).
export const FalCostContext = createContext<Map<string, number>>(new Map());
