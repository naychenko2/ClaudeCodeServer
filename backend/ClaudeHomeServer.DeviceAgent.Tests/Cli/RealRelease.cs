namespace ClaudeHomeServer.DeviceAgent.Tests.Cli;

/// <summary>
/// Настоящий выпуск 2.1.273 из downloads.claude.ai: <c>manifest.json</c> байт-в-байт (base64,
/// чтобы git не переписал переводы строк) и его <c>manifest.json.sig</c>, подписанный ключом
/// Anthropic. Проверка на них идёт без сети.
/// </summary>
internal static class RealRelease
{
    public const string Version = "2.1.273";

    public static byte[] Manifest => Convert.FromBase64String(ManifestBase64);

    public static byte[] Signature => System.Text.Encoding.ASCII.GetBytes(SignatureArmored);

    private const string ManifestBase64 =
        "ewogICJ2ZXJzaW9uIjogIjIuMS4yNzMiLAogICJtYW5pZmVzdFNpZ25hdHVyZUVuZm9yY2VtZW50IjogImZsYWciLAogICJjb21t" +
        "aXQiOiAiZDQ4ZWNmZDdhNDFjMTZjNDJlMDU2NGY3YTk0OTQ3ZDZlNGM1MGRiMSIsCiAgIm1vZHNDb21taXQiOiAiZjk2YzNiNDlj" +
        "NGM4NzIxNjg1MjA2YWFhYjIzNjA5YjJkMzk5ZGY0ZSIsCiAgImJ1aWxkRGF0ZSI6ICIyMDI2LTA5LTE1VDE3OjE5OjIyWiIsCiAg" +
        "InBsYXRmb3JtcyI6IHsKICAgICJkYXJ3aW4tYXJtNjQiOiB7CiAgICAgICJiaW5hcnkiOiAiY2xhdWRlIiwKICAgICAgImNoZWNr" +
        "c3VtIjogIjk1M2U5ODgwZGJjYjBiNzBmMzFjMWY1MDhkZTZhM2ZkMzg5NzUzZDEzMTY4ODU1N2ZkOTkyZGE5MTg0NjkzZmIiLAog" +
        "ICAgICAic2l6ZSI6IDIxMjIyODg4MAogICAgfSwKICAgICJkYXJ3aW4teDY0IjogewogICAgICAiYmluYXJ5IjogImNsYXVkZSIs" +
        "CiAgICAgICJjaGVja3N1bSI6ICIyMDMwZWNmOTExZTMwMWU3NzhiM2M1YTQ5MDY4ZDY3NTEzODRjODMwZTQ4ZWExNGYxMWViNjFk" +
        "ZDIzNjIyY2VlIiwKICAgICAgInNpemUiOiAyMjEwMjM0NTYKICAgIH0sCiAgICAibGludXgtYXJtNjQiOiB7CiAgICAgICJiaW5h" +
        "cnkiOiAiY2xhdWRlIiwKICAgICAgImNoZWNrc3VtIjogIjEwM2NmYWI0ZDZhZTg5OGI2YWY2OTIzMzZmYjY2MmZmY2M2MDcwNzVj" +
        "YzY0MDg4OTJiZDM1MGIzZjU0OWViZWUiLAogICAgICAic2l6ZSI6IDIyODU4MTYxNgogICAgfSwKICAgICJsaW51eC14NjQiOiB7" +
        "CiAgICAgICJiaW5hcnkiOiAiY2xhdWRlIiwKICAgICAgImNoZWNrc3VtIjogIjZjNzUyZTJjYzdjMTEwYzlkZjE1ZjI2ZDhkMTM0" +
        "ZDQzOGM1YWU5NWRiZDYxMGVmYzFhMzA4YmY3ZjljNWY2YzEiLAogICAgICAic2l6ZSI6IDIyODY2MzYwOAogICAgfSwKICAgICJs" +
        "aW51eC1hcm02NC1tdXNsIjogewogICAgICAiYmluYXJ5IjogImNsYXVkZSIsCiAgICAgICJjaGVja3N1bSI6ICIwZWE2NGU5MzJj" +
        "NmZhYzM1Y2U0ZTk5MjE0MDYxMjE1N2ZlMWNlY2FiMDUyZDU5NTM5ODczYjM1MGE1OTRkM2M2IiwKICAgICAgInNpemUiOiAyMjEx" +
        "OTc5ODQKICAgIH0sCiAgICAibGludXgteDY0LW11c2wiOiB7CiAgICAgICJiaW5hcnkiOiAiY2xhdWRlIiwKICAgICAgImNoZWNr" +
        "c3VtIjogIjE5MzA1MTQ1MDI4ZGZiNzc0ZmYxNmZkMjA2NmI4OTkxY2I5M2QxYWZiZTRiOGZmNjM4OWFlYzU0ZjFiMzA5ZjAiLAog" +
        "ICAgICAic2l6ZSI6IDIyMjU4NDc5MgogICAgfSwKICAgICJ3aW4zMi14NjQiOiB7CiAgICAgICJiaW5hcnkiOiAiY2xhdWRlLmV4" +
        "ZSIsCiAgICAgICJjaGVja3N1bSI6ICIxOTY1NDAwNjY3MmI2ZGE3Yzk0NTExNWVlYTk5Y2ExMDA1MTc5NjAxNmRmNTYzYTY1YjNm" +
        "MGM3ZDcyNzIwZWYwIiwKICAgICAgInNpemUiOiAyMzE3NzY0MTYKICAgIH0sCiAgICAid2luMzItYXJtNjQiOiB7CiAgICAgICJi" +
        "aW5hcnkiOiAiY2xhdWRlLmV4ZSIsCiAgICAgICJjaGVja3N1bSI6ICIxMjU3NjA4ZDdhMzQ1MTVkOThjMjNhNjg4NGZiZTY1ODE5" +
        "NWYyZGU0MzBhZTE2NzlmMDQ3ZjRkOGFmZGEyMGQ2IiwKICAgICAgInNpemUiOiAyMjMwMDEyNDgKICAgIH0KICB9LAogICJzZGtD" +
        "b21wYXQiOiB7CiAgICAidGVzdGVkV3JhcHBlclZlcnNpb25zIjogWwogICAgICAiMC4zLjIzMyIsCiAgICAgICIwLjMuMjM0IiwK" +
        "ICAgICAgIjAuMy4yMzUiLAogICAgICAiMC4zLjIzNiIsCiAgICAgICIwLjMuMjM3IiwKICAgICAgIjAuMy4yMzgiLAogICAgICAi" +
        "MC4zLjIzOSIsCiAgICAgICIwLjMuMjQwIiwKICAgICAgIjAuMy4yNDEiLAogICAgICAiMC4zLjI0MiIsCiAgICAgICIwLjMuMjQz" +
        "IiwKICAgICAgIjAuMy4yNDUiLAogICAgICAiMC4zLjI0NiIsCiAgICAgICIwLjMuMjQ3IiwKICAgICAgIjAuMy4yNDgiLAogICAg" +
        "ICAiMC4zLjI1MCIsCiAgICAgICIwLjMuMjUxIiwKICAgICAgIjAuMy4yNTIiLAogICAgICAiMC4zLjI1OCIsCiAgICAgICIwLjMu" +
        "MjU5IiwKICAgICAgIjAuMy4yNjAiLAogICAgICAiMC4zLjI2MSIsCiAgICAgICIwLjMuMjYzIiwKICAgICAgIjAuMy4yNjYiLAog" +
        "ICAgICAiMC4zLjI2NyIsCiAgICAgICIwLjMuMjY4IiwKICAgICAgIjAuMy4yNjkiLAogICAgICAiMC4zLjI3MCIsCiAgICAgICIw" +
        "LjMuMjcxIiwKICAgICAgIjAuMy4yNzIiCiAgICBdLAogICAgImhhcm5lc3NTY2hlbWEiOiAxCiAgfQp9Cg==";

