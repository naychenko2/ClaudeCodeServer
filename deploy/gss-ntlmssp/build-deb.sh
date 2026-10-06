#!/usr/bin/env bash
# Сборка gss-ntlmssp с патчем ntlm-key-exch-requires-sign-seal.diff в контейнере (хост не трогаем).
# Результат — .deb в каталоге вывода (по умолчанию ./out). Версия: <версия Ubuntu>+ccsN.
# Использование: build-deb.sh [каталог-вывода] [--test]
#   --test — после сборки прогнать test/run-test.sh на оригинальной и на собранной .so.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/out"; RUN_TEST=0
for a in "$@"; do
  if [[ "$a" == "--test" ]]; then RUN_TEST=1; else OUT="$(realpath -m "$a")"; fi
done
UBUNTU_VER="1.2.0-1build5"        # версия пакета на хосте: dpkg -l gss-ntlmssp
CCS_SUFFIX="+ccs1"
POOL=https://archive.ubuntu.com/ubuntu/pool/universe/g/gss-ntlmssp
IMAGE=ubuntu:26.04

mkdir -p "$OUT"
docker run --rm -v "$HERE:/in:ro" -v "$OUT:/out" "$IMAGE" bash -euo pipefail -c "
sed -i 's/^Types: deb\$/Types: deb deb-src/' /etc/apt/sources.list.d/ubuntu.sources
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq build-essential devscripts dpkg-dev fakeroot curl ca-certificates >/dev/null
mkdir /b && cd /b
for f in gss-ntlmssp_1.2.0-1build5.dsc gss-ntlmssp_1.2.0-1build5.debian.tar.xz gss-ntlmssp_1.2.0.orig.tar.gz; do curl -fsSO $POOL/\$f; done
dpkg-source -x gss-ntlmssp_$UBUNTU_VER.dsc >/dev/null
cd gss-ntlmssp-1.2.0
cp /in/ntlm-key-exch-requires-sign-seal.diff debian/patches/
echo ntlm-key-exch-requires-sign-seal.diff >> debian/patches/series
dch --force-distribution -D resolute -v $UBUNTU_VER$CCS_SUFFIX 'Локальная сборка CCS: KEY_EXCH без SIGN/SEAL не ведёт к расшифровке EncryptedRandomSessionKey (MS-NLMP 3.1.5.1.2, 3.2.5.1.2)'
DEBIAN_FRONTEND=noninteractive apt-get build-dep -y -qq . >/dev/null
dpkg-buildpackage -us -uc -b >/dev/null
cp /b/*.deb /out/
"
echo "Готово: $(ls "$OUT"/gss-ntlmssp_*.deb)"

if [[ $RUN_TEST == 1 ]]; then
  docker run --rm -v "$HERE/test:/t:ro" -v "$OUT:/out:ro" "$IMAGE" bash -euo pipefail -c '
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq gcc libkrb5-dev libgssapi-krb5-2 python3 gss-ntlmssp >/dev/null
mkdir /p && dpkg-deb -x /out/gss-ntlmssp_*+ccs*_amd64.deb /p
cp -r /t /w && cd /w
echo "=== оригинал"; ./run-test.sh /usr/lib/x86_64-linux-gnu/gssntlmssp/gssntlmssp.so
echo "=== патч";     ./run-test.sh /p/usr/lib/x86_64-linux-gnu/gssntlmssp/gssntlmssp.so'
fi
