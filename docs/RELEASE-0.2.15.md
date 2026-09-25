# Arcade Library Manager 0.2.15

Shared-ROM failures now list the required game/parent/device ZIPs and explain how to add missing companions and rebuild the plan. The installer continues to require verified ROM contents; missing companions are not silently skipped. Game archives can lack separate support ZIPs, which must be available in a configured ROM source. Completed downloads remain reusable.

A retry that reuses a CHD inside the app's staging folder now registers its final destination after the folder is committed. Existing unowned destination folders are flagged during planning, preserving their files; exact matching existing files can still be reused.

The Emumovies snap scrapper page now has Download EmuMovies Sync beside Open Sync / login. It opens the official download page without running an installer. Exported reports use the actual application build version and include the profile identities and paths behind the invalid-path count.

Validation includes a synthetic missing-device failure followed by a successful retry from retained staging, valid final CHD paths, companion ZIP placement, CRC/SHA rejection, embedded dependencies, source/control preservation, existing-destination review, and published desktop checks at normal and minimum size. No support ROMs are included in the app distribution.
