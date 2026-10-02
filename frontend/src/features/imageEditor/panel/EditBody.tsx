// Тело «Настроек» в режиме «Править» (панель v5, флаг image-panel-v5): список «Операция» с
// группами «С ИИ» и «Без ИИ · бесплатно» (правки без ИИ открывают редактор на инструменте).
// У «Изменить» — «Где менять: Вся картинка | Отмеченное · N»: при «Вся картинка» маска не
// уходит, даже если отметки есть. Персонаж и образцы — у операций с промптом.

import { Brush } from 'lucide-react';
import { Button, Checkbox, FS, InlineSegmented, Select, SP, C } from 'aihome_shell/kit';
import { AUTO_MODEL, type ImageEditCatalog } from '../api';
import { OUTPAINT_RATIOS, quickOffered } from '../editorInputs';
import { BlockedNotice } from '../strip/settings/SizeSection';
import { ic, Label, Opt, type Launch } from '../strip/settings/primitives';
import { openEditor, setWholeImage } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';
import { NoSamplesSection } from './OpSection';
import { BodyHint, ExecutorField, FieldLabel, MoreSettings, SamplesField } from './BodyParts';
import {
  EDIT_MODES, editOpOptions, editPickOf, editWhere, isNoAiTool, isOneVariant, noSamplesHint, runVerb, setPanelChoice,
  type EditPick, type EditWhere,
} from './panelOp';

export function EditBody({ projectId, sessionId, thread, L, catalog, isMobile, onCharacters }: {
  projectId: string; sessionId: string | null; thread: ImageThread; L: Launch; catalog: ImageEditCatalog;
  isMobile: boolean; onCharacters: () => void;
}) {
  const pick = editPickOf(L.choice.op);
  const maskMarks = L.hasImage ? L.drawn.filter(m => m.type === 'mask').length : 0;
  const where = editWhere(maskMarks, L.whole);
  const noSamples = noSamplesHint(L.op, !!L.quickAction);
  const autoModel = L.model?.id === AUTO_MODEL && !L.quickAction;

  const onPick = (v: EditPick | '') => {
    if (!v) return;
    if (isNoAiTool(v)) { if (sessionId) openEditor(sessionId, thread.id, null, { tool: v }); return; }
    setPanelChoice(projectId, { op: v });
  };
  const onWhere = (w: EditWhere) => setWholeImage(thread.id, w === 'whole');

  const more: string[] = [];
  if (autoModel) more.push(`подбор: ${EDIT_MODES.find(([m]) => m === L.choice.mode)?.[1].toLowerCase() ?? 'авто'}`);
  if (L.hasImage) more.push(L.settings.matchSourceSize ? 'размер оригинала' : 'размер модели');

  return (
    <div data-image-body="edit" style={{ fontSize: FS.sm }}>
      <Label>Операция</Label>
      <div data-image-op-select="">
        <Select<EditPick> value={pick} options={editOpOptions(quickOffered('enhanceFaces', catalog))} onChange={onPick}
          title={L.op === 'inpaint' ? 'Изменить по тексту · по отмеченному' : undefined} />
      </div>
      {pick === 'edit' && (
        <div data-image-where="">
          <FieldLabel aside={isMobile ? 'отметок на телефоне нет' : 'отметки — в редакторе'}>Где менять</FieldLabel>
          <div style={{ display: 'flex' }}>
            <InlineSegmented<EditWhere> value={where} isMobile={isMobile} onChange={onWhere}
              options={[
                { value: 'whole', label: 'Вся картинка' },
                { value: 'marked', label: `Отмеченное · ${maskMarks}`, disabled: maskMarks === 0,
                  title: maskMarks ? 'Менять только закрашенное кистью' : 'Отметьте место кистью в редакторе картинки' },
              ]} />
          </div>
          {!isMobile && sessionId && (
            <Button size="xs" variant="ghost" leftIcon={ic(Brush)} style={{ marginTop: SP.xs }}
              onClick={() => openEditor(sessionId, thread.id, null, { tool: 'mask' })}>
              Отметить в редакторе
            </Button>
          )}
        </div>
      )}
      {L.op === 'outpaint' && (
        <>
          <Label>До пропорций</Label>
          <div style={{ display: 'flex' }}>
            <InlineSegmented value={L.choice.ratio} isMobile={isMobile} touchWidth
              options={OUTPAINT_RATIOS.map(r => ({ value: r, label: r }))}
              onChange={ratio => setPanelChoice(projectId, { ratio })} />
          </div>
        </>
      )}
      {noSamples
        ? <NoSamplesSection title="Персонаж и образцы" hint={noSamples} />
        : <SamplesField projectId={projectId} L={L} catalog={catalog} onCharacters={onCharacters} isMobile={isMobile} />}
      {L.quickAction
        ? <BodyHint icon="info">Текст в поле ввода не нужен — достаточно нажать «{runVerb(L.op)}» внизу.{isOneVariant(L.op) ? ' Даёт один вариант.' : ''}</BodyHint>
        : <BodyHint>{L.op === 'inpaint' ? 'Что сделать с отмеченным' : 'Что изменить'} — в поле ввода чата</BodyHint>}
      <ExecutorField L={L} catalog={catalog} isMobile={isMobile} />
      <MoreSettings summary={more} isMobile={isMobile}>
        {autoModel && (
          <>
            <Label>Режим подбора</Label>
            <div data-image-modes="" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
              {EDIT_MODES.map(([mode, name, hint]) => (
                <Opt key={mode} on={L.choice.mode === mode} name={name} hint={hint} onClick={() => setPanelChoice(projectId, { mode })} />
              ))}
            </div>
          </>
        )}
        {L.hasImage && (
          <label style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.md, paddingBottom: SP.xs, cursor: 'pointer', color: C.textPrimary }}>
            <Checkbox checked={L.settings.matchSourceSize} onChange={v => L.setSettings({ matchSourceSize: v })} ariaLabel="Размер оригинала" />
            Вернуть в размере оригинала
          </label>
        )}
      </MoreSettings>
      <BlockedNotice L={L} />
    </div>
  );
}
