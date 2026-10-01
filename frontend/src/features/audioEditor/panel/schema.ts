// Схема частных параметров модели для «Полей операции» и «Дополнительно» (GET /api/audio-editor/schema).
// Кеш на сессию вкладки: схема зависит только от поставщика, модели и операции.

import { useEffect, useState } from 'react';
import { audioApi, type AudioOp, type AudioParamSchema } from '../api';

export interface SchemaState { schema: AudioParamSchema | null; error: string | null; loading: boolean }

const cache = new Map<string, Promise<AudioParamSchema>>();

export function loadSchema(provider: string, model: string, op: AudioOp): Promise<AudioParamSchema> {
  const key = `${provider}|${model}|${op}`;
  let p = cache.get(key);
  if (!p) {
    p = audioApi.schema(provider, model, op);
    // Отказ не кешируем: поставщик мог ожить
    p.catch(() => cache.delete(key));
    cache.set(key, p);
  }
  return p;
}

export const __resetSchemaCache = () => cache.clear();

const IDLE: SchemaState = { schema: null, error: null, loading: false };

// provider/model null — схемы нет (правка без ИИ, «Авто» без модели)
export function useSchema(provider: string | null, model: string | null, op: AudioOp): SchemaState {
  const [st, setSt] = useState<SchemaState>(IDLE);
  useEffect(() => {
    if (!provider || !model) { setSt(IDLE); return; }
    let alive = true;
    setSt({ schema: null, error: null, loading: true });
    loadSchema(provider, model, op).then(
      schema => { if (alive) setSt({ schema, error: null, loading: false }); },
      (e: Error) => { if (alive) setSt({ schema: null, error: e.message || 'Схема параметров недоступна', loading: false }); },
    );
    return () => { alive = false; };
  }, [provider, model, op]);
  return st;
}
