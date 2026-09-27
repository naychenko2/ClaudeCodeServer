# Ubuntu 26.04 LTS → быстрый подъём Windows в KVM (raw disk passthrough)

Цель: минимальным числом шагов от чистой установки Ubuntu дойти до момента, когда та же самая нативная установка Windows (на MSI M560 2TB NVMe) грузится внутри QEMU/KVM через прямой проброс физического диска.

Железо: ASUS ROG Crosshair VIII Hero (X570), Ryzen 9 5900X, 64 ГБ RAM, 2× RTX 3090 с NVLink-мостом. Обе GPU остаются на хосте под Linux (vLLM TP=2) — в эту VM GPU **не** пробрасывается, дисплей виртуальный (SPICE/QXL). Диски: Windows на MSI M560 2TB NVMe (не трогаем при разметке Linux), два Kingston SA400 480GB SATA (D:/E: — тоже не трогаем), Linux ставится на отдельный новый NVMe.

## 0. Предпосылки (должно быть сделано ДО установки Linux)

Это отдельный чеклист из прошлого разговора — здесь только контрольный список, не разворачиваю:

- [ ] BitLocker на Windows-диске выключен (иначе VM не сможет прочитать раздел без ключа восстановления)
- [ ] Быстрый запуск (Fast Startup) и гибернация в Windows отключены — `powercfg /h off` в админ-PowerShell. Без этого NTFS остаётся в «грязном»/гибридном состоянии, и второй загрузчик (хоть VM, хоть повторная загрузка на голом железе) увидит несогласованную файловую систему
- [ ] Windows последний раз выключена штатным Shutdown, не просто закрыта крышка/сон

Если что-то из этого не сделано — не продолжай, сначала закрой чеклист.

## 1. BIOS перед установкой Ubuntu

- `SVM Mode` — Enabled
- `IOMMU` — Enabled
- `ACS Enable` (или Auto) — Enabled
- `AER Cap.` — Enabled
- `Above 4G Decoding` — Enabled
- `Resizable BAR` — Enabled
- `Secure Boot` — временно Disabled (включишь обратно после того, как драйвер NVIDIA и всё остальное заведётся и стабилизируется — так меньше переменных при первом бринг-апе)

## 2. Установка Ubuntu 26.04 LTS

Ставишь **только на новый NVMe**. В разметке (`Something else` / ручной режим) — явно убедись, что в списке дисков установщика затронут ровно один диск, соответствующий новому NVMe по размеру. Диск с Windows (2TB MSI) и оба Kingston НЕ должны быть отмечены ни под что, даже под точку монтирования `/boot/efi` — им там не место, ESP для Linux создаёшь новый, отдельный от ESP Windows.

Boot loader (GRUB) ставь тоже на этот новый NVMe. После установки переключение между Windows/Linux — через boot menu материнки (F8 на ASUS) по выбору загрузочного диска, а не через GRUB os-prober — так GRUB вообще не будет трогать загрузчик Windows.

## 3. Первая загрузка Linux — драйвер и проверка NVLink

```bash
ubuntu-drivers devices
sudo ubuntu-drivers autoinstall   # поставит open-kernel-modules флейвор для Ampere
sudo reboot
```

После ребута:

```bash
nvidia-smi -pm 1
nvidia-smi topo -m        # ищи NV4 между GPU0 и GPU1, не PHB/PXB
nvidia-smi nvlink -s      # все линки Active
```

Если тут не так — дальше идти рано, сначала разбираемся с мостом/драйвером отдельно.

## 4. Стек виртуализации

```bash
egrep -c '(vmx|svm)' /proc/cpuinfo   # должно быть > 0

sudo apt update
sudo apt install -y qemu-kvm libvirt-daemon-system libvirt-clients \
    virtinst virt-manager ovmf swtpm swtpm-tools libosinfo-bin

sudo usermod -aG libvirt,kvm $USER
newgrp libvirt

sudo virsh net-start default 2>/dev/null
sudo virsh net-autostart default
```

Группа `libvirt`/`kvm` вступит в силу после перелогина (или `newgrp`, как выше, для текущей сессии).

## 5. Найти диск Windows безопасно (по стабильному ID, не по /dev/nvmeXn1)

Нумерация `/dev/nvme0n1` / `/dev/nvme1n1` **не гарантирована между перезагрузками** — на неё нельзя полагаться при выборе диска для проброса. Используй `by-id`:

```bash
lsblk -o NAME,SIZE,MODEL,SERIAL,MOUNTPOINT
ls -la /dev/disk/by-id/ | grep -i nvme
```

