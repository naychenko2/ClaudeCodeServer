// Состояние редактора ↔ сервер (ADR-018 §2): редактор пишет своё PUT state с дебаунсом 500 мс
// и ревизией, на 409 перечитывает и пишет заново; событие image_chat_state от агента
// применяется к открытому редактору. Эхо своей записи и чужие ручные правки не применяются:
// ревизия не выше известной — событие уже учтено.

import { useCallback, useEffect, useRef, useState } from 'react';
import { isStaleRevision, type ImageChatState, type ImageChatStateEvent, type ImageEditorApi } from '../api';
import { mergeRemote, remoteChanges, settingsKey, settingsToState, type EditorSettings } from './stateSync';
import type { ImageChatStateChange } from '../api';

export const STATE_DEBOUNCE_MS = 500;

// Актуальное состояние из тела 409: сервер отдаёт его рядом с ошибкой
function conflictState(e: unknown): ImageChatState | null {
  const st = (e as { body?: { state?: unknown } } | null)?.body?.state;
  return st && typeof st === 'object' ? st as ImageChatState : null;
}

export function useChatStateSync({ api, projectId, sessionId, settings, maskFor, onLoad, onAgent }: {
  api: ImageEditorApi;
  projectId: string;
  sessionId: string | null;
  settings: EditorSettings;
  // Маска кисти для записи — только когда сменилась canvasRevision
  maskFor: () => Promise<Blob | null>;
  // Состояние прочитано с сервера (открытие чата): редактор забирает сохранённое
  onLoad: (state: ImageChatState) => void;
  onAgent: (e: ImageChatStateEvent) => void;
}) {
  const base = useRef<ImageChatState | null>(null);
  const [loaded, setLoaded] = useState(0);
  const live = useRef({ settings, maskFor, onLoad, onAgent });
  useEffect(() => { live.current = { settings, maskFor, onLoad, onAgent }; });
  // Записи идут цепочкой: вторая стартует от ревизии, которую вернула первая
  const chain = useRef<Promise<void>>(Promise.resolve());

  // Новее известного — берём; ответ PUT мог прийти позже события агента
  const adopt = useCallback((st: ImageChatState) => {
    if (!base.current || st.revision > base.current.revision) base.current = st;
  }, []);

  useEffect(() => {
    base.current = null;
    if (!sessionId) return;
    let alive = true;
    api.getChatState(projectId, sessionId)
      .then(st => {
        if (!alive) return;
        base.current = st;
        live.current.onLoad(st);
        setLoaded(n => n + 1);
      })
      .catch(() => { /* состояния нет — запишем своё первой правкой */ });
    const off = api.subscribeChatState(e => {
      if (e.sessionId !== sessionId || e.projectId !== projectId || !base.current) return;
      if (e.revision <= base.current.revision) return;
      base.current = e.state;
      if (e.changedBy === 'agent') live.current.onAgent(e);
    });
    return () => { alive = false; off(); };
  }, [api, projectId, sessionId]);

  const push = useCallback(async (): Promise<void> => {
    if (!sessionId) return;
    await writeState({
      api, projectId, sessionId, settings: live.current.settings, maskFor: live.current.maskFor,
      base: () => base.current, setBase: st => { base.current = st; }, adopt,
      // Чужая запись, которую событие уже не принесёт (ревизия учтена): применяем как правку агента
      onRemote: (st, changes) => live.current.onAgent({
        type: 'image_chat_state', sessionId, projectId, revision: st.revision, state: st, changedBy: 'agent', changes,
      }),
    });
  }, [api, projectId, sessionId, adopt]);

  const key = settingsKey(settingsToState({} as ImageChatState, settings));
  useEffect(() => {
    if (!sessionId || !loaded) return;
    const t = setTimeout(() => {
      chain.current = chain.current.then(push).catch(() => {});
    }, STATE_DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [key, sessionId, loaded, push]);
}

// Одна запись состояния. Вторая попытка — только после 409: кто-то записал раньше (чаще —
// агент), перечитываем и пишем своё поверх свежей ревизии
export async function writeState({ api, projectId, sessionId, settings: mine, maskFor, base, setBase, adopt, onRemote }: {
  api: Pick<ImageEditorApi, 'putChatState' | 'getChatState'>;
  projectId: string;
  sessionId: string;
  settings: EditorSettings;
  maskFor: () => Promise<Blob | null>;
  base: () => ImageChatState | null;
  setBase: (st: ImageChatState) => void;
  adopt: (st: ImageChatState) => void;
  onRemote: (st: ImageChatState, changes: ImageChatStateChange[]) => void;
}): Promise<void> {
  let settings = mine;
  for (let attempt = 0; attempt < 2; attempt++) {
    const b = base();
    if (!b) return;
    const next = settingsToState(b, settings);
    if (settingsKey(next) === settingsKey(b)) return;
    const mask = next.canvasRevision && next.canvasRevision !== b.canvasRevision
      ? await maskFor().catch(() => null)
      : null;
    try {
      adopt(await api.putChatState(projectId, sessionId, next, mask));
      return;
    } catch (e) {
      if (!isStaleRevision(e)) return;
      const fresh = conflictState(e) ?? await api.getChatState(projectId, sessionId).catch(() => null);
      if (!fresh) return;
      // Поля, которые поменял другой (агент), берём с сервера — иначе запись их затёрла бы
      const changes = remoteChanges(b, fresh);
      setBase(fresh);
      if (changes.length) {
        settings = mergeRemote(settings, fresh, changes);
        onRemote(fresh, changes);
      }
    }
  }
}
