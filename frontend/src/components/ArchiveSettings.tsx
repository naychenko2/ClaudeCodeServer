// Настройка автоправила архивации чатов (план «Архив чатов» v4, шаг 6, флаг
// chat-auto-archive). ВЕСЬ блок рисуется только при включённом флаге — ручной
// архив, раздел «Архив» и сводка карточки работают и без тумблера. Здесь же
// кнопка «Применить сейчас», которая запускает первый проход по накопившимся
// старым чатам — фоновый тик правила их сам не трогает, решение человека.

import { useEffect, useState } from 'react';
import { Archive } from 'lucide-react';
import { archiveRuleApi } from '../api/chats';
import { showToast } from '../lib/toast';
import { C, FONT, R } from '../lib/design';
import { Button } from './ui';

interface Props {
  // Текущее значение порога (дней без активности) из User.ArchiveAfterDays.
  // null — правило выключено. Источник истины — сервер, фронт лишь
  // оптимистично отражает ввод.
  initialDays: number | null;
  // Был ли уже первый проход: если нет, кнопка «Применить сейчас» сразу после
  // сохранения порога — иначе только как повторный запуск.
  hasFirstRun: boolean;
  // Чьи чаты считаются превью: null = чаты вне проекта (личная сфера),
  // id проекта — чаты этого проекта (принадлежность проверяет бэкенд).
  projectId?: string | null;
}

// Дефолт для порога при первом включении: 30 дней. Подсказка под полем даёт
// подсказки вокруг этого порядка, а шкала — линейный набор 7/14/30/60/90
// (узкий набор: меньше — больше шума, больше — теряется смысл «не забыть»).
const DEFAULT_DAYS = 30;
const DAY_OPTIONS: number[] = [7, 14, 30, 60, 90];

