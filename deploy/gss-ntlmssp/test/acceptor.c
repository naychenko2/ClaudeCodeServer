/* Тестовый акцептор NTLM поверх GSSAPI: читает из stdin hex-токены, пишет ответ в stdout.
 * Строки ответа: "TOKEN <hex>" (токен для клиента), "OK <имя>" либо "FAIL <maj> <min>". */
#include <gssapi/gssapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static gss_OID_desc ntlm_oid = { 10, (void *)"\x2b\x06\x01\x04\x01\x82\x37\x02\x02\x0a" };

static int unhex(const char *s, unsigned char *out, size_t max)
{
    size_t n = strlen(s);
    while (n && (s[n - 1] == '\n' || s[n - 1] == '\r')) n--;
    if (n % 2 || n / 2 > max) return -1;
    for (size_t i = 0; i < n / 2; i++) {
        unsigned v;
        if (sscanf(s + 2 * i, "%2x", &v) != 1) return -1;
        out[i] = (unsigned char)v;
    }
    return (int)(n / 2);
}

int main(void)
{
    OM_uint32 maj, min;
    gss_OID_set_desc mechs = { 1, &ntlm_oid };
    gss_cred_id_t cred = GSS_C_NO_CREDENTIAL;
    gss_ctx_id_t ctx = GSS_C_NO_CONTEXT;
    char *line = NULL;
    size_t cap = 0;

    maj = gss_acquire_cred(&min, GSS_C_NO_NAME, GSS_C_INDEFINITE, &mechs,
                           GSS_C_ACCEPT, &cred, NULL, NULL);
    if (maj) { printf("FAIL %u %u acquire\n", maj, min); return 1; }
    setvbuf(stdout, NULL, _IOLBF, 0);

    while (getline(&line, &cap, stdin) > 0) {
        unsigned char buf[8192];
        int n = unhex(line, buf, sizeof buf);
        if (n < 0) { printf("FAIL 0 0 hex\n"); return 1; }
        gss_buffer_desc in = { (size_t)n, buf }, out = GSS_C_EMPTY_BUFFER;
        gss_name_t src = GSS_C_NO_NAME;

        maj = gss_accept_sec_context(&min, &ctx, cred, &in, GSS_C_NO_CHANNEL_BINDINGS,
                                     &src, NULL, &out, NULL, NULL, NULL);
        if (GSS_ERROR(maj)) { printf("FAIL %u %u\n", maj, min); return 2; }
        if (out.length) {
            printf("TOKEN ");
            for (size_t i = 0; i < out.length; i++) printf("%02x", ((unsigned char *)out.value)[i]);
            printf("\n");
            gss_release_buffer(&min, &out);
        }
        if (!(maj & GSS_S_CONTINUE_NEEDED)) {
            gss_buffer_desc nm = GSS_C_EMPTY_BUFFER;
            gss_display_name(&min, src, &nm, NULL);
            printf("OK %.*s\n", (int)nm.length, (char *)nm.value);
            return 0;
        }
    }
    return 3;
}
