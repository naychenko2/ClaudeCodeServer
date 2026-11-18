// К5c · «База знаний» предмета — контролируемый пикер датасета Dify (макет Д2, §3 «Знания предмета»).
// Трасса данных: значение — черновик родителя (К5d), пикер его не держит и не пишет: при монтировании
// GET /api/knowledge (knowledgeApi.knowledgeBases.list) → список в локальное состояние (единственное,
// что пикер держит) → выбор уходит родителю одним onChange. Запросов на запись нет.
//
// Фильтр списка — удобство, проверяет сервер (К1b, 400 dataset_not_attachable): из того, что бэк может
// вернуть в списке, attachable (KnowledgeAccess.IsTutorAttachable, KnowledgeAccess.cs:34-44) — ровно
// публичные и свои самостоятельные: в ответе это visibility==='public' и personal+deletable —
// свои заметки/проекты бэк отдаёт с deletable=false (KnowledgeBaseCatalogService.cs:95-101), чужие
// в список не попадают вовсе (тот же файл, :84-85). Строки type здесь — отображаемые подписи
// (features/knowledge/shared.tsx:32-40), границей им быть нельзя; держится visibility+deletable.
//
// Значение вне списка (привязанную базу удалили/переименовали/передали) — disabled-пункт «База
// недоступна» и плашка под полем: значение видно и остаётся выбранным, пока админ не сменит
// (тот же приём, что «Недоступно: …» в ChildRoutePicker). Отказ загрузки — Notice, «Без базы» доступен;
// привязанная база при этом — disabled-пункт с её id: поле не должно говорить «Без базы» там, где
// база есть, и отвязать её можно и без списка (ревью К5c). Список, пришедший после размонтирования,
// не применяется (флаг отмены в эффекте).

import { useEffect, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { SP } from '../../lib/design';
import { Field } from '../../components/ui/Field';
import { Select, type SelectOption } from '../../components/ui/Select';
import { Notice } from '../../components/ui/Notice';
import { knowledgeApi } from '../knowledge/api';
import type { KnowledgeBaseSummary } from '../knowledge/types';

interface Props {
  // DatasetId предмета (ChildSubject из профиля); null — «Без базы». Ключ состояния — только проп,
  // своего значения пикер не держит.
  value: string | null;
  onChange: (datasetId: string | null) => void;
  // Гасит поле на время сохранения (макет Д2 «Сохранение») — состояние родителя, пикер о нём не знает.
  disabled?: boolean;
}

const GROUP_OWN = 'Мои базы';
const GROUP_SHARED = 'Общие базы';

// Attachable из того, что может быть в ответе: см. заголовок.
const isAttachable = (kb: KnowledgeBaseSummary) => kb.visibility === 'public' || kb.deletable;

// Подпись пункта — имя и размер базы (макет Д2: «Название · N док.»); счётчик не склоняется.
const kbLabel = (kb: KnowledgeBaseSummary) => `${kb.title} · ${kb.documentCount} док.`;

export function ChildSubjectKnowledgePicker({ value, onChange, disabled }: Props) {
  // null — список ещё идёт; failed — список не пришёл (rejection или 5xx). configured=false —
  // раздел «Знания» выключен/не настроен: это не отказ, а состояние из ответа (api KnowledgeListResponse).
  const [resp, setResp] = useState<{ configured: boolean; items: KnowledgeBaseSummary[] } | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let alive = true;
    knowledgeApi.knowledgeBases.list().then(
      (r) => { if (alive) setResp({ configured: r.configured, items: r.items ?? [] }); },
      () => { if (alive) setFailed(true); },
    );
    return () => { alive = false; };
  }, []);

  const loading = !failed && resp === null;
  const off = !failed && resp !== null && !resp.configured;
  const own = resp ? resp.items.filter((kb) => isAttachable(kb) && kb.visibility === 'personal') : [];
  const shared = resp ? resp.items.filter((kb) => isAttachable(kb) && kb.visibility === 'public') : [];
  // Привязанное значение вне списка — только когда список известен и он не отказ и не выключен
  // (в этих состояниях «недоступна» сказать нечем: список не пришёл).
  const gone = !failed && resp !== null && resp.configured && value !== null
    && !own.some((kb) => kb.id === value) && !shared.some((kb) => kb.id === value);

  const options: SelectOption[] = [
    // Значение вне списка — первым disabled-пунктом со своим id: поле показывает привязанное,
    // выбрать его повторно нельзя, починить — можно (те же места, что в ChildRoutePicker).
    ...(gone ? [{ value: value as string, label: 'База недоступна', disabled: true }] : []),
    // Отказ загрузки при привязанной базе: списка нет, привязка — есть. Пункт с id: поле показывает
    // настоящее (placeholder «Без базы» лгал бы), «Без базы» остаётся selectable — отвязать можно и
    // без списка (находка ревью К5c, закрыта в К5d).
    ...(failed && value !== null ? [{ value, label: `База ${value}`, disabled: true }] : []),
    ...own.map((kb) => ({ value: kb.id, label: kbLabel(kb), group: GROUP_OWN })),
    ...shared.map((kb) => ({ value: kb.id, label: kbLabel(kb), group: GROUP_SHARED })),
  ];

  return (
    <div data-testid="child-knowledge-picker" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {failed && (
        <Notice tone="warning" icon={AlertTriangle} data-testid="knowledge-load-error">
          Список баз не пришёл — «Раздел «Знания»» не ответил. Выбрать «Без базы» можно и так.
        </Notice>
      )}
      <Field
        label="База знаний"
        hint={
          off
            ? 'Раздел «Знания» выключен или не настроен. Привязать базу сейчас нельзя.'
            : 'Здесь только твои самостоятельные базы и общие. Базы заметок и проектов привязать нельзя: через репетитора ребёнок увидел бы их содержимое.'
        }
      >
        {loading ? (
          // Список ещё идёт: выбирать пока не из чего, поле не трогается.
          <Select value="" onChange={() => {}} options={[{ value: '', label: 'Загрузка…' }]} disabled title="Загрузка…" />
        ) : off ? (
          // Макет Д2 «Знания выключены»: disabled-поле с единственным пунктом.
          <Select value="" onChange={() => {}} options={[{ value: '', label: 'Знания недоступны' }]} disabled title="Знания недоступны" />
        ) : (
          <Select
            // «Без базы» — первый пункт и реальное значение (null), а не placeholder-заглушка:
            // пустое значение здесь означает «без базы», а не «ничего не выбрано».
            value={value ?? ''}
            onChange={(v) => onChange(v === '' ? null : v)}
            options={options}
            placeholder="Без базы"
            disabled={disabled}
          />
        )}
      </Field>
      {gone && (
        <Notice tone="warning" icon={AlertTriangle} title="База недоступна" data-testid="knowledge-base-gone">
          Привязанную базу удалили, переименовали или передали другому владельцу. Репетитор по ней не ищет.
          Выбери другую базу или «Без базы».
        </Notice>
      )}
    </div>
  );
}
