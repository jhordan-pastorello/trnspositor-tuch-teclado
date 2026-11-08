#!/bin/bash
# Compartilhamento SMB para troca de arquivos com o Windows.
set -e

SHARE_DIR="/home/storage/Transferencia"
SMB_USER="storage"
SMB_PASS="Jhor@l1s"

mkdir -p "$SHARE_DIR"
chown "$SMB_USER:$SMB_USER" "$SHARE_DIR"

if ! dpkg -s samba >/dev/null 2>&1; then
    DEBIAN_FRONTEND=noninteractive apt-get install -y samba
fi

if ! grep -q "^\[Transferencia\]" /etc/samba/smb.conf; then
    cat >> /etc/samba/smb.conf <<'EOF'

[Transferencia]
   comment = Area de transferencia DeskLink
   path = /home/storage/Transferencia
   browseable = yes
   read only = no
   valid users = storage
   create mask = 0664
   directory mask = 0775
   force user = storage
EOF
fi

# senha do usuario SMB
printf '%s\n%s\n' "$SMB_PASS" "$SMB_PASS" | smbpasswd -a -s "$SMB_USER"

systemctl enable smbd
systemctl restart smbd
sleep 1

ufw allow from 192.168.1.0/24 to any port 445 proto tcp comment 'DeskLink-SMB' >/dev/null 2>&1 || true

echo "--- status ---"
systemctl is-active smbd
testparm -s 2>/dev/null | grep -A6 '\[Transferencia\]'
ls -ld "$SHARE_DIR"
