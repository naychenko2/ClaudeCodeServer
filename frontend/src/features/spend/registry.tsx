// Регистрация подсистемы «Аналитика»: side-effect-импорт манифеста в реестр.
//
// Пока Spend не MF-модуль, регистрация идёт статическим импортом из
// lib/subsystems/registry.ts. На шаге Ф2.3 (переезд на MF-remote) эта
// строка-импорт будет убрана, а manifest станет доступным через loadRemote.

import { registerSubsystem } from 'aihome_shell/kit';
import { manifest } from './manifest';

registerSubsystem(manifest);
