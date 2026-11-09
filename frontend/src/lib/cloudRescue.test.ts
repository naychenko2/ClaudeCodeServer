// Тесты общего модуля cloudRescue: guard, isStandDown, isCloudProvider,
// текст диалога и подписи кнопки.

import { describe, expect, it } from 'vitest';
import {
  makeLaunchGuard, isStandDown, isCloudProvider,
  CLOUD_DIALOG_TITLE, cloudDialogSubtitle, cloudButtonLabel,
} from './cloudRescue';
import type { LocalGpuQueueInfo } from '../types/localGpuQueue';

// ── makeLaunchGuard ──

describe('makeLaunchGuard', () => {
  it('первый begin — true, повторный до end — false, после end — true', () => {
    const g = makeLaunchGuard();
    expect(g.begin()).toBe(true);
    expect(g.begin()).toBe(false);
    g.end();
    expect(g.begin()).toBe(true);
  });

  it('end без begin — безопасно (guard уже свободен)', () => {
    const g = makeLaunchGuard();
    g.end();
    expect(g.begin()).toBe(true);
  });
});

// ── isStandDown ──

describe('isStandDown', () => {
  it('false без gpu', () => expect(isStandDown(null)).toBe(false));
  it('false без downSince', () => expect(isStandDown({} as LocalGpuQueueInfo)).toBe(false));
  it('false: downSince=null (бэк шлёт null, не undefined)', () => {
    expect(isStandDown({ downSince: null } as LocalGpuQueueInfo)).toBe(false);
  });
  it('true при downSince', () => {
    expect(isStandDown({ downSince: 1_700_000_000_000 } as LocalGpuQueueInfo)).toBe(true);
  });
});

// ── isCloudProvider ──

describe('isCloudProvider', () => {
  it('true: доступен и не local', () => {
    expect(isCloudProvider({ key: 'fal', available: true })).toBe(true);
  });
  it('false: local', () => {
    expect(isCloudProvider({ key: 'local', available: true })).toBe(false);
  });
  it('false: недоступен', () => {
    expect(isCloudProvider({ key: 'fal', available: false })).toBe(false);
  });
  it('false: null', () => {
    expect(isCloudProvider(null)).toBe(false);
  });
  it('false: undefined', () => {
    expect(isCloudProvider(undefined)).toBe(false);
  });
});

// ── Текст диалога ──

describe('cloudDialogSubtitle', () => {
  it('с ценой: «Стоимость ≈ $0.04. Локальная задача продолжит ждать.»', () => {
    expect(cloudDialogSubtitle('≈ $0.04')).toBe('Стоимость ≈ $0.04. Локальная задача продолжит ждать.');
  });
  it('без цены: «Локальная задача продолжит ждать.»', () => {
    expect(cloudDialogSubtitle(null)).toBe('Локальная задача продолжит ждать.');
  });
  it('слова «отмен…» нет (мутация: вернуть старую строку)', () => {
    expect(cloudDialogSubtitle('≈ $0.04')).not.toMatch(/отмен/i);
    expect(cloudDialogSubtitle(null)).not.toMatch(/отмен/i);
  });
});

describe('CLOUD_DIALOG_TITLE', () => {
  it('«Сделать в облаке?»', () => expect(CLOUD_DIALOG_TITLE).toBe('Сделать в облаке?'));
});

// ── cloudButtonLabel ──

describe('cloudButtonLabel', () => {
  it('с ценой: «Сделать в облаке · ≈ $0.04»', () => {
    expect(cloudButtonLabel('≈ $0.04')).toBe('Сделать в облаке · ≈ $0.04');
  });
  it('без цены: «Сделать в облаке · ≈ …»', () => {
    expect(cloudButtonLabel(null)).toBe('Сделать в облаке · ≈ …');
  });
});
