import type { DesktopDevice } from '../../types';
export declare function isLocalProjectDevice(d: DesktopDevice): boolean;
export declare function deviceLabel(d: DesktopDevice): string;
export declare const NO_DEVICES_HINT = "\u041D\u0435\u0442 \u0443\u0441\u0442\u0440\u043E\u0439\u0441\u0442\u0432 \u0441 \u0430\u0433\u0435\u043D\u0442\u043E\u043C AI Home. \u041F\u043E\u0434\u043A\u043B\u044E\u0447\u0438\u0442\u0435 \u043A\u043E\u043C\u043F\u044C\u044E\u0442\u0435\u0440 \u0441 \u043F\u0430\u043F\u043A\u043E\u0439 \u043F\u0440\u043E\u0435\u043A\u0442\u0430: \u043C\u0435\u043D\u044E \u0430\u0432\u0430\u0442\u0430\u0440\u0430 \u2192 \u00AB\u0423\u0441\u0442\u0440\u043E\u0439\u0441\u0442\u0432\u0430\u00BB.";
export declare function useDevicesMenuVisible(): boolean;