Найди запись, соответствующую MSI M560 2TB (по размеру ~1.8Ti и модели в выводе `lsblk`). Возьми **путь без суффикса `-partN`** — нужен весь диск целиком, не раздел. Дальше в этом файле обозначаю его как `WINDOWS_DISK_ID` — подставь реальное значение вида `/dev/disk/by-id/nvme-MSI_M560_...`.

Проверь, что диск нигде не примонтирован (колонка `MOUNTPOINT` в `lsblk` пустая для всех его партиций). Если Nautilus/Files автомонтировал что-то при подключении — отмонтируй перед следующим шагом:

```bash
sudo umount /dev/disk/by-id/WINDOWS_DISK_ID-part* 2>/dev/null
```

## 6. Создать VM поверх уже установленной Windows

Ключевые решения, почему так, а не иначе:

| Параметр | Значение | Почему |
|---|---|---|
| Шина диска | `sata` | У Windows нет драйвера virtio из коробки — с `bus=virtio` первая же загрузка встретит `INACCESSIBLE_BOOT_DEVICE`. SATA/AHCI и NVMe — inbox-драйверы Windows, работают сразу |
| Firmware | UEFI (OVMF) | Windows на голом железе ставилась в UEFI+GPT, BIOS/SeaBIOS её не увидит как загрузочную |
| CPU | `host-passthrough` | Ближе всего к реальному железу, меньше сюрпризов с лицензионными/аппаратными проверками |
| vTPM | `swtpm`, версия 2.0 | Windows 11 ожидает TPM 2.0, на реальном железе он был (fTPM), в VM эмулируем — иначе часть функций Windows будет ругаться |
| GPU | не пробрасывается | Обе 3090 остаются на хосте под NVLink/vLLM, решение уже принято ранее в этом разговоре |

```bash
sudo virt-install \
  --name win-passthrough \
  --memory 16384 \
  --vcpus 8 \
  --cpu host-passthrough \
  --os-variant win11 \
  --boot uefi \
  --disk path=/dev/disk/by-id/WINDOWS_DISK_ID,bus=sata,cache=none,io=native \
  --network network=default,model=virtio \
  --graphics spice \
  --video qxl \
  --channel spicevmc \
  --tpm backend.type=emulator,backend.version=2.0,model=tpm-crb \
  --features hyperv_relaxed=on,hyperv_vapic=on,hyperv_spinlocks=on,hyperv_spinlocks_retries=8191 \
  --noautoconsole
```

Если `--os-variant win11` ругается, что такого варианта нет в базе `osinfo` — проверь доступные:

```bash
osinfo-query os | grep -i win
```

и подставь то, что нашлось (`win10` тоже подойдёт функционально, влияет только на дефолтные подсказки по железу, не на факт загрузки).

`--memory`/`--vcpus` — подбери под то, сколько реально не жалко забрать у хоста на время сессии в Windows (хост тем временем продолжает греть LLM на обеих 3090, память и ядра CPU делятся с ним).

## 7. Первый запуск

```bash
virsh start win-passthrough
virt-viewer win-passthrough    # либо просто открой virt-manager — увидишь VM в списке
```

Чего ожидать:
- Windows должна дойти до экрана входа без запроса ключа восстановления BitLocker (если пункт 0 выполнен)
- После входа скорее всего увидишь «Windows не активирована» — это ожидаемо: жёсткая смена железа (виртуальная материнка вместо реальной) сбрасывает привязку цифровой лицензии. Не повод паниковать, чинится через `slmgr /ato` при залогиненном Microsoft-аккаунте либо просто оставь как есть, если это разовая проверка
- Часть устройств в Диспетчере устройств будет с жёлтым восклицательным знаком (виртуальная сетевая карта и т.п. — VirtIO без гостевых драйверов). Для базового «зашёл и посмотрел» это не критично; если нужна нормальная сеть в VM — установи VirtIO-drivers (`virtio-win` ISO, подключается вторым CD-ROM, драйвера ставятся из Диспетчера устройств)

## 8. Железное правило на будущее

**Никогда не запускай VM, пока Windows может быть смонтирована/грузиться на голом железе, и наоборот.** Один и тот же NTFS-том, открытый на запись с двух сторон одновременно — гарантированное повреждение файловой системы. Перед стартом VM всегда убеждайся, что нативная Windows была выключена штатно (не спала, не гибернировала), а перед повторной загрузкой на голом железе — что VM корректно остановлена:

```bash
virsh shutdown win-passthrough   # НЕ destroy — дождись полного завершения
virsh list --all                 # статус должен быть "shut off"
```

## 9. Быстрая шпаргалка для повторных сессий

```bash
# Проверить, что мост и обе карты живы перед тем как забирать ресурсы под VM
nvidia-smi topo -m

# Поднять VM
virsh start win-passthrough && virt-viewer win-passthrough

# Корректно погасить
virsh shutdown win-passthrough
virsh list --all
```
