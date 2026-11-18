// Т4c «Предметов и репетиторов» (макет Д1, пачка 1): контролируемый пикер «поставщик → модель»
// из списка сервера. Запросов не делает и своего состояния данных не держит: routes приходят
// пропом от вызывающего (их загрузка — Т4f); источник правды значения — черновик родителя
// (предмета), пикер меняет его только действием админа — один onChange на действие.
//
// Свободного ввода нет: значение, которого нет в routes (старое из профиля), показывается
// «недоступно: {значение}» disabled-пунктом — его видно, но нельзя выбрать повторно; пустое
// значение — placeholder, а не первый пункт списка (иначе поле врёт «выбрано», ничего не выбрано).
// Порядок списков — как пришёл с сервера (ChildRoutesController: записи пула, затем провайдеры);
// пикер не фильтрует и не группирует по kind — облачные/локальные группы, предупреждения и
// согласие (requiresAck) — Т6.
//
// Подпись «Только записи пула с API-ключом…» из макета (пачка 1) здесь не статика компонента:
// она о составе вызывающего, а не о пикере — её место в Т4f/Т6.

import { AlertTriangle } from 'lucide-react';
import { C, R, SP } from '../../lib/design';
import { Field } from '../../components/ui/Field';
import { Select, type SelectOption } from '../../components/ui/Select';
import { Notice } from '../../components/ui/Notice';
import { useIsMobile } from '../../lib/breakpoints';
import type { ChildRoute } from './types';

// Значение пикера — та же пара, что поля предмета (ChildSubjectAdmin.providerKey/model).
export interface ChildRouteValue {
  providerKey: string;
  model: string;
}

interface Props {
  // Элементы GET /api/admin/child-routes как есть: порядок — сервера, models непустой
  // (пустую пару сервер не отдаёт, ChildRoutesController.cs:51,73).
  routes: ChildRoute[];
  value: ChildRouteValue;
  onChange: (v: ChildRouteValue) => void;
  // Гасит поля на время сохранения (макет Д1 «Сохранение») — состояние родителя, пикер о нём не знает.
  disabled?: boolean;
}

export function ChildRoutePicker({ routes, value, onChange, disabled }: Props) {
  const isMobile = useIsMobile();

  // Пустой пул — не пустые списки, а причина: выбирать не из чего, и поле должно сказать это прямо.
  if (routes.length === 0) {
    return (
      <Notice tone="warning" icon={AlertTriangle} data-testid="child-route-picker">
        Нет записей пула с API-ключом
      </Notice>
    );
  }

  const provider = routes.find(r => r.providerKey === value.providerKey) ?? null;
  const models = provider?.models ?? [];

  // Список поставщиков — порядок routes; тексты — label от сервера (DisplayName или ключ).
  const providerOptions: SelectOption[] = routes.map(r => ({ value: r.providerKey, label: r.label }));
  if (!provider && value.providerKey) {
    providerOptions.unshift({ value: value.providerKey, label: `Недоступно: ${value.providerKey}`, disabled: true });
  }

  // Список моделей — модели выбранного поставщика, порядок его models.
  const modelOptions: SelectOption[] = models.map(m => ({ value: m.id, label: m.label }));
  if (value.model && !models.some(m => m.id === value.model)) {
    modelOptions.unshift({ value: value.model, label: `Недоступно: ${value.model}`, disabled: true });
  }

  return (
    <div
      data-testid="child-route-picker"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.sm,
        padding: SP.sm, borderRadius: R.xl, background: C.bgPanel,
      }}
    >
      {/* Два Select в ряд; на мобиле (360) — столбиком (макет Д1 .row2 / .m .row2) */}
      <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: isMobile ? SP.sm : SP.md }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <Field label="Поставщик">
            <Select
              value={value.providerKey}
              onChange={(pk) => {
                if (!pk) return; // placeholder — действия нет
                // Смена поставщика сбрасывает модель на первую из его списка: прежняя модель
                // его списка не касается.
                const next = routes.find(r => r.providerKey === pk);
                onChange({ providerKey: pk, model: next?.models[0]?.id ?? '' });
              }}
              options={providerOptions}
              placeholder={value.providerKey ? undefined : 'Выбери поставщика'}
              disabled={disabled}
            />
          </Field>
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <Field label="Модель">
            <Select
              value={value.model}
              onChange={(m) => {
                if (!m) return; // placeholder — действия нет
                onChange({ providerKey: value.providerKey, model: m });
              }}
              options={modelOptions}
              placeholder={value.model ? undefined : 'Выбери модель'}
              disabled={disabled}
            />
          </Field>
        </div>
      </div>
    </div>
  );
}
