// Регистрация подсистемы «Заметки»: side-effect-импорт манифеста в реестр.
//
// Это единственный файл фичи, который знает про реестр. Каркас (App, общие
// компоненты, lib) о существовании Notes не знает: он читает слоты и раздел из
// реестра. Удалить подсистему = удалить папку features/notes и одну строку в
// lib/subsystems/registry.ts.
//
// Манифест вынесен в manifest.tsx — чистый экспорт (без side-effect), чтобы
// Module Federation remote мог экспортировать его независимо от регистрации.

import { registerSubsystem } from 'aihome_shell/kit';
import { manifest } from './manifest';

registerSubsystem(manifest);
