// Каталог поставщиков проекта: один запрос на вкладку, общий для карточек, полосы и режима.

import { useEffect, useState } from 'react';
import { imageEditorApi, type ImageEditCatalog } from '../api';

const catalogs = new Map<string, Promise<ImageEditCatalog | null>>();
// Уже пришедшие каталоги — для строк вне React-рендера (меню переключателя полос)
const resolved = new Map<string, ImageEditCatalog>();

export const getCatalog = (projectId: string) => resolved.get(projectId) ?? null;

export function loadCatalog(projectId: string): Promise<ImageEditCatalog | null> {
  let p = catalogs.get(projectId);
  if (!p) {
    p = imageEditorApi().catalog(projectId)
      .then(c => { resolved.set(projectId, c); return c; })
      .catch(() => { catalogs.delete(projectId); return null; });
    catalogs.set(projectId, p);
  }
  return p;
}

export function useCatalog(projectId: string | null): ImageEditCatalog | null {
  const [catalog, setCatalog] = useState<ImageEditCatalog | null>(null);
  useEffect(() => {
    if (!projectId) return;
    let alive = true;
    void loadCatalog(projectId).then(c => { if (alive) setCatalog(c); });
    return () => { alive = false; };
  }, [projectId]);
  return catalog;
}
