#!/bin/bash
set -e
cp /tmp/60-desklink-uinput.rules /etc/udev/rules.d/60-desklink-uinput.rules
cp /tmp/desklink-modules.conf /etc/modules-load.d/desklink-uinput.conf
modprobe uinput || true
usermod -aG input storage
udevadm control --reload-rules
udevadm trigger --name-match=uinput --action=add || true
sleep 1
chgrp input /dev/uinput
chmod 0660 /dev/uinput
setfacl -m u:storage:rw /dev/uinput
echo "--- /dev/uinput ---"
ls -l /dev/uinput
getfacl -p /dev/uinput 2>/dev/null | tail -3
echo "--- grupos ---"
id storage
