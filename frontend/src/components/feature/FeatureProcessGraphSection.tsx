// Граф процесса с именем шаблона для подписи секции (F07, макет P14): имя подтверждённого
// шаблона берётся из каталога один раз на маршрут; пока не пришло или не нашлось — в подписи id.

import { useEffect, useState } from 'react';
import { workflowTemplatesApi } from '../../features/workflowTemplates/api';
import { FeatureProcessGraph, type FeatureProcessGraphProps } from './FeatureProcessGraph';
import type { WorkflowTemplateScope } from '../../features/workflowTemplates/types';

// Имя шаблона по id+scope из каталога (один запрос на смену ключа); пока не пришло или не
// нашлось — null: вызывающий подставляет id. templateId не задан — запроса нет.
export function useTemplateName(templateId: string | undefined, scope: WorkflowTemplateScope | undefined, projectId: string | undefined): string | null {
  const [name, setName] = useState<string | null>(null);
  useEffect(() => {
    setName(null);
    if (!templateId) return;
    let cancelled = false;
    workflowTemplatesApi
      .listTemplates(projectId)
      .then(entries => {
        if (cancelled) return;
        const hit = entries.find(e => e.template?.id === templateId && e.scope === scope);
        setName(hit?.template?.name || null);
      })
      .catch(() => { /* имя — украшение: без него в подписи остаётся id */ });
    return () => { cancelled = true; };
  }, [templateId, scope, projectId]);
  return name;
}

export function FeatureProcessGraphSection(props: FeatureProcessGraphProps) {
  const route = props.run.epic?.workflow ?? null;
  const name = useTemplateName(route?.templateId, route?.scope, props.run.epic?.projectId ?? undefined);
  return <FeatureProcessGraph {...props} templateName={name} />;
}
