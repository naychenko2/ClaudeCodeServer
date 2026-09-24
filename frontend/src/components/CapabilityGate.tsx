import type { ReactNode } from 'react';
import { Lock } from 'lucide-react';
import type { Project, ProjectFeatureKey } from '../types';
import { useProjectFeature, featureReason } from '../lib/projectCapabilities';
import { C, FS, R, SP } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

interface Props {
  project: Project | null | undefined;
  feature: ProjectFeatureKey;
  // Пустой стейт при недоступности. По умолчанию — компактная плашка «недоступно»
  // с иконкой замка и причиной. Для панелей удобнее empty state с большим текстом —
  // передавайте свой
  children: ReactNode;
  // Если да — на недоступности рендерится children=null вместо плашки. Полезно там,
  // где родитель САМ решает, что показать (например, скрыть таб целиком)
  hideWhenUnavailable?: boolean;
  // Кастомный empty state. Если не передан — рисуем дефолтную плашку с причиной
  fallback?: ReactNode;
}

// Гейт одной возможности (ADR-016 §3.4). Панель оборачивается в этот компонент и
// получает «недоступно с причиной» вместо себя при выключенной возможности. Никаких
// ручных проверок локальности в самих панелях — всё через useProjectFeature.
//
// Шаблон «доступно»: feature есть в группе, группа available=true. Матрица —
// единственная точка правды, сторож G10 (ProjectCapabilitiesGuardTests) ловит любые
// обращения к project.deviceId вне lib/projectCapabilities.
export function CapabilityGate({ project, feature, children, hideWhenUnavailable, fallback }: Props) {
  const available = useProjectFeature(project, feature);
  if (available) return <>{children}</>;
  if (hideWhenUnavailable) return null;
  if (fallback !== undefined) return <>{fallback}</>;
  const reason = featureReason(project, feature) ?? 'Подсистема недоступна для этого проекта';
  return (
    <div
      role="status"
      data-capability-gate={feature}
      style={{
        padding: '20px 16px',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        gap: SP.sm,
        color: C.textMuted,
        background: C.bgPanel,
        borderRadius: R.lg,
        border: `1px dashed ${C.border}`,
        margin: SP.md,
      }}
    >
      <Lock size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.textMuted }} />
      <div style={{ fontSize: FS.sm, color: C.textPrimary, fontWeight: 600 }}>
        Подсистема недоступна
      </div>
      <div style={{ fontSize: FS.xs, textAlign: 'center', maxWidth: 320, lineHeight: 1.5 }}>
        {reason}
      </div>
    </div>
  );
}
