import { useMemo, useState } from 'react';
import { AlertTriangle, Globe, Key, Shield, X } from 'lucide-react';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { Modal, Button, Checkbox, Badge } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import {
  isNetPermission, permissionLabel, NET_PERMISSION_WARNING,
} from './permissionLabels';

// === Данные диалога согласия (экран 2 макетов, ADR-031 §4) ===
// Значение секрета сервером НИКОГДА не возвращается — только признак «заполнен»
// и дата. Поэтому в данных нет value: поле ввода всегда пустое.

export interface PluginConsentSecret {
  name: string;
  // «Обязательный» (решение В2 макетов): не заданный блокирует кнопку «Установить».
  // Бэкенд-DTO его не отдаёт (только {name, set, updatedAt}), поэтому источник —
  // манифест; пока бэк не выводит флаг, фронт считает все секреты обязательными.
  required: boolean;
  // Значение введено на сервере (режим «изменить» — из DTO; «установить» — false)
  set?: boolean;
  // Когда введено: ISO-дата для бейджа «задан · {дата}»
  setAt?: string | null;
}

// Вклад плагина для чипов «Что добавится»
export interface PluginContribution {
  kind: 'panel' | 'command' | 'skill';
  label: string;
}

export interface PluginConsentData {
  id: string;
  name: string;
  version: string;
  // Отображаемый путь (папка) — режим «установить»
  path?: string;
  // Короткий sha256 — режим «установить»
  sha256?: string;
  // «Что добавится» (чипы) — режим «установить»; в «изменить» не показывается
  contributions: PluginContribution[];
  // Запрошенные права (потолок)
  requested: string[];
  // Секреты пакета
  secrets: PluginConsentSecret[];
  // Режим «изменить»: уже выданные права (отмечены по умолчанию)
  granted?: string[];
}

interface Props {
  data: PluginConsentData;
  // Режим: «установить» (при установке) или «изменить» (со строки установленного)
  mode: 'install' | 'modify';
  onClose: () => void;
  // Выдать права (подмножество requested) + подтверждённые значения секретов
  onSubmit: (permissions: string[], secrets: { name: string; value: string }[]) => Promise<void>;
  // Сохранить один секрет (PUT /secrets/{name}); возвращает updatedAt.
  // Родитель реализует через api.plugins.setSecret; не передан — «Сохранить» лишь
  // подтверждает значение локально (оно уедет с grant).
  onSaveSecret?: (name: string, value: string) => Promise<string>;
  // Права, появившиеся при обновлении (accept вернул consentRequired): выделяем как «новые»
  // (макет 4, карточка черновика) — человек видит, что именно добавилось.
  newPermissions?: string[];
}

// === Чистые хелперы (тестируются в ConsentDialog.test.ts) ===

// Умолчание галок (решение В1 макетов): все права отмечены, кроме net:* (сняты).
// Режим «изменить» — по уже выданным (granted).
export function defaultCheckedState(
  requested: string[],
  mode: 'install' | 'modify',
  granted?: string[],
): Record<string, boolean> {
  const init: Record<string, boolean> = {};
  for (const p of requested) {
    if (mode === 'modify') init[p] = (granted ?? []).includes(p);
    else init[p] = !isNetPermission(p);
  }
  return init;
}

// Решение В2: первый обязательный секрет, который ещё не «задовлен». null — ничего не
// блокирует. «Задовлен» = задан на сервере (set), только что сохранён в этой сессии
// (justSaved) или (в режиме «установить») есть подтверждённое значение (values) — оно
// уедет с grant.
export function findBlockingSecret(
  secrets: PluginConsentSecret[],
  values: Record<string, string>,
  justSaved: Record<string, string>,
  mode: 'install' | 'modify',
): PluginConsentSecret | null {
  for (const s of secrets) {
    if (!s.required) continue;
    const satisfied = !!s.set
      || justSaved[s.name] !== undefined
      || (mode === 'install' && (values[s.name] ?? '').trim().length > 0);
    if (!satisfied) return s;
  }
  return null;
}

// Подпись основной кнопки по числу отмеченных прав (макет, экран 2).
export function submitLabelFor(mode: 'install' | 'modify', checkedCount: number, totalCount: number): string {
  if (mode === 'modify') return 'Сохранить';
  if (checkedCount === 0) return 'Установить без прав';
  if (checkedCount === totalCount) return 'Установить';
  return `Установить · прав ${checkedCount} из ${totalCount}`;
}

