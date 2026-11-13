// Граф процесса с именем шаблона для подписи секции (F07, макет P14): имя подтверждённого
// шаблона берётся из каталога один раз на маршрут; пока не пришло или не нашлось — в подписи id.

import { useEffect, useState } from 'react';
import { workflowTemplatesApi } from '../../features/workflowTemplates/api';
import { FeatureProcessGraph, type FeatureProcessGraphProps } from './FeatureProcessGraph';

export function FeatureProcessGraphSection(props: FeatureProcessGraphProps) {
  const route = props.run.epic?.workflow ?? null;
  const projectId = props.run.epic?.projectId ?? undefined;
  const [name, setName] = useState<string | null>(null);
  const templateId = route?.templateId;
  const scope = route?.scope;

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

  return <FeatureProcessGraph {...props} templateName={name} />;
}
