// Каталог поставщиков проекта: один запрос на вкладку, общий для карточек, полосы и режима.

import { useEffect, useState } from 'react';
import { imageEditorApi, type ImageEditCatalog } from '../api';

const catalogs = new Map<string, Promise<ImageEditCatalog | null>>();

export function loadCatalog(projectId: string): Promise<ImageEditCatalog | null> {
  let p = catalogs.get(projectId);
  if (!p) {
    p = imageEditorApi().catalog(projectId).catch(() => { catalogs.delete(projectId); return null; });
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