// Остаточный риск (ADR-031 §3.6): WebRTC/ICE и dns-prefetch CSP не закрывает ни в
// одном движке, поэтому выдача права на чтение данных (notes:read/tasks:read) — это
// доверие пакету и в части утечки. По-человечески, без жаргона.
export const RESIDUAL_RISK_NOTE =
  'Панель плагина работает в изолированной песочнице и не может обращаться к интернету. ' +
  'Но полностью исключить, что она незаметно передаст небольшую часть выданных ей данных наружу, ' +
  'браузер не позволяет. Выдавайте доступ к заметкам и задачам только плагинам, которым доверяете.';

// Показываем подпись, когда запрошено хотя бы одно из прав на чтение данных
// (notes:read / tasks:read) — именно они несут остаточный риск утечки.
export function showsResidualRiskNote(requested: string[]): boolean {
  return requested.some(p => p === 'notes:read' || p === 'tasks:read');
}

// Инициалы для плитки: первые буквы слов имени (до 2)
function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

// Плитка 36×36 с инициалами (макет, экран 2): accent-тон
function Tile({ name }: { name: string }) {
  return (
    <div style={{
      width: 36, height: 36, borderRadius: R.lg, flexShrink: 0,
      background: C.accentLight, color: C.accent,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      fontWeight: 700, fontSize: 13,
    }}>
      {initialsOf(name)}
    </div>
  );
}

// Короткая дата «12.10» для бейджа «задан · {дата}»
function shortDate(iso: string | null | undefined): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
}

