import { createContext } from 'react';

// Контекст текущего проекта — для резолва локальных путей картинок в сообщениях
export const ChatProjectContext = createContext<{ id: string; rootPath: string } | null>(null);

// Контекст точной стоимости генераций fal.ai: requestId → списанная сумма (USD).
// Наполняется из fal_cost-сообщений (backend опрашивает billing-events по request_id).
export const FalCostContext = createContext<Map<string, number>>(new Map());