    private const string SignatureArmored = """
        -----BEGIN PGP SIGNATURE-----

        iQIzBAABCgAdFiEEMd3eJN36tnn0LXvSuqkp/xp+ys4FAmqpfnUACgkQuqkp/xp+
        ys7kWQ//c9IfM0gVejpllCrdnv/flWoMtqmeMtgfMOnl21Ldny8N29KeMZB56Wtw
        vD7Z7oGibrlZYQT7h2CUwiT0y0b/8MV/OAV3do+lY9HREe5t2XpOply0KFkHepo+
        sy9UBVsChPQ6vTWtOihgz9zuNlr/LdmOJNf8WnGVprPcirEI0d3HivHUKGIYT4jz
        +9+UvSTpPgmRaYYglomnBTyQ+RBiRXLbCVNvgcpDL4TIPtG1Dmn5rPTvNxA54Qla
        v9TKHa83QJP0HV6rCZErsJ1OI+s/KFeYSAAyNoXpz3Lq3I+kPqcwQj7yogIL2H0e
        2qDTu4H0TYEu6FJeQSX3EUySi84XUZZZxUZ51NOQVAAvkc14DzdsjXAMy6YX2LHB
        vORiZ2o2jWjA1A3/eahhpbVNYleF0HvXCEGZI2BFHhPRgIHXOdgCkdFJw9/twPS2
        aJe31NHnFmUSEWIpnfJlXW0bK2l3UZceAAOW6LjtTkb8mxwHTMOcAVUd3CUHH0d6
        gaqloSQqVUnpFgcG7D8F/SICCf5GQcFnS5DRlpsmpakjTBWi1+iFj827xHLXelN8
        pXhhgf0Nm4AJsl/AQpIlG9gSa60C2c2NdUKM0H+EKgGqGi6BsmXvkcHBKkGk3dCo
        aYh5lUHAFELXPb29jXJqb8YzN2PKq0wCkqOgyLwgr7LYa7xc9qA=
        =zkXN
        -----END PGP SIGNATURE-----
        """;
}
