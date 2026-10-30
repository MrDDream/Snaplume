#!/bin/sh
# After installing or upgrading Snaplume: let the desktop see the new icon and the file associations.
gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor >/dev/null 2>&1 || true
update-desktop-database -q >/dev/null 2>&1 || true
exit 0