export function ArchiveSettings({ initialDays, hasFirstRun, projectId = null }: Props) {
  // Локальный черновик порога: хранится в UI, в стор пишем по «Сохранить» (или
  // сейчас, если перешли с невалидного значения). При первом включении правила
  // стартуем с дефолтом — пока человек не настроил, пусть видит рабочее значение.
  const [days, setDays] = useState<number>(initialDays ?? DEFAULT_DAYS);
  const [previewCount, setPreviewCount] = useState<number | null>(null);
  // Стартовое состояние как у эффекта: при включённом правиле с первого кадра
  // «Считаю превью…», а не строка с невычисленным счётчиком
  const [previewLoading, setPreviewLoading] = useState<boolean>(initialDays !== null);
  // Превью не посчиталось (сеть/сервер) — показываем честную ошибку, а не «0 чатов»
  const [previewError, setPreviewError] = useState(false);
  const [saving, setSaving] = useState(false);
  const [running, setRunning] = useState(false);
  // Включено ли правило сейчас: false — поле и кнопки погашены. Отдельно от
  // days: пользователь мог сбросить days=null, и мы это отразим здесь.
  const [enabled, setEnabled] = useState<boolean>(initialDays !== null);

  useEffect(() => {
    setDays(initialDays ?? DEFAULT_DAYS);
    setEnabled(initialDays !== null);
  }, [initialDays]);

  // Счётчик превью пересчитываем при изменении days или скоупа, но НЕ чаще
  // раза в тик ввода (debounce): счётчик дёргает бэкенд, и печатать по
  // символу — лишняя нагрузка. 300мс хватает, чтобы человек успел доехать
  // до конца значения и не дёргать лишний раз.
  useEffect(() => {
    if (!enabled) {
      setPreviewCount(null);
      return;
    }
    let cancelled = false;
    setPreviewLoading(true);
    setPreviewError(false);
    const t = setTimeout(() => {
      archiveRuleApi.preview(days, projectId)
        .then(r => { if (!cancelled) setPreviewCount(r.count); })
        .catch(() => { if (!cancelled) { setPreviewCount(null); setPreviewError(true); } })
        .finally(() => { if (!cancelled) setPreviewLoading(false); });
    }, 300);
    return () => { cancelled = true; clearTimeout(t); };
  }, [days, enabled, projectId]);

  // Сохранить порог правила. enabled=true — шлём days как есть; enabled=false —
  // сброс (days=null в User.ArchiveAfterDays). Ответом приходит текущее
  // серверное значение, которое кладём в local-черновик, чтобы UI не отстал.
  const save = async (nextEnabled: boolean) => {
    setSaving(true);
    try {
      const r = await archiveRuleApi.setDays(nextEnabled ? days : null);
      setEnabled(r.archiveAfterDays !== null);
      if (r.archiveAfterDays !== null) setDays(r.archiveAfterDays);
      showToast('Автоправило архива', nextEnabled ? 'Порог сохранён' : 'Правило выключено', 'info');
    } catch (e) {
      showToast('Автоправило архива', e instanceof Error ? e.message : 'Не удалось сохранить', 'info');
    } finally {
      setSaving(false);
    }
  };

  // Кнопка первого прохода: «Применить сейчас». Запускает один проход правила,
  // снимает гейт фонового тика (User.ArchiveRuleFirstRunAt). Повторный клик —
  // ещё один проход, никакого «уже сделано»: правило фоновое, ручной прогон
  // пригодится после крупного завоза старых чатов.
  const runNow = async () => {
    setRunning(true);
    try {
      const r = await archiveRuleApi.runNow();
      // Число «0» тоже успех — правило пробежало, под отбор ничего не попало.
      const noun = r.archived === 0
        ? 'Под правило ничего не попало'
        : `В архив ушло чатов: ${r.archived}`;
      showToast('Автоправило архива', noun, 'info');
    } catch (e) {
      showToast('Автоправило архива', e instanceof Error ? e.message : 'Не удалось применить', 'info');
    } finally {
      setRunning(false);
    }
  };

  return (
    <div style={{
      border: `1px solid ${C.border}`,
      borderRadius: R.lg,
      background: C.bgPanel,
      padding: '14px 16px 16px',
      display: 'flex', flexDirection: 'column', gap: 10,
    }}>
      {/* Шапка: иконка + заголовок поля (дословно из канона) + переключатель.
          Включение — оптимистичное, истина — сервер: при ошибке тост и возврат. */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <Archive size={18} strokeWidth={2} style={{ color: C.textSecondary, flexShrink: 0 }} />
        <label style={{
          fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600,
          color: C.textHeading, flex: 1, minWidth: 0,
        }}>
          Убирать в архив чаты без сообщений дольше…
        </label>
        <input
          type="checkbox"
          checked={enabled}
          disabled={saving}
          onChange={e => { void save(e.target.checked); }}
          // Стандартный чекбокс: кастомный контрол рисовать не нужно, реестр
          // ui-кита чекбоксов не даёт; нативный в этом контексте читается ясно
          style={{ width: 16, height: 16, accentColor: C.accent, flexShrink: 0, cursor: saving ? 'wait' : 'pointer' }}
        />
      </div>

      {/* Подпись под полем (дословно). Поясняет почему «0 чатов под правило» —
          не баг: закреплённые и с активными задачами намеренно остаются. */}
      <p style={{
        margin: 0, fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted,
        lineHeight: 1.4,
      }}>
        Закреплённые чаты и чаты с активными задачами остаются на месте
      </p>

      {/* Поле порога и кнопка сохранения. Скрыто, когда правило выключено:
          сервер всё равно не примет запрос с days без флага, а человек не
          должен видеть значение, которое никуда не пишется. */}
      {enabled && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <select
            value={days}
            disabled={saving}
            onChange={e => setDays(Number(e.target.value))}
            style={{
              fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary,
              padding: '6px 28px 6px 10px', borderRadius: R.md,
              border: `1px solid ${C.border}`, background: C.bgWhite,
              cursor: saving ? 'wait' : 'pointer', minWidth: 100,
            }}
          >
            {DAY_OPTIONS.map(d => (
              <option key={d} value={d}>{d} {pluralDays(d)}</option>
            ))}
          </select>
          <Button variant="secondary" size="sm" loading={saving} onClick={() => void save(true)}>
            Сохранить
          </Button>
        </div>
      )}

      {/* Счётчик превью + кнопка первого прохода. Кнопка появляется сразу
          после сохранения порога (или если правило уже было включено), и
          остаётся доступной как повторный запуск. Текст кнопки и подписи —
          дословно из docs/product/archive-chats.md. */}
      {enabled && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
          <span style={{
            fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted,
            flex: 1, minWidth: 0,
          }}>
            {previewLoading
              ? 'Считаю превью…'
              : previewError
                ? 'Не удалось посчитать, сколько чатов подпадёт'
                : `Под правило подпадёт ${previewCount ?? 0} ${pluralChats(previewCount ?? 0)}`}
          </span>
          <Button
            variant="primary"
            size="sm"
            loading={running}
            onClick={() => void runNow()}
            // Первый прогон — hasFirstRun=false, текст подсказывает «начать»;
            // после первого прохода остаётся как повторный запуск
            title={hasFirstRun
              ? 'Прогнать правило ещё раз прямо сейчас'
              : 'Запустить первый проход правила'}
          >
            Применить сейчас
          </Button>
        </div>
      )}
    </div>
  );
}

// Склонение «дней» / «день» / «дня» — узкий набор чисел до 365, формула та же,
// что у chatCountWord, но без миллионного края (порог всегда ≤ 365).
function pluralDays(n: number): string {
  const m100 = n % 100;
  if (m100 >= 11 && m100 <= 14) return 'дней';
  const m10 = n % 10;
  if (m10 === 1) return 'день';
  if (m10 >= 2 && m10 <= 4) return 'дня';
  return 'дней';
}

function pluralChats(n: number): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'чат';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'чата';
  return 'чатов';
}