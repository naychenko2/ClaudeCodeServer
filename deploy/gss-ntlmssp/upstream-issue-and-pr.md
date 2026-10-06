# Черновик для gssapi/gss-ntlmssp (НЕ опубликован)

Публикация — внешнее действие, только по явной просьбе человека. Патч —
[ntlm-key-exch-requires-sign-seal.diff](ntlm-key-exch-requires-sign-seal.diff) (основан на 1.2.0; в `main` и 1.3.x
тот же код, перед отправкой — перебазировать и перепроверить номера строк).

## Issue

**Title:** Acceptor decrypts EncryptedRandomSessionKey when KEY_EXCH is set without SIGN/SEAL, MIC check fails (GSS_S_DEFECTIVE_TOKEN)

**Body:**

With a Windows SSPI client that negotiates `NTLMSSP_NEGOTIATE_KEY_EXCH` but neither `NTLMSSP_NEGOTIATE_SIGN` nor
`NTLMSSP_NEGOTIATE_SEAL` (this is what HTTP-style Negotiate/NTLM clients such as Word/WebDAV and `curl.exe --ntlm`
on Windows send; Type1 flags `0xE2088207`), `gssntlm_accept_sec_context()` rejects a correct password:
the NT hash check passes, then the MIC check fails and the call returns `GSS_S_DEFECTIVE_TOKEN`
(`gss_sec_ctx.c`, the `ntlm_verify_mic()` branch, `GSSNTLMSSP_DEBUG` shows `[589824:13]`).

Cause: the acceptor decrypts `EncryptedRandomSessionKey` on `KEY_EXCH` alone. [MS-NLMP] 3.2.5.1.2 (and 3.1.5.1.2 for the
client) uses the encrypted session key only if `KEY_EXCH` **and** (`SIGN` or `SEAL`) are in `NegFlg`:

```
If (NTLMSSP_NEGOTIATE_KEY_EXCH flag is set in NegFlg
  AND (NTLMSSP_NEGOTIATE_SIGN OR NTLMSSP_NEGOTIATE_SEAL are set in NegFlg))
    Set ExportedSessionKey to RC4K(KeyExchangeKey, AUTHENTICATE_MESSAGE.EncryptedRandomSessionKey)
Else
    Set ExportedSessionKey to KeyExchangeKey
```

Without SIGN/SEAL the MIC is therefore keyed with `KeyExchangeKey`; the acceptor keys it with a garbage RC4 output.

Reproduction (raw NTLMv2 client, only Type1 flags vary; the MIC is valid):

| Type1 flags | result before | result after |
|---|---|---|
| `0xE2088237` (SIGN+SEAL) | OK | OK |
| `0xE2088217` (SIGN) / `0xE2088227` (SEAL) | OK | OK |
| `0xE2088207` (no SIGN/SEAL) | `GSS_S_DEFECTIVE_TOKEN` | OK |
| `0xE2088207`, tampered MIC | rejected | rejected |
| wrong password | rejected | rejected |

The MIC verification is not weakened: only the key selection follows the spec.

## Pull request

**Title:** Use KeyExchangeKey as ExportedSessionKey unless KEY_EXCH is combined with SIGN/SEAL

**Description:** Fixes the issue above. Changes:

- `gss_sec_ctx.c` (`gssntlm_accept_sec_context`): decrypt `EncryptedRandomSessionKey` only when
  `KEY_EXCH && (SIGN || SEAL)`; otherwise `ExportedSessionKey = KeyExchangeKey` (MS-NLMP 3.2.5.1.2).

Verification: raw NTLMv2 client against a GSSAPI acceptor, matrix above; the `0xE2088207` case fails before the patch and
passes after, tampered-MIC and wrong-password cases fail in both.
Suggested test for the upstream suite: add the `0xE2088207` negotiate-flag case to `tests/t_auth.c` / the NTLMv2
acceptor tests next to the existing SIGN+SEAL one.

## For discussion with the maintainer (not part of the PR)

`gss_auth.c` (`gssntlm_cli_auth`): the initiator side may need the symmetric rule: the MIC keyed with `KeyExchangeKey`
when `KEY_EXCH` is negotiated without SIGN/SEAL (MS-NLMP 3.1.5.1.2). Only reachable in datagram mode, and
`EncryptedRandomSessionKey` is still sent there, so changing it alone may break interop with unpatched acceptors.
We have not tested it, so it is left out of the PR and of our local package.

## Второй дефект: пара MsvAvFlags=0 в Type2 (отдельный issue, патч [ntlm-no-empty-msvavflags-in-type2.diff](ntlm-no-empty-msvavflags-in-type2.diff))

### Issue

**Title:** Acceptor puts an empty MsvAvFlags pair into the Type2 TargetInfo; Windows SSPI edits it in place and the client MIC never verifies

**Body:**

`gssntlm_accept_sec_context()` encodes the CHALLENGE_MESSAGE TargetInfo with `MsvAvFlags = 0` (`av_flags` is passed to
`ntlm_encode_target_info()` unconditionally). A Windows SSPI client (Word/WebDAV, `curl.exe --negotiate`) treats that pair
as a place to set `MIC_PRESENT`: it edits the received pair in place (`|= 0x2`) and computes the MIC over its own,
already modified copy of the Type2 message. The acceptor stores and signs the Type2 exactly as it sent it (without the
edit), so the MIC check fails with `GSS_S_DEFECTIVE_TOKEN` for a correct password. Without the pair in Type2, Windows
appends the flags in a separate AV blob and the MIC covers the Type2 as sent on the wire (what spec-compliant clients do).
An offline recomputation of the MIC over Type2 with `MsvAvFlags = 0x2` reproduces the client value exactly.

### Pull request

**Title:** Do not send an empty MsvAvFlags pair in Type2

Pass `NULL` instead of `&av_flags` to `ntlm_encode_target_info()` when `av_flags` is 0. The MIC verification is not
weakened. Verification: see the live Windows check in the task result (patched `+ccs2` against a stand).
