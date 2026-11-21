// Т4c/Т6 «Предметов и репетиторов» (макет Д1, пачка 1 + пачка 2): контролируемый пикер
// «поставщик → модель» из списка сервера. Запросов не делает и своего состояния данных не держит:
// routes приходят пропом от вызывающего (их загрузка — Т4f); источник правды значения — черновик
// родителя (предмета), пикер меняет его только действием админа — один onChange на действие.
//
// Пачка 2 (Т6, разрез §3 «Что И2′ не обещает»): список поставщиков сгруппирован по kind —
// «Anthropic · записи пула с API-ключом», «Облачные поставщики», «Локальные модели»; внутри
// группы — порядок routes (порядок сервера). Для выбранного облачного/локального маршрута под
// полями — предупреждение Notice и галка согласия («Понимаю риск и разрешаю этот маршрут» →
// acknowledgeRouteRisk); для локального — пометка «бюджет не тратится»: его ограничивают окна
// и минуты, не деньги. Согласие привязано к providerKey: при смене поставщика пикер отдаёт
// acknowledgeRouteRisk: false для требующего согласия (галка сбрасывается, разрез §3).
// Согласия у пула нет никогда: маршрут профиля (помощника) валидируется без него
// (ChildProfileValidator.cs:39-41).
//
// Свободного ввода нет: значение, которого нет в routes (старое из профиля), показывается
// «недоступно: {значение}» disabled-пунктом — его видно, но нельзя выбрать повторно; пустое
// значение — placeholder, а не первый пункт списка (иначе поле врёт «выбрано», ничего не выбрано).
// Порядок моделей внутри поставщика — его models. Облачной модели без цены в списке нет:
// сервер отдаёт только пары, прошедшие ChildRoute.Evaluate (ChildRoutesController.cs:68-72).

import { AlertTriangle, Cpu } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { Field } from '../../components/ui/Field';
import { Select, type SelectOption } from '../../components/ui/Select';
import { Notice } from '../../components/ui/Notice';
import { Checkbox } from '../../components/ui/Checkbox';
import { Badge } from '../../components/ui/Badge';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import type { ChildRoute } from './types';

// Значение пикера — та же тройка, что поля профиля (ChildSettingsProfile.providerKey/model/
// acknowledgeRouteRisk). Последнее — состояние галки: true — согласие стоит (живое от сервера
// или только что поставленное админом); отсутствие или false — согласия нет. Едет ли оно в тело
// — решает родитель (Т6): только действие этой сессии, не прочитанное из ответа.
export interface ChildRouteValue {
  providerKey: string;
  model: string;
  acknowledgeRouteRisk?: boolean;
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

// Метки групп и их порядок — макет Д1, пикер (б); сервер ставит пул первым
// (ChildRoutesController.cs:27-28,64).
const GROUPS: { kind: ChildRoute['kind']; label: string }[] = [
  { kind: 'pool', label: 'Anthropic · записи пула с API-ключом' },
  { kind: 'cloud', label: 'Облачные поставщики' },
  { kind: 'local', label: 'Локальные модели' },
];
// Имя согласия — одно в галке и в тексте отказа route_risk_unacknowledged (REJECT_TEXT
// родителя): человека ведём к тому контролу, который он реально видит.
const ACK_LABEL = 'Понимаю риск и разрешаю этот маршрут';

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

  // Список поставщиков — группы в порядке GROUPS, внутри группы — порядок routes.
  const providerOptions: SelectOption[] = GROUPS.flatMap(({ kind, label }) =>
    routes.filter(r => r.kind === kind).map(r => ({ value: r.providerKey, label: r.label, group: label })));
  if (!provider && value.providerKey) {
    providerOptions.unshift({ value: value.providerKey, label: `Недоступно: ${value.providerKey}`, disabled: true });
  }

  // Список моделей — модели выбранного поставщика, порядок его models.
  const modelOptions: SelectOption[] = models.map(m => ({ value: m.id, label: m.label }));
  if (value.model && !models.some(m => m.id === value.model)) {
    modelOptions.unshift({ value: value.model, label: `Недоступно: ${value.model}`, disabled: true });
  }

  const acknowledged = value.acknowledgeRouteRisk === true;

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
                // его списка не касается. Согласие привязано к прежнему поставщику — для нового
                // требующего его отдаём false: галка пуста, согласие спрашивается заново (разрез §3).
                const next = routes.find(r => r.providerKey === pk);
                onChange({
                  providerKey: pk,
                  model: next?.models[0]?.id ?? '',
                  ...(next?.requiresAck ? { acknowledgeRouteRisk: false } : {}),
                });
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
                // Смена модели согласия не касается: оно привязано к providerKey, не к модели.
                onChange({ ...value, model: m });
              }}
              options={modelOptions}
              placeholder={value.model ? undefined : 'Выбери модель'}
              disabled={disabled}
            />
          </Field>
        </div>
      </div>

      {/* Предупреждение и согласие — только для выбранного маршрута, требующего их (макет Д1, пикер (б)). */}
      {provider && (
        provider.kind === 'pool' ? (
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>
            Anthropic по API-ключу. Расход считается по ценам модели и входит в бюджет ребёнка.
          </span>
        ) : (
          <>
            {provider.kind === 'cloud' ? (
              <Notice tone="warning" icon={AlertTriangle} title={`Переписка уходит поставщику ${provider.label}`}>
                Всё, что пишет ребёнок, обрабатывает {provider.label} на своих серверах, по своим правилам
                хранения данных, в том числе данных детей. Расход считается по ценам модели.
              </Notice>
            ) : (
              <>
                <Notice tone="warning" icon={AlertTriangle} title="Локальная модель слабее держит правила безопасности">
                  Правила детского режима до неё доедут, но она может решить задачу за ребёнка или не заметить
                  стоп-сигнал. Кнопка «Мне неприятно» работает всегда.
                </Notice>
                <span style={{ display: 'flex', gap: SP.sm, alignItems: 'center', flexWrap: 'wrap' }}>
                  <Badge tone="success" size="xs" icon={<Cpu size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden />}>
                    бюджет не тратится
                  </Badge>
                  <span style={{ fontSize: FS.sm, color: C.textMuted }}>
                    Ограничивают только окна и минуты в день.
                  </span>
                </span>
              </>
            )}
            <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
              <Checkbox
                checked={acknowledged}
                ariaLabel={ACK_LABEL}
                disabled={disabled}
                onChange={(v) => onChange({ ...value, acknowledgeRouteRisk: v })}
              />
              <span style={{ fontSize: FS.md, color: C.textPrimary }}>{ACK_LABEL}</span>
            </span>
          </>
        )
      )}
    </div>
  );
}
