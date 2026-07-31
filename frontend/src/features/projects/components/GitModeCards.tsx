import { C, FONT, R } from '../../../lib/design';
import { Toggle } from '../../../components/ui';

export type GitMode = 'none' | 'manual' | 'auto';

// Общий список режимов ведения истории — формулировки согласованы, не менять.
// Подсказка ушла в title (hover) — так карточка умещается в одну строку
// вместо двух (было главным вкладом в вертикальный скролл диалогов).
export const GIT_MODES: { value: GitMode; label: string; hint: string }[] = [
  { value: 'none', label: 'Без ведения истории', hint: 'Обычная папка — версии файлов не сохраняются' },
  { value: 'manual', label: 'Ручное ведение истории', hint: 'Версии сохраняются, когда вы сами нажмёте «Зафиксировать» в разделе «Файлы». Рекомендуется для разработки кода' },
  { value: 'auto', label: 'Автоматическое ведение истории', hint: 'Каждый ход ИИ сохраняется в историю сам. Рекомендуется для работы с документами' },
];

// Высота карточки режима: паддинги 7+7, строка 13px с рамкой в 1px. Экспортируется
// ради скелета в EditDialog — тот держит место под ещё не приехавший git-статус, и
// считать высоту второй раз значило бы развести её с настоящей карточкой.
export const GIT_CARD_H = 33;
// Высота тела блока «История файлов» по САМОМУ ВЫСОКОМУ состоянию — три карточки с
// зазорами. Им же держится место, пока едет git-статус, и к нему подтягивается
// вариант репозитория (бейдж и две карточки), который на пару пикселей ниже.
export const GIT_BODY_H = GIT_CARD_H * 3 + 5 * 2;

// Компактная однострочная радио-карточка режима истории (EditDialog + AddProjectDialog).
export function GitModeCard({ active, label, hint, disabled, onClick }: {
  active: boolean; label: string; hint: string; disabled?: boolean; onClick: () => void;
}) {
  return (
    <div
      onClick={disabled ? undefined : onClick}
      title={hint}
      style={{
        display: 'flex', alignItems: 'center', gap: 8, padding: '7px 10px',
        cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.6 : 1,
        borderRadius: R.lg, border: `1px solid ${active ? C.accent : C.border}`,
        background: active ? C.accentLight : C.bgWhite,
      }}
    >
      <span style={{
        width: 13, height: 13, borderRadius: '50%', flexShrink: 0,
        border: `1.5px solid ${active ? C.accent : C.dashed}`,
        background: active ? C.accent : 'transparent',
        boxShadow: active ? `inset 0 0 0 2.5px ${C.bgWhite}` : 'none',
      }} />
      <span style={{
        fontSize: 13, fontFamily: FONT.sans, fontWeight: active ? 600 : 500,
        color: active ? C.textHeading : C.textPrimary,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {label}
      </span>
    </div>
  );
}

// Строка «отправлять на git-сервер (push)» — общая для создания и настроек проекта.
export function GitPushRow({ checked, onChange, disabled, disabledTitle }: {
  checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; disabledTitle?: string;
}) {
  return (
    <div
      onClick={disabled ? undefined : () => onChange(!checked)}
      title={disabled ? disabledTitle : undefined}
      style={{
        display: 'flex', alignItems: 'center', gap: 8, padding: '1px 10px 0 31px',
        cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.5 : 1,
      }}
    >
      <Toggle checked={checked} onChange={() => { if (!disabled) onChange(!checked); }} />
      <span style={{ fontSize: 12, fontFamily: FONT.sans, color: C.textPrimary }}>
        Ещё и отправлять копию на git-сервер (push)
      </span>
    </div>
  );
}
