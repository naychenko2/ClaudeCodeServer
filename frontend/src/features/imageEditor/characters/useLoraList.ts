// Список LoRA из ComfyUI с датами обучения. Один источник и для селекта, и для строки «своя LoRA».

import { useEffect, useState } from 'react';
import type { ImageEditLoras, ImageEditorApi } from '../api';

export function useLoraList(api: ImageEditorApi, projectId: string, enabled: boolean): ImageEditLoras | null {
  const [list, setList] = useState<ImageEditLoras | null>(null);
  useEffect(() => {
    if (!enabled || list) return;
    let alive = true;
    api.loras(projectId).then(r => { if (alive) setList(r); }).catch(() => { if (alive) setList({ available: false, items: [] }); });
    return () => { alive = false; };
  }, [enabled, list, api, projectId]);
  return list;
}
