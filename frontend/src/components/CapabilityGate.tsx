import type { ReactNode } from 'react';
import { Lock } from 'lucide-react';
import type { Project, ProjectFeatureKey } from '../types';
import { useProjectFeature, featureReason } from '../lib/projectCapabilities';
import { EmptyState } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

interface UnavailableProps {
  // Ключ возможности или панели — в data-capability-gate для тестов
  feature: string;
  // Заголовок по панели: «Файлы недоступны», «Изменения недоступны»…
  title: string;
  reason: string | null | undefined;
}

// Плашка «недоступно с причиной» (ADR-016 §3.4) — единственная на весь продукт. Стоит на
// EmptyState compact, как и состояния DeviceAgentGate: одна панель не должна менять вид
// в зависимости от того, какой гейт сработал. Без хуков — панели зовут её ранним return
// из обёртки, где хук useProjectFeature уже вызван
export function CapabilityUnavailable({ feature, title, reason }: UnavailableProps) {
  return (
    <div role="status" data-capability-gate={feature} style={{ height: '100%' }}>
      <EmptyState
        compact
        icon={<Lock size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title={title}
        subtitle={reason ?? 'Недоступно для этого проекта'}
      />
    </div>
  );
}

interface Props {
  project: Project | null | undefined;
  feature: ProjectFeatureKey;
  // Заголовок плашки по умолчанию — по панели, которую закрывает гейт
  title: string;
  children: ReactNode;
  // Если да — на недоступности рендерится children=null вместо плашки. Полезно там,
  // где родитель САМ решает, что показать (например, скрыть таб целиком)
  hideWhenUnavailable?: boolean;
  // Кастомный empty state. Если не передан — рисуем CapabilityUnavailable
  fallback?: ReactNode;
}

// Гейт одной возможности (ADR-016 §3.4). Панель оборачивается в этот компонент и
// получает «недоступно с причиной» вместо себя при выключенной возможности. Никаких
// ручных проверок локальности в самих панелях — всё через useProjectFeature.
//
// Шаблон «доступно»: feature есть в группе, группа available=true. Матрица —
// единственная точка правды, сторож G10 (ProjectCapabilitiesGuardTests) ловит любые
// обращения к project.deviceId вне lib/projectCapabilities.
export function CapabilityGate({ project, feature, title, children, hideWhenUnavailable, fallback }: Props) {
  const available = useProjectFeature(project, feature);
  if (available) return <>{children}</>;
  if (hideWhenUnavailable) return null;
  if (fallback !== undefined) return <>{fallback}</>;
  return <CapabilityUnavailable feature={feature} title={title} reason={featureReason(project, feature)} />;
}
