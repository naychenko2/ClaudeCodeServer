// Свойства открытого документа для центральной области: загрузка, обновление и запись.
//
// Хук, а не состояние внутри компонента, потому что представлений у одних и тех же данных
// два (полоса под шапкой и сайдбар справа) — иначе каждое ходило бы в API само, и на один
// файл приходилось бы вдвое больше запросов.
//
// Данные тянутся лесенкой: сначала дешёвая настройка области (в ней схема типов) — нет
// схемы, дальше не идём; потом сам документ; индекс запрашивается ТОЛЬКО когда у типа есть
// свойство-ссылка, которому нужен список целей. Просмотрщик открывают на любом файле
// репозитория, и платить тремя запросами за каждый .png ни к чему.

import { useCallback, useEffect, useState } from 'react';
import type { DocDetail, DocEntry, DocTypeSchema } from '../../types';
import { api } from '../../lib/api';
import { onFilesChanged } from '../../lib/signalr';
import { typeOf } from '../../lib/docsTypes';

export interface DocPropsState {
  doc: DocDetail | null;
  type: DocTypeSchema | null;
  index: DocEntry[];
  savingKey: string | null;
  error: string | null;
  save: (key: string, value: string | null) => void;
}

export function useDocProps(projectId: string, filePath: string, enabled: boolean): DocPropsState {
  const [doc, setDoc] = useState<DocDetail | null>(null);
  const [type, setType] = useState<DocTypeSchema | null>(null);
  const [index, setIndex] = useState<DocEntry[]>([]);
  const [savingKey, setSavingKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!enabled) return;
    let alive = true;
    // Файл вне области документации — 404 на первом же шаге, и это нормальный ответ:
    // свойств у него просто нет
    void (async () => {
      try {
        const scope = await api.docs.scope(projectId);
        if (!alive) return;
        if (!scope.docTypes?.length) { setDoc(null); setType(null); return; }

        const detail = await api.docs.doc(projectId, filePath);
        if (!alive) return;
        const t = typeOf(scope.docTypes, detail);
        setDoc(t ? detail : null);
        setType(t);

        if (t?.properties.some(p => p.kind === 'docLink')) {
          const list = await api.docs.index(projectId);
          if (alive) setIndex(list);
        }
      } catch {
        if (alive) { setDoc(null); setType(null); }
      }
    })();
    return () => { alive = false; };
  }, [projectId, filePath, enabled]);

  useEffect(() => {
    // Сброс СРАЗУ, а не по ответу: иначе при переходе к следующему файлу под его шапкой
    // ещё секунду висят свойства предыдущего — и это не «пока грузится», а прямая ложь,
    // потому что контролы кликабельны и правка ушла бы не в тот документ
    setDoc(null);
    setType(null);
    setIndex([]);
    setError(null);
    return load();
  }, [load]);

  // Документ правят и мимо этих контролов — из чата, руками в редакторе, из панели
  // документации. Без подписки значения оставались бы прежними
  useEffect(() => onFilesChanged(({ projectId: p, paths }) => {
    if (p !== projectId) return;
    if (paths.some(x => x.toLowerCase() === filePath.toLowerCase())) load();
  }), [projectId, filePath, load]);

  const save = useCallback((key: string, value: string | null) => {
    setSavingKey(key);
    setError(null);
    api.docs.setProperty(projectId, filePath, key, value)
      .then(res => setDoc(d => (d ? { ...d, properties: res.properties } : d)))
      .catch(() => setError(`Не удалось сохранить «${key}»`))
      .finally(() => setSavingKey(null));
  }, [projectId, filePath]);

  return { doc, type, index, savingKey, error, save };
}
