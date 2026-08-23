import { createContext, useContext } from 'react';
import type { Persona, Task, TeamPlanDecision } from '../../types';

// Контекст текущего проекта — для резолва локальных путей картинок в сообщениях
export const ChatProjectContext = createContext<{ id: string; rootPath: string } | null>(null);

// Корень активного git-дерева (worktree) хода/чата — пути в ленте сначала пробуют
// относительность к нему (короче и без префикса .claude/worktrees/…), и только
// при неудаче — к корню проекта. Провайдится ChatPanel (turnTree.path ?? session.worktreePath);
// null — дерева нет, пути считаются как раньше, только от rootPath.
export const ChatTreePathContext = createContext<string | null>(null);

// Открыть файл проекта на просмотр — тем же путём, что дерево и карточки инструментов.
// Контекстом, а не пропом: MarkdownContent сидит глубоко в ленте, а ссылки на файлы
// в тексте ассистента нужны в каждом её отпрыске.
export const ChatOpenFileContext = createContext<((path: string) => void) | null>(null);

// Открыть URL в панели «Чтение» — та же логика: MarkdownContent сидит глубоко в ленте,
// а перехват клика по внешней ссылке и кнопка-компаньон нужны в каждом её отпрыске.
// null — фича выключена (флаг link-reader) или чат не поддерживает ридер: ссылки
// ведут себя как раньше (новая вкладка), кнопка-компаньон не рисуется.
export const ChatOpenReaderContext = createContext<((url: string) => void) | null>(null);

// Открыть задачу РЯДОМ с лентой — правой половиной центральной зоны воркспейса, тем же
// приёмом, что открытый файл и «Чтение» (split чат|деталь): разговор остаётся перед
// глазами. Контекстом, а не пропом — карточка доклада сидит глубоко в ленте.
// null — места рядом нет (мобила, планшет, чат вне воркспейса): карточка показывает
// детали задачи модалкой поверх ленты, как раньше.
export const ChatOpenTaskContext = createContext<((task: Task) => void) | null>(null);

// Id текущей сессии — шторке «какой промпт ушёл» под постом: она грузит снимок по REST,
// а ChatItemView сидит глубоко в ленте и id сессии пропом не получает.
export const ChatSessionContext = createContext<string | null>(null);

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
  // Id текущего плана режима — свёрнутая карточка отличает по нему «свой» ход волн
  // от чужого (старая версия плана после перепланирования ходом не владеет)
  planCardId: string | null;
  // Явно выбранный состав исполнителей; пустой — вся команда проекта
  executorPersonaIds: string[];
  // feedback — правка плана (decision 'edit'): сервер сам гасит карточку и пересобирает план
  onRespond: (planId: string, decision: TeamPlanDecision, subtaskId?: string, executorPersonaId?: string, feedback?: string) => void;
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

// Списанные кредиты генераций glif: jobId → кредиты. Наполняется из glif_cost-элементов
// ленты (только те, где backend нашёл billing в payload — остальных в карте нет).
export const GlifCostContext = createContext<Map<string, number>>(new Map());

// Видимое медиа tool-блоков ленты после кросс-блочного дедупа (glif project_update +
// media_view одного URL — один блок; строится в ChatPanel через buildMediaVisibility).
// Значение по id tool_use — массив медиа (пустой = всё скрыто дедупом).
// null — карта не построена (рендер вне ленты): блок показывает своё медиа как есть.
// Тип массива — MediaItem из MediaBlock (импорт type-only, чтобы не тянуть компонент в контексты)
export const MediaVisibilityContext = createContext<Map<string, import('./MediaBlock').MediaItem[]> | null>(null);

// Кто сейчас звучит: индекс реплики в ленте, у аватара которой идёт пульс, и цвет
// говорящей персоны для колец. Единый источник с цветом aurora-сияния над композером —
// ChatPanel считает его одним мемо, чтобы «кольцо» и «свет» не разъезжались.
// null — озвучки нет либо говорит голос инстанса (лицо не резолвится, подсвечивать нечего).
export const SpeakingItemContext = createContext<{ index: number; color: string } | null>(null);
