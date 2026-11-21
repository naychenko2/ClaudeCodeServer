// Ф2b: каркас окна «Настройки детского режима» (ADR-041 ред. 7 решение 74; макет
// docs/mockups/kids-settings-window.html — вариант A «вкладки», MODAL_W.wide).
// Разделы: «Обзор» (./ChildSettingsOverview) и три слота-пропса — их наполняют Ф2c–Ф2e.
// Загрузка — childApi.getChildProfile, запись — childApi.putChildProfile (единственная
// ручка записи профиля, решение 12/30). Отказ любой из двух — текст body.message в теле
// окна: окно НЕ закрывается, черновик остаётся (серверный текст как есть, своей
// формулировки по кодам нет).
// Отказ сохранения несёт и body.code: он кладётся в черновик (saveError) — разделы различают
// им свой отказ, «Предметы» обводят строку с опознанным id (ревью R-Ф2b).
//
// Конкурентность (постановка шага): ответ любой ручки применяется только своему
// поколению запроса (genRef): ответ прежнего ребёнка, прежней попытки или ответ, пришедший
// после закрытия окна, в состояние не входит
// (сверки по id недостаточно: A→B→A даёт совпадающий ключ).
//
// Имена (ребёнка и опекунов) даёт вызывающий: у него список пользователей уже есть
// (components/UserManagementModal.tsx:206); вторая копия того же запроса внутри окна была
// бы второй точкой правды и вторым отказом. Без имени строка показывает id.
//
// Слоты разделов — функция от живого черновика окна, а не готовый узел: черновик и
// «Сохранить» принадлежат каркасу, раздел их только редактирует (тот же контракт, что у
// ChildTimeSection Ф2d: values + onChange). Вызывающий пишет слот так:
//   timeSection={(d) => <ChildTimeSection values={…из d.profile…} onChange={(v) => d.patch({ … })} />}

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import { Modal } from '../../components/ui/Modal';
import { Tabs } from '../../components/ui/Tabs';
import { Button } from '../../components/ui/Button';
import { Notice } from '../../components/ui/Notice';
import { Badge } from '../../components/ui/Badge';
import { useIsMobileModal } from '../../components/ui/useIsMobileModal';
import { C, FS, MODAL_W, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { childApi } from './api';
import type { ChildSettingsProfile } from './types';
import { ChildSettingsOverview } from './ChildSettingsOverview';

// Порядок и подписи разделов — по макету (SECS, строки 554–556): сначала видно, как сейчас
// устроено, потом правится.
export type ChildSettingsSection = 'overview' | 'subjects' | 'time' | 'safety';

const SECTIONS: { key: ChildSettingsSection; label: string }[] = [
  { key: 'overview', label: 'Обзор' },
  { key: 'subjects', label: 'Предметы' },
  { key: 'time', label: 'Время и лимиты' },
  { key: 'safety', label: 'Безопасность и согласие' },
];

// Черновик окна: ответ GET, поверх которого правят разделы. Им же говорит «Сохранить».
export interface ChildSettingsDraftState {
  profile: ChildSettingsProfile;
  // Есть несохранённое: ставит только patch (сверка строк здесь была бы второй правдой).
  dirty: boolean;
  saving: boolean;
  patch: (p: Partial<ChildSettingsProfile>) => void;
  // Отказ последнего «Сохранить» (ревью R-Ф2b): code — для действия интерфейса, message — тот же
  // текст в теле окна. subjectId — предмет, опознанный отказом subject_* (subjectIdOfReject ниже):
  // раздел «Предметы» обводит им строку. Нет отказа — null.
  saveError?: { code: string; message: string; subjectId?: string } | null;
}

export type ChildSettingsSlot = (draft: ChildSettingsDraftState) => ReactNode;

interface Props {
  userId: string;
  // Имя ребёнка для шапки окна — его берёт вызывающий (строка списка, из которой открыли
  // окно). Без него шапка показывает id: окно остаётся читаемым, но не человеческим.
  userLabel?: string;
  // Имена опекунов по id — тот же список вызывающего.
  userNames?: Record<string, string>;
  onClose: () => void;
  // Разделы 2–4: их наполняют Ф2c–Ф2e. Пустой слот — заглушка, окно остаётся рабочим.
  subjectsSection?: ChildSettingsSlot;
  timeSection?: ChildSettingsSlot;
  safetySection?: ChildSettingsSlot;
}

// Текст отказа ручки: серверный message как есть (в нём уже названа причина — «нет такого
// пользователя», «id опекуна не найден»). Своего текста по кодам нет: их формулировки
// держит сервер (ChildProfileValidator).
function rejectText(e: unknown): string {
  const body = (e as { body?: { message?: string } } | null)?.body;
  const m = typeof body?.message === 'string' ? body.message.trim() : '';
  return m || 'Не получилось. Попробуй ещё раз.';
}

// Код отказа (error.body.code) — он же в saveError черновика: интерфейс различает отказы
// не текстом (ревью R-Ф2b: без кода каркас их глотал).
function rejectCode(e: unknown): string {
  const c = (e as { body?: { code?: string } } | null)?.body?.code;
  return typeof c === 'string' ? c : '';
}

// Предмет, опознанный отказом subject_*: сервер пишет его id в message в «уголках»
// («Название предмета «math» — от 1 до 40 символов.», ChildProfileValidator.ValidateSubjects);
// структурированного поля id в теле отказа нет. Только коды subject_*: в прочих текстах
// кавычки держат не id предмета (ключ провайдера, модель), и обводить ими нечего.
function subjectIdOfReject(code: string, message: string): string | undefined {
  if (!code.startsWith('subject_')) return undefined;
  return message.match(/«([^»]+)»/)?.[1];
}

export function ChildSettingsWindow({
  userId, userLabel, userNames, onClose, subjectsSection, timeSection, safetySection,
}: Props) {
  const isMobile = useIsMobileModal();
  const [section, setSection] = useState<ChildSettingsSection>('overview');
  const [profile, setProfile] = useState<ChildSettingsProfile | null>(null);
  // Ответ чтения пришёл (профиль может быть и null — «профиля нет»). Без этого «Загружаю…»
  // оставалось бы и при пустом профиле: интерфейс врал бы о состоянии, которое уже известно.
  const [loaded, setLoaded] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [loadFailed, setLoadFailed] = useState<string | null>(null);
  // Отказ сохранения — целый {code, message, subjectId}: текст в тело окна, код и id предмета —
  // в черновик (ревью R-Ф2b: каркас глотал body.code, раздел «Предметы» ждал errorId).
  const [saveError, setSaveError] = useState<{ code: string; message: string; subjectId?: string } | null>(null);
  const [saving, setSaving] = useState(false);
  const [reloadNonce, setReloadNonce] = useState(0);

  // Поколение запросов окна: инкремент на каждое чтение (первое, повтор, смена ребёнка —
  // она даёт новый load через новый userId). Ответ чтения и записи применяется, только
  // если его поколение текущее.
  const genRef = useRef(0);
  const savingRef = useRef(false);

  const load = useCallback(() => {
    const gen = ++genRef.current;
    childApi.getChildProfile(userId).then(
      (r) => {
        if (genRef.current !== gen) return; // устаревший ответ: пришёл после смены или повтора
        setProfile(r.profile);
        setLoaded(true);
        setDirty(false);
        setSaveError(null);
      },
      (e) => { if (genRef.current === gen) setLoadFailed(rejectText(e)); },
    );
  }, [userId]);

  // Первое открытие и смена ребёнка: черновик и ошибки прежнего снимаются целиком, читается
  // новый (состояние прежнего ребёнка не переживает смену).
  useEffect(() => {
    setProfile(null);
    setLoaded(false);
    setDirty(false);
    setLoadFailed(null);
    setSaveError(null);
    savingRef.current = false;
    setSaving(false);
    load();
  }, [userId, load, reloadNonce]);

  const patch = useCallback((p: Partial<ChildSettingsProfile>) => {
    setProfile((prev) => (prev ? { ...prev, ...p } : prev));
    setDirty(true);
  }, []);

  const save = useCallback(() => {
    if (!profile || savingRef.current) return; // повтор во время запроса игнорируется
    const gen = genRef.current;
    savingRef.current = true;
    setSaving(true);
    setSaveError(null);
    childApi.putChildProfile(userId, profile).then(
      (r) => {
        // Ответ прежнего ребёнка или прежней загрузки: черновик текущего не заменяем.
        // Успех снятия — черновик равен сохранённому: отметка «несохранённое» снимается.
        if (genRef.current !== gen) return;
        setProfile(r.profile ?? profile);
        setDirty(false);
      },
      // Отказ — целый: текст в тело окна, код и опознанный id предмета — в черновик разделам.
      (e) => {
        if (genRef.current !== gen) return; // окно остаётся открытым; отказ прежнего поколения не показываем
        const code = rejectCode(e);
        const message = rejectText(e);
        setSaveError({ code, message, subjectId: subjectIdOfReject(code, message) });
      },
    ).finally(() => {
      savingRef.current = false;
      setSaving(false);
    });
  }, [userId, profile]);

  const subtitle = (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{userLabel || userId}</span>
      {profile && (
        <Badge tone={profile.enabled ? 'success' : 'neutral'} size="xs">
          {profile.enabled ? 'детский режим включён' : 'детский режим выключен'}
        </Badge>
      )}
    </div>
  );

  // Полоса разделов — в слоте header Modal'а: она не уезжает при прокрутке тела (тот же
  // смысл, что «tabs» вне mbody в макете). На мобиле — тот же ряд вкладок сверху: вариант A
  // макета на телефоне (четыре подписи влезают и на 360, полоса прокручивается).
  const tabs = (
    <Tabs
      value={section}
      items={SECTIONS.map((s) => ({ value: s.key, label: s.label }))}
      onChange={setSection}
      ariaLabel="Разделы настроек ребёнка"
      size="md"
      isMobile={isMobile}
      transparent
    />
  );

  const slot = (key: ChildSettingsSection, label: string, fn?: ChildSettingsSlot) => fn ? fn({ profile: profile!, dirty, saving, patch, saveError }) : (
    <p data-testid={`child-settings-slot-${key}`} style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5, margin: 0 }}>
      Раздел «{label}» наполнит следующий шаг.
    </p>
  );

  const body = loadFailed ? (
    <Notice
      data-testid="child-settings-load-error"
      tone="danger"
      icon={AlertTriangle}
      title="Настройки не загрузились"
      action={
        <Button variant="ghost" size="sm" dataTestId="child-settings-retry" leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => setReloadNonce((n) => n + 1)}>
          Повторить
        </Button>
      }
    >
      {loadFailed} Ничего не потеряно — попробуй ещё раз.
    </Notice>
  ) : !loaded ? (
    <p data-testid="child-settings-loading" style={{ fontSize: FS.sm, color: C.textMuted, margin: 0 }}>
      Загружаю настройки…
    </p>
  ) : !profile ? (
    // Ответ был, профиля нет: режим у этого пользователя не включён (GET отдаёт null, когда
    // профиля никогда не заводили). Окно не выдумывает настройки, которых нет.
    <Notice data-testid="child-settings-no-profile" tone="info" icon={AlertTriangle} title="Профиля настроек нет">
      Этот пользователь не в детском режиме. Включи его в «Управлении пользователями» — здесь появятся предметы, время и безопасность.
    </Notice>
  ) : (
    <div data-testid="child-settings-window" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {saveError && (
        <Notice data-testid="child-settings-save-error" tone="danger" icon={AlertTriangle} title="Не сохранено">
          {saveError.message}
        </Notice>
      )}
      {section === 'overview' && (
        <ChildSettingsOverview profile={profile} userNames={userNames} openSection={setSection} />
      )}
      {section === 'subjects' && slot('subjects', 'Предметы', subjectsSection)}
      {section === 'time' && slot('time', 'Время и лимиты', timeSection)}
      {section === 'safety' && slot('safety', 'Безопасность и согласие', safetySection)}
    </div>
  );

  const footer = (
    <>
      <span data-testid="child-settings-dirty" style={{ flex: 1, minWidth: 0, alignSelf: 'center', fontSize: FS.sm, color: C.textMuted }}>
        {dirty ? 'Есть несохранённые изменения' : 'Изменений нет'}
      </span>
      <Button
        variant="primary"
        size="md"
        dataTestId="child-settings-save"
        onClick={save}
        loading={saving}
        disabled={!profile || !dirty}
      >
        Сохранить
      </Button>
    </>
  );

  return (
    <Modal
      width={MODAL_W.wide}
      title="Настройки детского режима"
      subtitle={subtitle}
      onClose={onClose}
      header={tabs}
      footer={footer}
    >
      {body}
    </Modal>
  );
}
