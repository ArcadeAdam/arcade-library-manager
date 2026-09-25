# Arcade Library Manager 0.2.4

The previous theme composition forced gameplay into a wide padded box and enlarged a screenshot behind it. Fanart now uses a smaller left/right gameplay window at its source aspect ratio, a luminous animated border, full fanart or a soft artwork-derived backdrop, floating transparent foreground art, and an independent logo layer. Cinema retains a larger screen; Artwork omits the screen. Motion can be adjusted or disabled.

Local general and illustrated-character cutout models extract foreground from available flyers and art, with cached PNG outputs. Download the verified model pair once in Media studio; model files are not bundled in the portable ZIP. Rejected masks fall back to artwork. Imported transparent PNGs and background/logo overrides are supported per game. Automatic extraction does not reconstruct artwork hidden behind printed text.

Media studio now selects one preferred existing LaunchBox identity for each game, so retained alternate TeknoParrot profiles do not create orphan themes. Existing themes are preserved; use Render preview to compare the new composition before creating missing videos.

The sidebar is unnumbered, Install plan is renamed Install games, and the tagline has been removed.

Validation covers decoded gameplay aspect ratio and corner preservation, luminous animated border pixels, transparent foreground visibility/motion/bounds, left/right selection, zero-motion/static fallback, source and existing-video preservation, game identity selection and per-game layer discovery. Full integration and portable startup/UI/package checks are recorded in the release manifest. The Anime Champ preview was rendered from the user's actual local flyer, logo and gameplay snap using local cutout inference; no game-specific art is included in the shared packages.
