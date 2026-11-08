# DeskLink

**Mouse e teclado compartilhados entre Windows e Linux (GNOME/Wayland), como se fossem um só computador.**

Encoste o ponteiro no topo da tela do notebook: o teclado e o touchpad passam a controlar o
servidor Linux. Volte descendo até a borda inferior (ou com `Ctrl+Alt+Espaço`). A área de
transferência vai junto — texto **e imagens** (aquele print do GNOME que você cola com `Ctrl+V`).
E a pasta `Z:` mapeada no Windows é o mesmo diretório do Linux: arraste arquivos entre os dois.

Sem portal, sem diálogo de permissão, sem root em runtime. Sobe sozinho no login dos dois lados.

```
┌──────────────── Windows (notebook) ────────────────┐        ┌──── Linux (servidor GNOME) ────┐
│  DeskLink.exe  (invisível, tarefa de logon)        │        │  desklink.service (systemd)     │
│  ├─ WH_MOUSE_LL / WH_KEYBOARD_LL  (captura)        │  TCP   │  ├─ TCP 47810 + TLS             │
│  ├─ Raw Input                     (deltas)         │ ─────► │  ├─ /dev/uinput: tablet + kbd   │
│  ├─ UDP 47811 discovery                            │  4.7k  │  ├─ clipboard (wl-copy/paste)   │
│  └─ clipboard texto + PNG                          │  TLS   │  └─ UDP 47811 discovery         │
└────────────────────────────────────────────────────┘        └─────────────────────────────────┘
```

---

## Índice

