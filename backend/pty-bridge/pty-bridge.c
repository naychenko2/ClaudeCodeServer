/*
 * pty-bridge — полноценный PTY через forkpty(3).
 *
 * Создаёт псевдо-терминал, запускает /bin/bash в нём,
 * релеит stdin → master fd, master fd → stdout.
 *
 * Resize: stdin-байт 0x1B (ESC), затем 'R', затем 2 байта cols
 * (big-endian), 2 байта rows. Всё остальное — ввод в PTY.
 *
 * Компиляция:
 *   gcc -O2 -static -o pty-bridge pty-bridge.c -lutil
 *
 * Запуск:
 *   ./pty-bridge [cols rows]
 */

#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <pty.h>
#include <sys/ioctl.h>
#include <sys/select.h>
#include <sys/wait.h>

#define BUF_SIZE 65536

static int master_fd = -1;
static pid_t child_pid = -1;

/* Установить размер терминала */
static void set_winsize(int fd, unsigned short cols, unsigned short rows) {
    struct winsize ws;
    ws.ws_col = cols;
    ws.ws_row = rows;
    ws.ws_xpixel = 0;
    ws.ws_ypixel = 0;
    ioctl(fd, TIOCSWINSZ, &ws);
}

/* Обработка SIGCHLD — чтобы waitpid не блокировал */
static volatile sig_atomic_t child_exited = 0;
static void sigchld_handler(int signo) {
    (void)signo;
    child_exited = 1;
}

int main(int argc, char **argv) {
    unsigned short cols = 80, rows = 24;

    if (argc >= 3) {
        cols = (unsigned short)atoi(argv[1]);
        rows = (unsigned short)atoi(argv[2]);
    }
    if (cols < 8)  cols = 80;
    if (rows < 2)  rows = 24;

    /* Создаём PTY */
    struct termios term;
    child_pid = forkpty(&master_fd, NULL, NULL, NULL);
    if (child_pid == -1) {
        fprintf(stderr, "pty-bridge: forkpty failed: %s\n", strerror(errno));
        return 1;
    }

    if (child_pid == 0) {
        /* Дочерний процесс: запускаем bash */
        setenv("TERM", "xterm-256color", 1);
        execl("/bin/bash", "/bin/bash", "--norc", "--noediting", "-i", (char *)NULL);
        /* Если bash не нашёлся — fallback на sh */
        execl("/bin/sh", "/bin/sh", (char *)NULL);
        fprintf(stderr, "pty-bridge: exec failed: %s\n", strerror(errno));
        _exit(127);
    }

    /* Родитель: устанавливаем размер */
    set_winsize(master_fd, cols, rows);

    /* SIGCHLD — узнавать о смерти ребёнка */
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = sigchld_handler;
    sa.sa_flags = SA_RESTART | SA_NOCLDSTOP;
    sigaction(SIGCHLD, &sa, NULL);

    /* Режим stdin: неканонический, без эха (relay, не терминал) */
    tcgetattr(STDIN_FILENO, &term);
    term.c_lflag &= ~(ECHO | ICANON | ISIG);
    term.c_cc[VMIN] = 1;
    term.c_cc[VTIME] = 0;
    tcsetattr(STDIN_FILENO, TCSANOW, &term);

    /* Буфер и позиция чтения resize-команды */
    unsigned char buf[BUF_SIZE];
    int resize_state = 0; /* 0=обычный, 1=ESC получен, 2='R' получен, 3+ = чтение размера */
    unsigned char resize_cols[2], resize_rows[2];
    int resize_idx = 0;

    /* Основной цикл: select на stdin и master_fd */
    while (1) {
        fd_set rfds;
        FD_ZERO(&rfds);
        if (!child_exited) {
            FD_SET(STDIN_FILENO, &rfds);
            FD_SET(master_fd, &rfds);
        } else {
            /* Ребёнок умер — читаем остаток вывода и выходим */
            FD_SET(master_fd, &rfds);
        }

        int maxfd = master_fd > STDIN_FILENO ? master_fd : STDIN_FILENO;
        int ret = select(maxfd + 1, &rfds, NULL, NULL, NULL);

        if (ret < 0) {
            if (errno == EINTR) continue;
            break;
        }

        /* Чтение из PTY master → stdout */
        if (FD_ISSET(master_fd, &rfds)) {
            ssize_t n = read(master_fd, buf, BUF_SIZE);
            if (n <= 0) break;
            ssize_t written = 0;
            while (written < n) {
                ssize_t w = write(STDOUT_FILENO, buf + written, (size_t)(n - written));
                if (w < 0) {
                    if (errno == EINTR) continue;
                    break;
                }
                written += w;
            }
            fflush(stdout);
        }

        /* Чтение stdin → обработка resize / relay в PTY */
        if (FD_ISSET(STDIN_FILENO, &rfds)) {
            ssize_t n = read(STDIN_FILENO, buf, BUF_SIZE);
            if (n <= 0) break;

            for (ssize_t i = 0; i < n; i++) {
                unsigned char c = buf[i];

                if (resize_state > 0) {
                    /* Внутри resize-команды */
                    if (resize_state == 1 && c == 'R') {
                        resize_state = 2;
                        resize_idx = 0;
                    } else if (resize_state >= 2 && resize_state < 4) {
                        /* Чтение cols (2 байта big-endian) */
                        resize_cols[resize_idx++] = c;
                        if (resize_idx == 2) { resize_state = 4; resize_idx = 0; }
                    } else if (resize_state >= 4 && resize_state < 6) {
                        /* Чтение rows (2 байта big-endian) */
                        resize_rows[resize_idx++] = c;
                        if (resize_idx == 2) {
                            unsigned short new_cols = (unsigned short)((resize_cols[0] << 8) | resize_cols[1]);
                            unsigned short new_rows = (unsigned short)((resize_rows[0] << 8) | resize_rows[1]);
                            set_winsize(master_fd, new_cols, new_rows);
                            resize_state = 0;
                        }
                    } else {
                        /* Любой другой байт после ESC — отбой resize-режима */
                        /* Relay предыдущих байтов (ESC + не-R) в PTY — упрощённо релеим всё */
                        resize_state = 0;
                        /* Этот байт пойдёт в PTY через обычный путь */
                    }
                }

                if (resize_state == 0) {
                    if (c == 0x1B) {
                        resize_state = 1; /* ожидаем 'R' */
                    } else {
                        /* Relay в PTY */
                        char ch = (char)c;
                        ssize_t w = write(master_fd, &ch, 1);
                        (void)w; /* игнорируем ошибки записи */
                    }
                }
            }
        }

        if (child_exited && ret == 0) break;
    }

    /* Дожидаемся ребёнка */
    if (child_pid > 0) {
        int status;
        waitpid(child_pid, &status, 0);
    }

    return 0;
}
