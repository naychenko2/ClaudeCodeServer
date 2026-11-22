// Общее хранилище окон графиков панели «Ферма»: «Нагрузка и кэш» (stats) и
// «Температуры» (hardware). Значения независимы (свои ключи и свои дефолты),
// логика одна: чтение с проверкой допустимых значений, fallback, запись.
// Оба обращения под try/catch: при отказе хранилища (приватный режим) выбор
// продолжает жить в памяти — тот же порядок, что у вкладки (loadTab/changeTab
// в LocalModelPanel). Ключи не зависят от контекста: выбор общий для проектов
// и личных чатов — окно графика свой у человека, а не у чата.

import type { Window as StatsWindow } from './ThroughputChart';
import type { HardwareWindow } from './useLocalModelHardware';

export const STATS_WINDOW_KEY = 'cc_local_model_stats_window';
export const HW_WINDOW_KEY = 'cc_local_model_hardware_window';

type AnyWindow = StatsWindow | HardwareWindow;

// Чтение: только допустимые значения ('1m'|'5m'|'15m'|'1h'); мусор, пустой ключ
// или отказ getItem — fallback (дефолт передаёт вызывающий: у stats 5m, у hardware 15m).
export function loadWindow(key: string, fallback: AnyWindow): AnyWindow {
  try {
    const v = localStorage.getItem(key);
    if (v === '1m' || v === '5m' || v === '15m' || v === '1h') return v;
  } catch { /* приватный режим: выбор живёт в памяти */ }
  return fallback;
}

// Запись: синхронная; при отказе — проглочена (выбор продолжает жить в памяти,
// следующий выбор перезапишет ключ, когда хранилище вернётся).
export function saveWindow(key: string, value: AnyWindow): void {
  try { localStorage.setItem(key, value); } catch { /* приватный режим */ }
}
