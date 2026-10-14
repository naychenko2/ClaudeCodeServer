// TS-отражение бэкенд-контракта LocalGpuQueueInfo (backend/.../Media/LocalGpuQueueInfo.cs).
// Все необязательные поля nullable — живой снимок очереди GPU, не учёт.

// Прогресс семплера: текущий шаг, всего шагов, текущая стадия (если известна).
export interface LocalGpuSteps {
  step: number;
  total: number;
  stage?: number | null;
}

// Кто сейчас держит GPU: вид прогона, тяжёлый ли, оценка оставшегося времени.
export interface LocalGpuBusy {
  kind: string;
  heavy: boolean;
  remainingSeconds?: number | null;
}

// Снимок очереди GPU.
export interface LocalGpuQueueInfo {
  ahead?: number | null;
  startInSeconds?: number | null;
  steps?: LocalGpuSteps | null;
  percent?: number | null;
  percentExact?: boolean | null;
  remainingSeconds?: number | null;
  busy?: LocalGpuBusy | null;
  downSince?: number | null;
  // M2: true — задача идёт (не «в очереди»); false/undefined/null — старая логика (ahead решает)
  running?: boolean | null;
}
