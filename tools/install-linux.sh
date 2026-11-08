#!/usr/bin/env bash
# Instala/atualiza o daemon DeskLink no Linux (usuario, sem root).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"

echo "==> copiando daemon"
mkdir -p ~/.local/share/desklink ~/.config/systemd/user
cp "$root/daemon/desklink_daemon.py" ~/.local/share/desklink/desklink_daemon.py
cp "$root/daemon/clipwatch.py" ~/.local/share/desklink/clipwatch.py
cp "$here/desklink.service" ~/.config/systemd/user/desklink.service

echo "==> recarregando systemd e (re)iniciando"
systemctl --user daemon-reload
systemctl --user enable desklink.service
systemctl --user restart desklink.service
sleep 2
systemctl --user --no-pager status desklink.service | head -6

cat <<'EOF'

OK. Falta uma vez (com sudo), se ainda nao fez:
  sudo ./tools/setup-uinput.sh

Dependencias (Ubuntu):
  sudo apt install python3-gi gir1.2-gtk-3.0 wl-clipboard

Firewall (ufw), se estiver ativo:
  sudo ufw allow from 192.168.1.0/24 to any port 47810 proto tcp comment DeskLink-TCP
  sudo ufw allow from 192.168.1.0/24 to any port 47811 proto udp comment DeskLink-Discovery

Logs:  journalctl --user -u desklink -f
EOF
