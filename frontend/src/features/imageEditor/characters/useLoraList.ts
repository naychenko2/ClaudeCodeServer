// Список LoRA из ComfyUI с датами обучения. Грузится один раз на карточку персонажа (в CharacterCard)
// и отдаётся пропсом и в селект, и в строку «своя LoRA»; reload перечитывает его после обучения/удаления версии.

import { useCallback, useEffect, useState } from 'react';
import type { ImageEditLoras, ImageEditorApi } from '../api';

export function useLoraList(api: ImageEditorApi, projectId: string): { list: ImageEditLoras | null; reload: () => void } {
  const [list, setList] = useState<ImageEditLoras | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let alive = true;
    api.loras(projectId).then(r => { if (alive) setList(r); }).catch(() => { if (alive) setList({ available: false, items: [] }); });
    return () => { alive = false; };
  }, [api, projectId, tick]);
  const reload = useCallback(() => setTick(t => t + 1), []);
  return { list, reload };
}
