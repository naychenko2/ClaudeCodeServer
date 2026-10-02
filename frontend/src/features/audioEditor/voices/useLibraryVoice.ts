// Голос из библиотеки, выбранный в поле операции: его вид нужен проверке запуска ещё до сервера.
// undefined — не знаем (не спрашивали, грузим, сбой сети); null — такого голоса в библиотеке нет.
// refresh — повод перечитать (возврат с вкладки «Голоса», где голос могли обучить или удалить)

import { useEffect, useState } from 'react';
import { isPersonalScope } from '../scope';
import { voicesApi, type AudioVoice } from './api';

export function useLibraryVoice(scope: string, sessionId: string | null, slug: string | null, refresh?: unknown): AudioVoice | null | undefined {
  const [voice, setVoice] = useState<AudioVoice | null | undefined>(undefined);
  useEffect(() => {
    setVoice(undefined);
    if (!slug || isPersonalScope(scope)) return;
    let alive = true;
    voicesApi.list(scope, sessionId).then(
      l => { if (alive) setVoice(l.voices.find(v => v.slug === slug) ?? null); },
      () => { /* не знаем — решит сервер */ },
    );
    return () => { alive = false; };
  }, [scope, sessionId, slug, refresh]);
  return voice;
}
