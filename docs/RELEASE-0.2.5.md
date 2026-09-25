# Arcade Library Manager 0.2.5

Character cutouts now bounce farther and faster while the gameplay window stays fixed. Horizontal travel is approximately 70% greater and the animation cycle about twice as fast. The vertical movement is an upward bounce with a quicker return, with a slightly stronger, faster tilt. Movement continues to follow the existing 0–100 setting; zero stops it.

Foreground placement is bounded to the available artwork area and the canvas so the stronger movement does not introduce clipped edges. Existing generated or curated themes are preserved; use Render preview to compare the updated motion before generating missing themes.

Validation covers decoded foreground travel, direction reversal, bounds, stationary gameplay, zero-motion behavior, Cinema/left-right/aspect-ratio handling and the existing integration suite. Release artifacts include the portable application and source ZIP; installed cutout models and the existing settings directory are reused.
