// Вес результата «Размер и сжатие»: POST …/transform?dryRun=true, не чаще раза в 300 мс
// (ADR-018 §9). Шаг не пишется — сервер кодирует в память и отдаёт только bytes.

import { useEffect, useMemo, useState } from 'react';
import type { ImageEditorApi, ImageEncodeSpec, ImageTransformBase, ImageTransformOp } from './api';
import { weightEstimator } from './transforms';

export function useWeight(api: ImageEditorApi, projectId: string, base: Promise<ImageTransformBase> | null,
  ops: ImageTransformOp[], encode: ImageEncodeSpec | null) {
  const [state, setState] = useState<{ key: string; bytes: number | null; error: string | null }>({ key: '', bytes: null, error: null });
  const key = base && encode ? JSON.stringify({ ops, encode }) : '';

  const estimator = useMemo(() => weightEstimator(
    (b, o, e) => api.transform(projectId, { base: b, ops: o, encode: e }, { dryRun: true }),
    (k, bytes, error) => setState({ key: k, bytes, error }),
  ), [api, projectId]);

  useEffect(() => () => estimator.cancel(), [estimator]);

  useEffect(() => {
    if (!base || !encode) return;
    estimator.request(base, key, ops, encode);
    // ops и encode входят в key — пересчёт только при смене содержимого, а не ссылки
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [base, key, estimator]);

  const fresh = state.key === key;
  return {
    bytes: fresh ? state.bytes : null,
    error: fresh ? state.error : null,
    loading: !!key && (!fresh || (state.bytes == null && !state.error)),
  };
}
