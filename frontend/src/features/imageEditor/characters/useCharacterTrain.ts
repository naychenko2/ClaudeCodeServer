// Состояние обучения LoRA персонажа для всех мест панели: карточка, главная кнопка, шторка.
// Перенос из CharacterLoraTrainSection без смены поведения; разметка и модалки остаются у потребителей.

import { useEffect, useState } from 'react';
import { loraTrainApi } from '../api';
import { reloadCharacters } from './useCharacters';
import { useLoraTraining } from './useLoraTraining';
import type { LoraVersionsHandle } from './useLoraVersions';
import { failureText, trainSectionView, type TrainSectionView } from './loraTrainView';
import { TRAIN_MIN } from './trainingPhotos';

export function useCharacterTrain(
  projectId: string, slug: string,
  // null — список фото ещё не загружен
  photoCount: number | null,
  // Общий с «Версиями персонажа» контроллер: ошибка подключения версии показывается рядом с обучением
  versions: LoraVersionsHandle,
) {
  const training = useLoraTraining(projectId, slug);
  const [edited, setEdited] = useState<string | null>(null);
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [isAdmin, setIsAdmin] = useState(false);

  const trigger = edited ?? training.state?.defaultTrigger ?? '';
  const foreignBusy = !!training.state?.gpu.busy && !training.state.gpu.mine;
  useEffect(() => {
    setIsAdmin(false);
    if (!foreignBusy) return;
    let alive = true;
    loraTrainApi.active().then(a => { if (alive) setIsAdmin(!!a.jobId); }).catch(() => {});
    return () => { alive = false; };
  }, [foreignBusy, projectId, slug]);

  const view: TrainSectionView = trainSectionView({
    view: training.view, state: training.state, error: training.error, photoCount,
    minPhotos: TRAIN_MIN, trigger, isAdmin, dismissedRunId: dismissed,
  });

  // Первое обучение сервер подключает само — подтягиваем персонажа, чтобы раздел LoRA показал новую
  const doneRun = view.kind === 'done' && view.first ? view.version.runId : null;
  useEffect(() => { if (doneRun) void reloadCharacters(projectId); }, [doneRun, projectId]);

  return {
    view,
    training,
    trigger,
    // Явно введённый триггер (null — взят умолчательный): уходит в start
    edited,
    setTrigger: setEdited,
    // Скрыть результат прогона (runId) и вернуться к форме
    dismiss: setDismissed,
    isAdmin,
    // Ошибка действий обучения и ошибка подключения версии — текстом для показа
    actionError: training.error ? failureText(training.error) : null,
    connectError: versions.error ? failureText(versions.error) : null,
  };
}
