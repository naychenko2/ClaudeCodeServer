import { createContext, useContext } from 'react';
import type { Persona, TeamPlanDecision } from '../../types';

// Контекст текущего проекта — для резолва локальных путей картинок в сообщениях
export const ChatProjectContext = createContext<{ id: string; rootPath: string } | null>(null);

// Открыть файл проекта на просмотр — тем же путём, что дерево и карточки инструментов.
// Контекстом, а не пропом: MarkdownContent сидит глубоко в ленте, а ссылки на файлы
// в тексте ассистента нужны в каждом её отпрыске.
export const ChatOpenFileContext = createContext<((path: string) => void) | null>(null);

// Персона текущего чата, если он ведётся от её лица.
// Провайдится в ChatPanel — лента показывает её аватар у реплик ассистента,
// не таща persona-проп через все вложенные компоненты.
export const PersonaContext = createContext<Persona | null>(null);

// Имя ассистента сессии (Claude | DeepSeek | GLM | …) — для строк в UI, чтобы не тащить проп
// через все вложенные компоненты ленты. Провайдится в ChatPanel по session.model.
export const AssistantNameContext = createContext<string>('Ассистент');
export function useAssistantName(): string {
  return useContext(AssistantNameContext);
}

// Обвязка карточки плана «Командной реализации»: состояние режима (нужно для
// подписи запущенного плана, примечания об авто-волнах и состава кандидатов) плюс
// действия. Контекстом, а не пропами — карточка лежит глубоко в ленте.
// null — режим выключен либо чат не штаб: карточка тогда показывается только для чтения.
export interface TeamPlanChatContext {
  autoWaves: boolean;
  waveNumber: number;
  // Явно выбранный состав исполнителей; пустой — вся команда проекта
  executorPersonaIds: string[];
  onRespond: (planId: string, decision: TeamPlanDecision, subtaskId?: string, executorPersonaId?: string) => void;
  // Правка плана уходит координатору обычным сообщением (как «Ответить» у эскалаций)
  onSendMessage: (text: string) => void;
}
export const TeamPlanContext = createContext<TeamPlanChatContext | null>(null);

// Обвязка карточки остановки «Командной реализации» (Э4): решение уходит в хаб
// (кнопка + необязательный комментарий), карточка гаснет. Контекстом, как у плана —
// карточка лежит глубоко в ленте. null — режим выключен: карточка только для чтения.
export interface TeamEscalationChatContext {
  onRespond: (escalationId: string, actionId?: string, comment?: string) => void;
}
export const TeamEscalationContext = createContext<TeamEscalationChatContext | null>(null);

// Контекст точной стоимости генераций fal.ai: requestId → списанная сумма (USD).
// Наполняется из fal_cost-сообщений (backend опрашивает billing-events по request_id).
export const FalCostContext = createContext<Map<string, number>>(new Map());
