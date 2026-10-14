// Витрина строки ожидания локальной GPU-генерации (шаг 34):
// все 5+ состояний × длинная/короткая форма × 320/360/широко.
// Компонент LocalGpuQueueLine + форматтер describeLocalGpuQueue.

import { useState } from 'react';
import { Cpu } from 'lucide-react';
import { Island, IslandHeader, InlineSegmented } from '../components/ui';
import { C, FS, ISLAND, R, SP } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { useIsMobile } from '../lib/breakpoints';
import { LocalGpuQueueLine } from '../components/LocalGpuQueueLine';
import type { LocalGpuQueueInfo } from '../types/localGpuQueue';

type Width = 'wide' | 'narrow';

// Демо-данные для каждого состояния.
// now — замороженный момент (2025-10-09), чтобы downSince давал стабильное время
// в витрине: время намерто фиксировано для воспроизводимости, а не текущий Date.now().
const NOW = 1760000000000; // ~2025-10-09

const DEMO: { label: string; long: LocalGpuQueueInfo; short: LocalGpuQueueInfo }[] = [
  {
    label: '1. В очереди (ahead=1, ETA 4 мин)',
    long: { ahead: 1, startInSeconds: 240, percentExact: false },
    short: { ahead: 1, startInSeconds: 240, percentExact: false },
  },
  {
    label: '1б. В очереди (ahead=0, ETA 40 с)',
    long: { ahead: 0, startInSeconds: 40, percentExact: false },
    short: { ahead: 0, startInSeconds: 40, percentExact: false },
  },
  {
    label: '1в. В очереди (без ETA)',
    long: { ahead: 2, startInSeconds: null, percentExact: false },
    short: { ahead: 2, startInSeconds: null, percentExact: false },
  },
  {
    label: '1г. В очереди (ahead=null — без номеров)',
    long: { ahead: null, startInSeconds: null, percentExact: false },
    short: { ahead: null, startInSeconds: null, percentExact: false },
  },
  {
    label: '2. Считается (шаг 12/25, осталось 30 с)',
    long: { steps: { step: 12, total: 25 }, remainingSeconds: 30, percent: 48, percentExact: true },
    short: { steps: { step: 12, total: 25 }, remainingSeconds: 30, percent: 48, percentExact: true },
  },
  {
    label: '2б. Считается (stage=2, шаг 3/8)',
    long: { steps: { step: 3, total: 8, stage: 2 }, remainingSeconds: 15, percent: 38, percentExact: true },
    short: { steps: { step: 3, total: 8, stage: 2 }, remainingSeconds: 15, percent: 38, percentExact: true },
  },
  {
    label: '4. Считается, ETA не замерено (шаги есть)',
    long: { steps: { step: 12, total: 25 }, remainingSeconds: null, percent: 48, percentExact: true },
    short: { steps: { step: 12, total: 25 }, remainingSeconds: null, percent: 48, percentExact: true },
  },
  {
    label: '4б. Считается, нет ни ETA, ни шагов',
    long: { steps: null, remainingSeconds: null, percent: 40, percentExact: false },
    short: { steps: null, remainingSeconds: null, percent: 40, percentExact: false },
  },
  {
    label: '3. Впереди тяжёлая (видео, 12 мин)',
    long: { busy: { kind: 'video', heavy: true, remainingSeconds: 720 }, percentExact: false },
    short: { busy: { kind: 'video', heavy: true, remainingSeconds: 720 }, percentExact: false },
  },
  {
    label: '3б. Впереди тяжёлая (без ETA)',
    long: { busy: { kind: 'train', heavy: true, remainingSeconds: null }, percentExact: false },
    short: { busy: { kind: 'train', heavy: true, remainingSeconds: null }, percentExact: false },
  },
  {
    label: '5а. Стенд недоступен (downSince)',
    long: { downSince: NOW - 3600_000, percentExact: false },
    short: { downSince: NOW - 3600_000, percentExact: false },
  },
  {
    label: '5б. Стенд недоступен (короткая)',
    long: { downSince: NOW - 1800_000, percentExact: false },
    short: { downSince: NOW - 1800_000, percentExact: false },
  },
];

export function LocalGpuQueueKitSection() {
  const isMobile = useIsMobile();
  const [width, setWidth] = useState<Width>(isMobile ? 'narrow' : 'wide');
  const narrow = width === 'narrow';

  return (
    <Island>
      <IslandHeader
        icon={<Cpu size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />}
        title="Строка ожидания локальной GPU — шаг 34"
      />
      <div style={{ padding: ISLAND.pad, display: 'flex', flexDirection: 'column', gap: ISLAND.gap }}>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.lg, alignItems: 'center' }}>
          <InlineSegmented<Width> value={width} onChange={setWidth} options={[
            { value: 'wide', label: 'широко' },
            { value: 'narrow', label: '320' },
          ]} />
        </div>

        <div style={{
          width: narrow ? 320 : '100%',
          maxWidth: '100%',
          display: 'flex',
          flexDirection: 'column',
          gap: SP.md,
        }}>
          {DEMO.map(({ label, long, short }) => (
            <div key={label} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>{label}</span>
              <div style={{
                padding: `${SP.sm}px ${SP.md}px`,
                border: `1px solid ${C.borderLight}`,
                borderRadius: R.lg,
                background: C.bgCard,
                display: 'flex',
                flexDirection: 'column',
                gap: SP.xs,
              }}>
                {/* Длинная форма */}
                <LocalGpuQueueLine info={long} compact={false} now={NOW} />
                {/* Короткая форма (только в narrow-режиме) */}
                {narrow && <LocalGpuQueueLine info={short} compact now={NOW} />}
              </div>
            </div>
          ))}
        </div>
      </div>
    </Island>
  );
}
