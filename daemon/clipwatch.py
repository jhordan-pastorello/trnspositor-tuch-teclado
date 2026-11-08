#!/usr/bin/env python3
"""Avisa o daemon quando o clipboard muda (evento do GTK, sem polling).

No GNOME o `wl-paste --watch` nao funciona (precisa do protocolo wlroots);
o sinal `owner-change` do GTK3 existe em qualquer compositor.
"""
import sys

import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk

clip = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)


def on_owner_change(_clipboard, _event):
    print("changed", flush=True)


clip.connect("owner-change", on_owner_change)
print("ready", flush=True)
Gtk.main()
