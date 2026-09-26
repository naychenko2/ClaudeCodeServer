// Мост «лента чата → открытый редактор» (ADR-018 §10.3): React-контекст модуля. Редактор
// оборачивает им чат ядра, и карточки ленты внутри ChatPanel его видят — провайдер и
// потребитель в одном инстансе модуля. В полном чате провайдера нет: карточки предлагают
// «Открыть в редакторе».

import { createContext, useContext } from 'react';

export interface ImageEditorBridgeApi {
  // Чат, открытый в редакторе: карточки чужого чата мостом не пользуются
  sessionId: string | null;
  busy: boolean;
  // Сумма генерации по текущей котировке редактора: «≈ $0.24»
  priceSum: string | null;
  insertPrompt: (prompt: string, opts?: { count?: number | null }) => void;
  generate: (prompt: string, opts?: { count?: number | null }) => void;
  // Показать задачу в центре редактора: прогресс, варианты или ошибку
  showJob: (jobId: string, count: number, expectedSeconds?: number | null) => void;
  // Задача агента ещё идёт — редактор берёт её под наблюдение, если свободен
  trackJob: (jobId: string, count: number, expectedSeconds?: number | null) => void;
}

export const ImageEditorBridge = createContext<ImageEditorBridgeApi | null>(null);

export function useImageEditorBridge(sessionId: string | null): ImageEditorBridgeApi | null {
  const bridge = useContext(ImageEditorBridge);
  return bridge && sessionId && bridge.sessionId === sessionId ? bridge : null;
}
