// Настройка автоправила архивации чатов (план «Архив чатов» v4, шаг 6, флаг
// chat-auto-archive). ВЕСЬ блок рисуется только при включённом флаге — ручной
// архив, режим «Архивные» и сводка карточки работают и без тумблера.
// Сохранение порога И ЕСТЬ запуск: отдельной кнопки «Применить сейчас» нет —
// «Сохранить» пишет порог и тут же прогоняет правило по накопившимся чатам,
// иначе залежи так и лежат в списке (фоновый тик их не трогает).

import { useEffect, useState } from 'react';
import { Archive } from 'lucide-react';
import { archiveRuleApi } from '../api/chats';
import { showToast } from '../lib/toast';
import { C, FONT, R } from '../lib/design';
import { Button } from './ui';

interface Props {
  // Текущее значение порога (дней без активности): у проекта — его собственный
  // Project.ArchiveAfterDays (или унаследованный личный, пока своего нет), вне
  // проекта — User.ArchiveAfterDays. null — правило выключено. Источник истины —
  // сервер, фронт лишь оптимистично отражает ввод.
  initialDays: number | null;
  // Чей порог настраиваем и чьи чаты считаются превью: null = чаты вне проекта
  // (личная сфера), id проекта — чаты этого проекта (владение проверяет бэкенд).
  projectId?: string | null;
  // Проход правила завершился — хозяин блока (диалог проекта) закрывает себя.
  // Сам блок про диалог ничего не знает.
  onArchiveDone?: () => void;
}

// Дефолт для порога при первом включении: 30 дней. Подсказка под полем даёт
// подсказки вокруг этого порядка, а шкала — линейный набор 7/14/30/60/90
// (узкий набор: меньше — больше шума, больше — теряется смысл «не забыть»).
const DEFAULT_DAYS = 30;
const DAY_OPTIONS: number[] = [7, 14, 30, 60, 90];

export function ArchiveSettings({ initialDays, projectId = null, onArchiveDone }: Props) {
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
  // Идёт «Сохранить»: запись порога плюс сразу проход правила. Отдельно от
  // saving (тумблер) — у кнопки свой индикатор и свой текст состояния.
  const [applying, setApplying] = useState(false);
  // Включено ли правило сейчас: false — поле и кнопки погашены. Отдельно от
  // days: пользователь мог сбросить days=null, и мы это отразим здесь.
  const [enabled, setEnabled] = useState<boolean>(initialDays !== null);

  // Блок занят: идёт запись порога тумблером или «Сохранить» с проходом правила
  const busy = saving || applying;

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

  // Записать порог. В проекте пишем в Project.ArchiveAfterDays, вне проекта — в
  // личный User.ArchiveAfterDays: в диалоге проекта личный порог трогать нельзя,
  // иначе настройка одного проекта меняет правило всех остальных.
  const writeDays = (next: number | null) =>
    projectId
      ? archiveRuleApi.setProjectDays(projectId, next)
      : archiveRuleApi.setDays(next);

  // Тумблер правила: только запись порога (или сброс), без прохода — человек
  // включил блок, чтобы настроить, а не чтобы прямо сейчас всё убрать в архив.
  const toggle = async (nextEnabled: boolean) => {
    setSaving(true);
    try {
      const r = await writeDays(nextEnabled ? days : null);
      setEnabled(r.archiveAfterDays !== null);
      if (r.archiveAfterDays !== null) setDays(r.archiveAfterDays);
      showToast('Автоправило архива', nextEnabled ? 'Порог сохранён' : 'Правило выключено', 'info');
    } catch (e) {
      showToast('Автоправило архива', e instanceof Error ? e.message : 'Не удалось сохранить', 'info');
    } finally {
      setSaving(false);
    }
  };

  // «Сохранить» = согласие: пишем порог и тут же прогоняем правило по этой сфере.
  // Успех (в том числе «убрано 0») закрывает диалог, ошибка — оставляет открытым
  // и разблокирует кнопку, чтобы человек мог повторить.
  const saveAndRun = async () => {
    setApplying(true);
    try {
      const saved = await writeDays(days);
      setEnabled(saved.archiveAfterDays !== null);
      if (saved.archiveAfterDays !== null) setDays(saved.archiveAfterDays);
      const r = projectId
        ? await archiveRuleApi.runNowForProject(projectId)
        : await archiveRuleApi.runNow();
      showToast(
        'Автоправило архива',
        r.archived > 0
          ? `Убрано в архив ${r.archived} ${pluralChats(r.archived)}`
          : 'Под правило пока ничего не подпадает',
        'info',
      );
      onArchiveDone?.();
    } catch (e) {
      showToast('Автоправило архива', e instanceof Error ? e.message : 'Не удалось убрать чаты в архив', 'info');
    } finally {
      setApplying(false);
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
          disabled={busy}
          onChange={e => { void toggle(e.target.checked); }}
          // Стандартный чекбокс: кастомный контрол рисовать не нужно, реестр
          // ui-кита чекбоксов не даёт; нативный в этом контексте читается ясно
          style={{ width: 16, height: 16, accentColor: C.accent, flexShrink: 0, cursor: busy ? 'wait' : 'pointer' }}
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
            disabled={busy}
            onChange={e => setDays(Number(e.target.value))}
            style={{
              fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary,
              padding: '6px 28px 6px 10px', borderRadius: R.md,
              border: `1px solid ${C.border}`, background: C.bgWhite,
              cursor: busy ? 'wait' : 'pointer', minWidth: 100,
            }}
          >
            {DAY_OPTIONS.map(d => (
              <option key={d} value={d}>{d} {pluralDays(d)}</option>
            ))}
          </select>
          {/* «Сохранить» — главное действие блока: пишет порог и сразу убирает
              подпавшие чаты в архив. Пока идёт проход — блокировка с индикатором
              и текст состояния рядом, чтобы пауза не читалась как «ничего не
              произошло»: диалог закроется сам по завершении. */}
          <Button
            variant="primary"
            size="sm"
            loading={busy}
            disabled={busy}
            onClick={() => void saveAndRun()}
          >
            Сохранить
          </Button>
          {applying && (
            <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>
              Убираю чаты в архив…
            </span>
          )}
        </div>
      )}

      {/* Счётчик превью: сколько чатов этой сферы подпадёт под текущий порог.
          Отдельной кнопки прохода рядом нет — проход запускает «Сохранить». */}
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