- [Como funciona](#como-funciona)
- [Requisitos](#requisitos)
- [Instalação](#instalação)
- [Uso](#uso)
- [Configuração](#configuração)
- [Protocolo](#protocolo)
- [Solução de problemas](#solução-de-problemas)
- [Limitações](#limitações)
- [Desinstalar](#desinstalar)
- [Estrutura do projeto](#estrutura-do-projeto)

---

## Como funciona

### Captura (Windows)
Um processo **sem interface** (`WinExe`, sem janela e sem ícone de bandeja) roda com privilégio
máximo, iniciado por uma tarefa agendada no logon:

- hooks de baixo nível (`WH_MOUSE_LL` / `WH_KEYBOARD_LL`) interceptam e **suprimem** o input local
  quando o controle está no Linux;
- **Raw Input** fornece os deltas reais do dispositivo — funciona inclusive durante arrasto/captura,
  quando `SetCursorPos` é ignorado pelo Windows;
- o cursor local é estacionado numa janela 2×2 com cursor nulo (`ClipCursor`), então ele não
  aparece nem vaza para a tela errada.

### Injeção (Linux)
Um daemon Python cria dispositivos **virtuais no kernel** via `/dev/uinput`:

| Dispositivo | Tipo | Por quê |
|---|---|---|
| `DeskLink Virtual Tablet` | ponteiro **absoluto** (`ABS_X`/`ABS_Y`) + botões + scroll | posição exata, **sem aceleração** e sem deriva |
| `DeskLink Virtual Keyboard` | teclado (`EV_KEY`) | keycodes físicos (evdev), independente de layout |

O GNOME trata os dois como hardware real. Não há portal, screencast, X11 ou permissão a pedir.

### Troca de telas
- Borda configurável (`top` por padrão). Permanecer `SwitchDelayMs` na borda troca o controle.
- A posição na borda é mapeada **proporcionalmente** e o movimento é escalado por
  `resolução_remota / resolução_local` (ex.: 2560/1920 = 1,333).
- Para voltar, é preciso permanecer ~180 ms na borda oposta (evita cortar arrasto).
- `Ctrl+Alt+Espaço` alterna a qualquer momento.

### Rede
- **Descoberta**: o cliente faz broadcast UDP em `47811`; o daemon responde com nome, porta e
  resolução real.
- **Transporte**: TCP `47810` com **TLS**. Certificado autoassinado, com **pinning (TOFU)** na
  primeira conexão — o fingerprint fica salvo em `config.json` e é validado nas seguintes.

### Clipboard
- Texto: `CF_UNICODETEXT` ↔ `text/plain;charset=utf-8`.
- Imagens: PNG ↔ `image/png` (é o formato que o print do GNOME usa).
- Polling de 300 ms dos dois lados; hash evita eco.

---

## Requisitos

| Lado | Requisitos |
|---|---|
| **Windows** | Windows 10 v1809+ ou 11 · [.NET 10 Runtime](https://dotnet.microsoft.com/download) (ou SDK para compilar) |
| **Linux** | GNOME em **Wayland** (testado no Ubuntu 26.04) · Python 3.8+ · `wl-clipboard` · acesso a `/dev/uinput` (grupo `input`, configurado uma vez) |

Rede: as duas máquinas na mesma LAN (ou Tailscale/VPN), portas `47810/tcp` e `47811/udp`.

---

## Instalação

### 1. Linux

```bash
git clone https://github.com/jhordan-pastorello/trnspositor-tuch-teclado.git
cd trnspositor-tuch-teclado

sudo apt install wl-clipboard
./tools/install-linux.sh        # daemon + systemd --user
sudo ./tools/setup-uinput.sh    # UMA VEZ: módulo uinput, regra udev, grupo input
```

O `setup-uinput.sh` faz:

```bash
cp tools/60-desklink-uinput.rules /etc/udev/rules.d/
cp tools/desklink-modules.conf    /etc/modules-load.d/
modprobe uinput
usermod -aG input "$USER"
udevadm control --reload-rules && udevadm trigger --name-match=uinput
chgrp input /dev/uinput && chmod 0660 /dev/uinput
```

> Depois do `usermod` vale fazer logout/login para o grupo valer em todas as sessões.
> O script já aplica uma ACL no device para funcionar imediatamente.

Firewall (se usar `ufw`):

```bash
sudo ufw allow from 192.168.1.0/24 to any port 47810 proto tcp comment DeskLink-TCP
sudo ufw allow from 192.168.1.0/24 to any port 47811 proto udp comment DeskLink-Discovery
```

### 2. Windows

```powershell
git clone https://github.com/jhordan-pastorello/trnspositor-tuch-teclado.git
cd trnspositor-tuch-teclado

# PowerShell como Administrador
.\tools\install-windows.ps1
```

O script publica o binário em `%LOCALAPPDATA%\DeskLink\app` e registra a tarefa
`DeskLink` (AtLogOn, `RunLevel Highest`, reinício automático). Nada abre na tela.

> **Por que tarefa agendada e não serviço?** Serviços rodam na sessão 0 e **não** conseguem
> capturar mouse/teclado do usuário. A tarefa no logon roda elevada dentro da sessão interativa,
> que é o único lugar de onde os hooks funcionam — e ainda permite controlar janelas elevadas (UAC).

---

## Uso

| Ação | Como |
|---|---|
| Ir para o Linux | encoste no **topo** da tela do notebook e segure ~250 ms |
| Voltar para o Windows | desça até a **borda inferior** do Linux e **empurre ~40 px** além dela (ou `Ctrl+Alt+Espaço`) |
| Alternar a qualquer momento | `Ctrl+Alt+Espaço` |
| Bordas diferentes | `Edge` no `config.json` |

Ao trocar, o cursor do Windows "desaparece" (fica numa janela invisível) e o teclado passa a
digitar no Linux — inclusive acentos, `Ctrl+C/V`, `Alt+Tab` e atalhos do desktop.

A margem de 40 px na volta existe de propósito: encostar na borda durante um arrasto não corta
mais a seleção; para voltar é preciso empurrar o ponteiro para fora.

---

## Arquivos

O servidor expõe uma pasta SMB e o instalador mapeia no Windows como **`Z:`**:

```
Z:\                    ↔  /home/storage/Transferencia  (no Linux)
```

Arraste arquivos entre o Explorer e o Nautilus normalmente — é o mesmo diretório, sem cópia
manual e com a velocidade da rede local.

Configuração (no servidor, uma vez):

```bash
sudo ./tools/setup-samba.sh
```

No Windows, para remapear (outro PC, ou após trocar senha):

```powershell
net use Z: \\192.168.1.30\Transferencia /user:storage <senha> /persistent:yes
```

Alternativa sem SMB: o **LocalSend** (instalado nos dois lados) serve para envios pontuais —
abra o app, arraste o arquivo e o outro lado aceita o pareamento.

---

## Configuração

`%LOCALAPPDATA%\DeskLink\config.json` (criado no primeiro start):

| Campo | Padrão | Descrição |
|---|---|---|
| `Edge` | `top` | Borda que leva ao Linux: `top`, `bottom`, `left`, `right` |
| `SwitchDelayMs` | `250` | Tempo parado na borda antes de trocar |
| `Speed` | `1.0` | Multiplicador extra de velocidade (1.0 = proporcional) |
| `Enabled` | `true` | Liga/desliga a troca de telas |
| `ServerHost` | `""` | IP fixo do servidor (vazio = descoberta automática) |
| `ServerPort` | `47810` | Porta TCP |
| `DiscoveryPort` | `47811` | Porta UDP de descoberta |
| `ToggleHotkeyEnabled` | `true` | Habilita `Ctrl+Alt+Espaço` |
| `ServerFingerprint` | `""` | Preenchido sozinho no primeiro contato (TOFU) |

Depois de editar: `Stop-ScheduledTask DeskLink; Start-ScheduledTask DeskLink`.

**Logs**
- Windows: `%LOCALAPPDATA%\DeskLink\desklink.log`
- Linux: `journalctl --user -u desklink -f`

---

## Protocolo

Cada mensagem: `[u8 tipo][u32 tamanho LE][payload]`.

| Tipo | Nome | Payload | Direção |
|---:|---|---|---|
| 1 | `Hello` | JSON `{v,name,w,h}` | W → L |
| 2 | `HelloAck` | JSON `{v,name,w,h}` | L → W |
| 3 | `Pointer` | `i32 x, i32 y` (absoluto, espaço do servidor) | W → L |
| 4 | `Button` | `u8 botão (1=esq,2=meio,3=dir,4=extra), u8 down` | W → L |
| 5 | `Wheel` | `i32 dx, i32 dy` (unidades de 120, igual evdev) | W → L |
| 6 | `Key` | `u16 keycode evdev, u8 down` | W → L |
| 7 | `Leave` | — (libera teclas/botões) | W → L |
| 8 | `Heartbeat` | — | W → L |
| 9 | `Clipboard` | UTF-8 | ambos |
| 10 | `ClipboardImage` | PNG | ambos |

Handshake:

```
cliente                                   servidor
  │ ── TLS (certificado autoassinado) ──►   │
  │ ── Hello {w,h} ─────────────────────►   │
  │ ◄──────────────── HelloAck {w,h} ──    │
  │ ── Pointer/Button/Wheel/Key ────────►   │  → /dev/uinput
  │ ── Clipboard / ClipboardImage ──────►   │  → wl-copy
  │ ◄──────────────── Clipboard(Image) ──   │  ← wl-paste
```

---

## Solução de problemas

| Sintoma | Causa provável / solução |
|---|---|
| "servidor não encontrado" | firewall (47810/47811), sub-redes diferentes ou Wi-Fi com isolamento. Defina `ServerHost` com o IP |
| Conecta e cai a cada ~8 s | versão antiga do daemon/cliente (protocolo `u16`). Atualize os dois |
| Ponteiro não aparece no Linux | `/dev/uinput` sem permissão → `sudo ./tools/setup-uinput.sh` e relogar |
| Ponteiro aparece em canto/posição errada | dispositivo relativo antigo ainda ativo → `systemctl --user restart desklink` |
| Teclado com layout trocado | o layout do Linux deve ser o mesmo do Windows (`pt` nos dois) |
| Colar imagem não funciona | precisa de `wl-clipboard` e o app de destino deve aceitar PNG no clipboard |
| Janelas UAC não recebem input | a tarefa precisa de `RunLevel Highest` (o instalador já configura) |

Diagnóstico rápido:

```bash
# Linux
systemctl --user status desklink
journalctl --user -u desklink -n 50
ls -l /dev/uinput
python3 -c "import os; os.close(os.open('/dev/uinput', os.O_WRONLY)); print('uinput ok')"

# Windows
Get-ScheduledTask -TaskName DeskLink | Select State
Get-Content "$env:LOCALAPPDATA\DeskLink\desklink.log" -Tail 30
```

---

## Limitações

- **Gestos de 3/4 dedos** do touchpad são consumidos pelo Windows antes de qualquer aplicativo —
  não há como encaminhá-los ao Linux. Movimento, clique, scroll e teclado funcionam.
- **Clipboard**: apenas texto e PNG (imagens).
- Arrastar/selecionar usa Raw Input (deltas do dispositivo); para voltar ao Windows é preciso
  empurrar ~40 px além da borda.
- Keycodes são físicos (evdev): o layout do Linux deve ser o mesmo do Windows.
- Um cliente por vez (um notebook controlando um servidor).

---

## Desinstalar

**Windows**

```powershell
Unregister-ScheduledTask -TaskName DeskLink -Confirm:$false
Remove-Item "$env:LOCALAPPDATA\DeskLink" -Recurse -Force
```

**Linux**

```bash
systemctl --user disable --now desklink
rm -rf ~/.local/share/desklink ~/.config/systemd/user/desklink.service
sudo rm -f /etc/udev/rules.d/60-desklink-uinput.rules /etc/modules-load.d/desklink-uinput.conf
```

---

## Estrutura do projeto

```
DeskLink/
├─ src/DeskLink.Windows/          # agente Windows (C#/.NET 10, WinExe, sem UI)
│  ├─ Program.cs                  # single instance + message loop
│  ├─ InputRouter.cs              # hooks, borda, estado remoto
│  ├─ RawInputWindow.cs           # WM_INPUT → deltas do dispositivo
│  ├─ HiddenCursor.cs             # janela 2×2 com cursor nulo
│  ├─ LinkClient.cs               # descoberta UDP, TLS, reconexão
│  ├─ ClipboardSync.cs            # texto + PNG (thread STA)
│  ├─ KeyMap.cs                   # scan code set 1 → evdev
│  ├─ Proto.cs                    # framing do protocolo
│  ├─ Config.cs / Log.cs / Native.cs
│  └─ app.manifest                # DPI PerMonitorV2
├─ daemon/desklink_daemon.py      # daemon Linux (uinput, TLS, discovery, clipboard)
├─ tools/
│  ├─ install-windows.ps1         # publica + tarefa agendada
│  ├─ install-linux.sh            # daemon + systemd --user
│  ├─ setup-uinput.sh             # udev + módulo + grupo (sudo, uma vez)
│  ├─ desklink.service            # unit systemd --user
│  ├─ 60-desklink-uinput.rules    # regra udev
│  └─ desklink-modules.conf       # carrega uinput no boot
└─ README.md
```

### Compilar do zero

```powershell
dotnet publish src/DeskLink.Windows -c Release -o "$env:LOCALAPPDATA\DeskLink\app"
```

```bash
./tools/install-linux.sh
```

---

Feito para uso diário numa mesa com notebook Windows + servidor Linux. Se algo quebrar,
o log dos dois lados diz exatamente o que aconteceu.
