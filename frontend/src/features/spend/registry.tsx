// Регистрация подсистемы «Аналитика»: side-effect-импорт манифеста в реестр.
//
// После Ф2.3 (переезд на MF-remote) хост манифест получает через
// loadRemote('aihome_spend/subsystem'), а не статическим импортом этой строки,
// поэтому файл в графе хоста не висит (остался бы только для статической
// регистрации; manifest сам доступен через modules/spend/subsystem.tsx).

import { registerSubsystem } from 'aihome_shell/kit';
import { manifest } from './manifest';

registerSubsystem(manifest);