// === Диалог согласия: права с чекбоксами, секреты «задан/не задан»,
// === подпись основной кнопки по числу выданных прав.
export function ConsentDialog({ data, mode, onClose, onSubmit, onSaveSecret, newPermissions }: Props) {
  const isMobile = useIsMobile();

  // Умолчание галок (В1) / в «изменить» — по granted
  const [checked, setChecked] = useState<Record<string, boolean>>(
    () => defaultCheckedState(data.requested, mode, data.granted),
  );
  // Черновик поля ввода: что человек печатает прямо сейчас. Чистится по «Сохранить»/крестику,
  // поэтому при повторном открытии строки поле всегда пустое.
  const [draft, setDraft] = useState<Record<string, string>>({});
  // Подтверждённые значения (по «Сохранить»): уезжают на сервер с grant
  const [values, setValues] = useState<Record<string, string>>({});
  // name → updatedAt после успешного PUT в этой сессии (бейдж «задан · {дата}»)
  const [justSaved, setJustSaved] = useState<Record<string, string>>({});
  // Какой секрет развёрнут в поле ввода
  const [expandedSecret, setExpandedSecret] = useState<string | null>(null);
  // Какой секрет сейчас сохраняется (PUT в полёте)
  const [savingSecret, setSavingSecret] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const checkedCount = useMemo(
    () => data.requested.filter(p => checked[p]).length,
    [data.requested, checked],
  );
  const totalCount = data.requested.length;

  // V2: обязательный секрет, не задовленный, блокирует «Установить» (не «Сохранить»).
  const blockingSecret = useMemo(
    () => findBlockingSecret(data.secrets, values, justSaved, mode),
    [data.secrets, values, justSaved, mode],
  );

  const submitLabel = submitLabelFor(mode, checkedCount, totalCount);

  // «Задан» для бейджа: на сервере, сохранён в этой сессии или (установить) подтверждён.
  const isSet = (s: PluginConsentSecret): boolean =>
    !!s.set || justSaved[s.name] !== undefined
    || (mode === 'install' && (values[s.name] ?? '').trim().length > 0);

  // Дата бейджа: из этой сессии (PUT) или из DTO
  const setAtOf = (s: PluginConsentSecret): string | null =>
    shortDate(justSaved[s.name] ?? s.setAt ?? null);

  // «Сохранить» в строке секрета: подтвердить значение (уедет с grant) + PUT (плагин в
  // обоих режимах уже установлен). Значение из поля уходит из состояния — поле пустеет.
  const saveSecret = async (s: PluginConsentSecret) => {
    const v = (draft[s.name] ?? '').trim();
    if (!v) return;
    setSavingSecret(s.name);
    setError(null);
    try {
      setValues(prev => ({ ...prev, [s.name]: v }));
      if (onSaveSecret) {
        const updatedAt = await onSaveSecret(s.name, v);
        setJustSaved(prev => ({ ...prev, [s.name]: updatedAt }));
      }
      setDraft(prev => {
        const n = { ...prev };
        delete n[s.name];
        return n;
      });
      setExpandedSecret(null);
    } catch (e: unknown) {
      // Сервисные 400/403/409 — текстом сервера; значение секрета в нём не бывает
      setError(e instanceof Error ? e.message : 'Не удалось сохранить секрет');
    } finally {
      setSavingSecret(null);
    }
  };

  // Крестик: свернуть строку и выбросить черновик (значение не подтверждено)
  const collapseSecret = (name: string) => {
    setDraft(prev => {
      const n = { ...prev };
      delete n[name];
      return n;
    });
    setExpandedSecret(null);
  };

  const submit = async () => {
    setLoading(true);
    setError(null);
    try {
      const permissions = data.requested.filter(p => checked[p]);
      const secrets = data.secrets
        .filter(s => (values[s.name] ?? '').trim().length > 0)
        .map(s => ({ name: s.name, value: values[s.name].trim() }));
      await onSubmit(permissions, secrets);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Не получилось');
    } finally {
      setLoading(false);
    }
  };

  // Моно-строка шапки: {version} · {path} · sha256 {short}
  const headMeta = [
    data.version,
    data.path,
    data.sha256 ? `sha256 ${data.sha256}` : null,
  ].filter(Boolean).join('  ·  ');

  return (
    <Modal
      width={MODAL_W.consent}
      title={
        mode === 'install'
          ? `Установить «${data.name}»?`
          : `Права «${data.name}»`
      }
      onClose={onClose}
      closeOnBackdrop={!loading}
      footer={
        <div style={{ display: 'flex', gap: SP.sm, justifyContent: 'flex-end', width: '100%', flexDirection: isMobile ? 'column' : 'row' }}>
          <Button
            variant="ghost"
            size="md"
            fullWidth={isMobile}
            onClick={onClose}
            disabled={loading}
          >
            Отмена
          </Button>
          <Button
            variant="primary"
            size="md"
            fullWidth={isMobile}
            loading={loading}
            disabled={mode === 'install' && !!blockingSecret}
            title={mode === 'install' && blockingSecret
              ? `Сначала задайте обязательный секрет ${blockingSecret.name}`
              : undefined}
            onClick={() => void submit()}
          >
            {submitLabel}
          </Button>
        </div>
      }
    >
      {/* Шапка: плитка + (mono-строка) */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.md }}>
        <Tile name={data.name} />
        <div style={{ minWidth: 0, flex: 1 }}>
          {headMeta && (
            <div style={{
              fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {headMeta}
            </div>
          )}
        </div>
      </div>

      {/* «Что добавится» — только режим «установить» */}
      {mode === 'install' && data.contributions.length > 0 && (
        <div>
          <SectionLabel>Что добавится</SectionLabel>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
            {data.contributions.map((c, i) => (
              <Chip key={i}>{c.label}</Chip>
            ))}
          </div>
        </div>
      )}

      {/* Права, которые просит плагин */}
      {data.requested.length > 0 && (
        <div>
          <SectionLabel>Права, которые просит плагин</SectionLabel>
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {data.requested.map(key => {
              const label = permissionLabel(key);
              if (label === null) return null; // secret:* — в блоке секретов
              const net = isNetPermission(key);
              const on = !!checked[key];
              return (
                <div
                  key={key}
                  onClick={() => setChecked(c => ({ ...c, [key]: !c[key] }))}
                  style={{
                    display: 'flex', alignItems: 'flex-start', gap: SP.md,
                    padding: '10px 0', cursor: 'pointer',
                    borderTop: '1px solid ' + C.borderLight,
                  }}
                >
                  <Checkbox checked={on} onChange={() => {}} ariaLabel={label} />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
                      {net && (
                        <Globe size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.warning, flexShrink: 0 }} />
                      )}
                      <span style={{ fontWeight: 600, color: C.textHeading, fontSize: FS.base }}>{label}</span>
                      {newPermissions?.includes(key) && (
                        <Badge tone="accent" size="xs" title="Право появилось при обновлении">new</Badge>
                      )}
                    </div>
                    <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>{key}</div>
                    {net && (
                      <div style={{ fontSize: FS.sm, color: C.warningText, marginTop: 2 }}>{NET_PERMISSION_WARNING}</div>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
          {/* Остаточный риск: только когда запрошено чтение данных (notes:read/tasks:read) */}
          {showsResidualRiskNote(data.requested) && (
            <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5, marginTop: SP.sm }}>
              {RESIDUAL_RISK_NOTE}
            </div>
          )}
        </div>
      )}

      {/* Секреты */}
      {data.secrets.length > 0 && (
        <div>
          <SectionLabel>Секреты</SectionLabel>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            {data.secrets.map(s => {
              const expanded = expandedSecret === s.name;
              const set = isSet(s);
              const date = setAtOf(s);
              const saving = savingSecret === s.name;
              return (
                <div key={s.name} style={{
                  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
                }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: '8px 12px' }}>
                    <Key size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textSecondary, flexShrink: 0 }} />
                    <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textPrimary, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {s.name}
                    </span>
                    {set
                      ? <Badge tone="success" dot>задан{date ? ` · ${date}` : ''}</Badge>
                      : <Badge tone="warning" dot>не задан</Badge>}
                    <Button
                      variant="ghost" size="xs"
                      disabled={saving}
                      onClick={() => setExpandedSecret(expanded ? null : s.name)}
                    >
                      {set ? 'Заменить' : 'Задать'}
                    </Button>
                  </div>
                  {expanded && (
                    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: '0 12px 10px' }}>
                      <input
                        type="password"
                        value={draft[s.name] ?? ''}
                        onChange={e => setDraft(v => ({ ...v, [s.name]: e.target.value }))}
                        placeholder="••••••••••••••••"
                        aria-label={`Значение секрета ${s.name}`}
                        style={{
                          flex: 1, minWidth: 0, height: 34, padding: '0 12px',
                          borderRadius: R.xl, border: `1px solid ${C.border}`,
                          background: C.bgWhite, color: C.textPrimary,
                          fontFamily: FONT.mono, fontSize: FS.sm, outline: 'none',
                        }}
                      />
                      <Button
                        variant="primary" size="sm"
                        loading={saving}
                        disabled={(draft[s.name] ?? '').trim().length === 0}
                        onClick={() => void saveSecret(s)}
                      >
                        Сохранить
                      </Button>
                      <button
                        onClick={() => collapseSecret(s.name)}
                        style={{
                          width: 28, height: 28, borderRadius: R.md, border: 'none', background: 'transparent',
                          color: C.textMuted, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
                        }}
                        aria-label="Скрыть"
                      >
                        <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </button>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

      {/* Info-плашка со щитом */}
      <div style={{
        display: 'flex', alignItems: 'flex-start', gap: SP.sm,
        padding: SP.md, borderRadius: R.lg,
        background: C.infoBg, border: `1px solid ${C.info}`,
      }}>
        <Shield size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.info, flexShrink: 0, marginTop: 1 }} />
        <div style={{ fontSize: FS.sm, color: C.textPrimary, lineHeight: 1.5 }}>
          Агент не сможет вызывать плагин без выданных прав. Права и секреты можно поменять позже:
          «Настройки проекта → Плагины».
        </div>
      </div>

      {/* Ошибка (валидация/установка): текст сервера, значения секретов в нём не бывает */}
      {error && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: SP.sm,
          padding: SP.md, borderRadius: R.lg,
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
        }}>
          <AlertTriangle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.dangerText, flexShrink: 0, marginTop: 1 }} />
          <div style={{ fontSize: FS.sm, color: C.dangerText, lineHeight: 1.5 }}>{error}</div>
        </div>
      )}
    </Modal>
  );
}

// Подпись секции: FS.xs, uppercase, C.textMuted
function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, marginBottom: SP.sm,
    }}>
      {children}
    </div>
  );
}

// Чип вклада (макет, экран 2): спокойный, без акцента
function Chip({ children }: { children: React.ReactNode }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4,
      padding: '3px 8px', borderRadius: R.sm, fontSize: FS.sm,
      background: C.bgPanel, color: C.textSecondary, border: `1px solid ${C.borderLight}`,
    }}>
      {children}
    </span>
  );
}
