// Регистрация подсистемы «Плагины»: side-effect-импорт манифеста в реестр.
//
// Это единственный файл фичи, который знает про реестр. Каркас (App, общие
// компоненты, lib) о существовании вкладов плагинов не знает: он читает слоты
// из реестра. Манифест вынесен в manifest.tsx — чистый экспорт (без side-effect).

import { registerSubsystem } from 'aihome_shell/kit';
import { manifest } from './manifest';

registerSubsystem(manifest);
