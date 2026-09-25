# Arcade Library Manager 0.2.7

The media section is now Make themes, with Games and Theme settings tabs. It reads artwork and gameplay snaps already downloaded by LaunchBox, previews a theme, and generates selected missing themes. Character cutouts, animated borders, movement controls, and the 25–33 second duration range remain available.

Artwork download buttons, media-provider catalogs/imports, EmuMovies login and password storage, media queues, and their maintenance stages are removed. Old account settings are ignored. Existing workflow history is retained; retired stages cannot execute when an older workflow is resumed. A changed settings snapshot requires a fresh reviewed selection.

Game installation, emulator updates, LaunchBox game synchronization, and bezel setup remain available. Bezels have their own sidebar page. No existing library artwork, themes, games, or bezel settings are removed by this update.

Rendering uses the available GPU video encoder automatically (NVIDIA, Intel, or AMD), with CPU fallback if hardware encoding cannot initialize. Static artwork is resized and blurred once per render and reused before the animation effects. CPU decoding, composition, and software encoding use bounded parallel threads; there is no Quiet mode. The selected encoder and render time are recorded beside each generated theme. The scene composition still uses CPU filters.

Validation includes hardware probing, automatic CPU fallback, cancellation, static-layer animation preservation, the simplified desktop controls, legacy settings, retired workflow-stage handling, local metadata matching, and the existing installation, LaunchBox, bezel, and real theme-rendering checks.
