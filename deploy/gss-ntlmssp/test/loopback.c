/* Петля инициатор → акцептор gss-ntlmssp в одном процессе без запроса SIGN/SEAL (флаги GSS = 0).
 * Печатает "OK" или "FAIL maj min"; любой аргумент включает GSS_C_DATAGRAM_FLAG (KEY_EXCH без SIGN/SEAL). Нужны NTLM_USER_FILE и GSS_MECH_CONFIG. */
#include <gssapi/gssapi.h>
#include <gssapi/gssapi_ext.h>
#ifndef GSS_C_DATAGRAM_FLAG
#define GSS_C_DATAGRAM_FLAG 0x10000
#endif
#include <stdio.h>
#include <string.h>

static gss_OID_desc ntlm_oid = { 10, (void *)"\x2b\x06\x01\x04\x01\x82\x37\x02\x02\x0a" };

int main(int argc, char **argv)
{
    OM_uint32 maj, min;
    gss_OID_set_desc mechs = { 1, &ntlm_oid };
    gss_cred_id_t acred = GSS_C_NO_CREDENTIAL, icred = GSS_C_NO_CREDENTIAL;
    gss_ctx_id_t ictx = GSS_C_NO_CONTEXT, actx = GSS_C_NO_CONTEXT;
    gss_buffer_desc in = GSS_C_EMPTY_BUFFER, out = GSS_C_EMPTY_BUFFER, nb;
    gss_name_t target = GSS_C_NO_NAME;

    maj = gss_acquire_cred(&min, GSS_C_NO_NAME, GSS_C_INDEFINITE, &mechs, GSS_C_ACCEPT, &acred, NULL, NULL);
    if (maj) { printf("FAIL %u %u acquire-acc\n", maj, min); return 1; }

    gss_buffer_desc un = { strlen("WORKGROUP\\andrey"), "WORKGROUP\\andrey" };
    gss_name_t uname;
    maj = gss_import_name(&min, &un, GSS_C_NT_USER_NAME, &uname);
    gss_key_value_element_desc el = { "password", "secret" };
    gss_key_value_set_desc store = { 1, &el };
    maj = gss_acquire_cred_from(&min, uname, GSS_C_INDEFINITE, &mechs, GSS_C_INITIATE, &store, &icred, NULL, NULL);
    if (maj) { printf("FAIL %u %u acquire-init\n", maj, min); return 1; }
    nb.value = "HTTP@host"; nb.length = 9;
    gss_import_name(&min, &nb, GSS_C_NT_HOSTBASED_SERVICE, &target);

    for (int i = 0; i < 6; i++) {
        gss_buffer_desc o1 = GSS_C_EMPTY_BUFFER;
        maj = gss_init_sec_context(&min, icred, &ictx, target, &ntlm_oid, argc > 1 ? GSS_C_DATAGRAM_FLAG : 0, 0, NULL, &in, NULL, &o1, NULL, NULL);
        if (GSS_ERROR(maj)) { printf("FAIL %u %u init\n", maj, min); return 2; }
        if (!(maj & GSS_S_CONTINUE_NEEDED) && !o1.length) break;
        if (in.length) gss_release_buffer(&min, &in);
        maj = gss_accept_sec_context(&min, &actx, acred, &o1, GSS_C_NO_CHANNEL_BINDINGS, NULL, NULL, &out, NULL, NULL, NULL);
        if (GSS_ERROR(maj)) { printf("FAIL %u %u accept\n", maj, min); return 3; }
        if (!(maj & GSS_S_CONTINUE_NEEDED)) { printf("OK\n"); return 0; }
        in = out;
    }
    printf("FAIL loop\n");
    return 4;
}
