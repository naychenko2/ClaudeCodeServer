/*
 * pty-bridge — полноценный PTY через forkpty(3).
 *
 * Создаёт псевдо-терминал, запускает /bin/bash в нём,
 * релеит stdin → master fd, master fd → stdout.
 *
 * ПРОТОКОЛ stdin (кадры, чтобы управление не смешивалось с вводом):
 *   [type:1][len:4 big-endian][payload:len]
 *     type=0x00 — данные ввода: payload пишется как есть в PTY;
 *     type=0x01 — resize: payload = 4 байта (cols big-endian, rows big-endian).
 *   Раньше resize слался inband спецпоследовательностью ESC 'R' в тот же поток —
 *   это ломало ввод escape-последовательностей (стрелки: ведущий ESC терялся),
 *   а байты размера (напр. rows=24=0x18) утекали в шелл как ^X. Кадры это исключают.
 *
 * master fd → stdout: сырой вывод PTY (без обёртки).
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
#include <stdint.h>
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

/* Записать все байты в fd (с учётом частичных write) */
static void write_all(int fd, const unsigned char *data, size_t n) {
    size_t written = 0;
    while (written < n) {
        ssize_t w = write(fd, data + written, n - written);
        if (w < 0) {
            if (errno == EINTR) continue;
            break;
        }
        written += (size_t)w;
    }
}

/* ---- Парсер кадров stdin (устойчив к фрагментации на границах read) ---- */
enum frame_state { F_TYPE, F_LEN, F_PAYLOAD };
static enum frame_state fstate = F_TYPE;
static unsigned char frame_type = 0;
static unsigned char len_buf[4];
static int len_idx = 0;
static uint32_t frame_len = 0;
static uint32_t frame_got = 0;
static unsigned char resize_buf[4];

/* Обработать очередной прочитанный из stdin блок по протоколу кадров */
static void process_stdin(const unsigned char *buf, ssize_t n) {
    ssize_t i = 0;
    while (i < n) {
        unsigned char c = buf[i];
        switch (fstate) {
            case F_TYPE:
                frame_type = c;
                fstate = F_LEN;
                len_idx = 0;
                i++;
                break;
            case F_LEN:
                len_buf[len_idx++] = c;
                i++;
                if (len_idx == 4) {
                    frame_len = ((uint32_t)len_buf[0] << 24) | ((uint32_t)len_buf[1] << 16)
                              | ((uint32_t)len_buf[2] << 8)  | (uint32_t)len_buf[3];
                    frame_got = 0;
                    if (frame_len == 0) {
                        fstate = F_TYPE; /* пустой кадр — ничего не делаем */
                    } else {
                        fstate = F_PAYLOAD;
                    }
                }
                break;
            case F_PAYLOAD: {
                uint32_t remaining = frame_len - frame_got;
                uint32_t avail = (uint32_t)(n - i);
                uint32_t take = avail < remaining ? avail : remaining;
                if (frame_type == 0x00) {
                    /* данные — стримим сразу куском в PTY */
                    write_all(master_fd, buf + i, take);
                } else if (frame_type == 0x01) {
                    /* resize — собираем до 4 байт */
                    for (uint32_t k = 0; k < take; k++) {
                        if (frame_got + k < sizeof(resize_buf))
                            resize_buf[frame_got + k] = buf[i + k];
                    }
                }
                frame_got += take;
                i += (ssize_t)take;
                if (frame_got == frame_len) {
                    if (frame_type == 0x01 && frame_len >= 4) {
                        unsigned short cols = (unsigned short)((resize_buf[0] << 8) | resize_buf[1]);
                        unsigned short rows = (unsigned short)((resize_buf[2] << 8) | resize_buf[3]);
                        set_winsize(master_fd, cols, rows);
                    }
                    fstate = F_TYPE;
                }
                break;
            }
        }
    }
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

    /* Режим stdin: неканонический, без эха (relay, не терминал).
     * stdin здесь — pipe от .NET; tcsetattr на pipe вернёт ENOTTY, это ок. */
    if (tcgetattr(STDIN_FILENO, &term) == 0) {
        term.c_lflag &= ~(ECHO | ICANON | ISIG);
        term.c_cc[VMIN] = 1;
        term.c_cc[VTIME] = 0;
        tcsetattr(STDIN_FILENO, TCSANOW, &term);
    }

    unsigned char buf[BUF_SIZE];

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

        /* Чтение из PTY master → stdout (сырой вывод) */
        if (FD_ISSET(master_fd, &rfds)) {
            ssize_t n = read(master_fd, buf, BUF_SIZE);
            if (n <= 0) break;
            write_all(STDOUT_FILENO, buf, (size_t)n);
            fflush(stdout);
        }

        /* Чтение stdin → разбор кадров → relay данных / resize */
        if (!child_exited && FD_ISSET(STDIN_FILENO, &rfds)) {
            ssize_t n = read(STDIN_FILENO, buf, BUF_SIZE);
            if (n <= 0) break;
            process_stdin(buf, n);
        }

        if (child_exited && ret == 0) break;
    }

    /* Дожидаемся ребёнка. Если вышли из цикла из-за закрытия stdin (не смерти ребёнка) —
     * шлём шеллу SIGHUP, иначе waitpid заблокируется на живом интерактивном bash. */
    if (child_pid > 0) {
        if (!child_exited) kill(child_pid, SIGHUP);
        int status;
        waitpid(child_pid, &status, 0);
    }

    return 0;
}
