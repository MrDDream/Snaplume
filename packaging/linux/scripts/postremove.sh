#!/bin/sh
# After removing Snaplume: forget its icon and file associations.
# The user's settings (~/.config/Snaplume) and captures are left untouched.
gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor >/dev/null 2>&1 || true
update-desktop-database -q >/dev/null 2>&1 || true
exit 0
