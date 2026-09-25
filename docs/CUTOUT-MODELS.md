# Local transparent theme cutouts

The renderer can prepare transparent foreground PNGs from local artwork, then animate those layers independently of the fanart background and gameplay snap. Existing transparent PNGs are accepted without installing a model. A solid rectangular PNG is not treated as a cutout merely because its file supports alpha.

## Install and storage

In Make themes, use **Download cutout models** once. The two CPU models total 354,717,941 bytes (about 340 MiB). This is an explicit download from the rembg project's GitHub release. Rendering does not initiate model downloads and never uploads artwork or account credentials. The portable application includes ONNX Runtime, but does not bundle model weights or FFmpeg.

The installer downloads into temporary files under `<CachePath>\Models`, verifies the pinned file sizes and SHA-256 hashes, then atomically installs each completed model. An interrupted model download is discarded; an already verified model is reused. If the second download is interrupted, the first model remains usable. The UI saves the returned `isnet-general-use.onnx` path after the full install completes. A damaged installed model can be replaced by running the installer again.

Automatic discovery checks the configured cutout model path, then `Tools\Models\isnet-general-use.onnx` beside the application, then `<CachePath>\Models\isnet-general-use.onnx`. If the configured path is a directory, the default model filename is appended. The illustrated-character model must be named `isnet-anime.onnx` beside the general model. A missing configured path does not silently select another installation. Checksum validation runs before model use.

| Model | Bytes | Pinned SHA-256 | Source and upstream checksum |
| --- | ---: | --- | --- |
| IS-Net general use | 178,648,008 | `60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a` | [rembg download](https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx), [rembg session implementation](https://github.com/danielgatis/rembg/blob/main/rembg/sessions/dis_general_use.py), published MD5 `fc16ebd8b0c10d971d3513d564d01e29` |
| IS-Net illustrated characters | 176,069,933 | `f15622d853e8260172812b657053460e20806f04b9e05147d49af7bed31a6e99` | [rembg download](https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-anime.onnx), [rembg session implementation](https://github.com/danielgatis/rembg/blob/main/rembg/sessions/dis_anime.py), published MD5 `6f184e756bb3bd901c8849220a83e38e` |

The SHA-256 values were calculated from downloaded model files whose MD5 values matched the rembg publisher's checksums. Downloads with changed bytes are rejected instead of silently adopting new weights.

## Windows runtime prerequisite

ONNX Runtime's [Windows installation requirements](https://onnxruntime.ai/docs/install/#requirements) include Microsoft's Visual C++ runtime (2019 or newer). If automatic extraction reports that it cannot load the inference runtime, install the official [Visual C++ v14 Redistributable, x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) and restart the app. The portable application does not silently install system runtimes. Imported transparent PNG cutouts do not require ONNX inference.

## Local processing and limits

Processing is sequential across render jobs and bounded to four CPU inference threads. FFmpeg decodes local images to RGBA, artwork is limited to 1,800 pixels on its longest side, and the models process a 1,024-pixel square input with their published normalization. Cancellation terminates the current ONNX inference or FFmpeg process. Generated PNGs and rejected-source results are cached by source content, model availability, and algorithm version.

The general model runs first. If it does not produce a usable subject and the second model is installed, the illustrated-character model is tried. Empty masks, mostly opaque frames, broad screenshot panels and rectangular detections are rejected. Small disconnected fragments are suppressed, accepted subjects are cropped to their transparent bounds, and imported PNGs retain the artist's composition rather than splitting letters or body parts.

Automatic segmentation does not repaint artwork. Text or logos overlapping a character can remain attached, and cluttered posters may still produce no usable subject. The renderer then continues with its other artwork and logs the missing cutout; an imported transparent PNG gives exact creative control. Gameplay screenshots are excluded from automatic artwork discovery because their interface and backgrounds can become false foregrounds.

The actual Anime Champ advertisement flyer was tested locally: the general model did not yield a usable subject; the illustrated model extracted its central five-character group with transparent surroundings. Some printed text and the overlapping logo remain. A gameplay screenshot that initially looked like a rectangular cutout was rejected after mask quality checks were tightened. These are representative checks, not a guarantee for every game.

## Provenance and notices

- [Highly Accurate Dichotomous Image Segmentation / IS-Net](https://github.com/xuebinqin/DIS) is the official general model project by Xuebin Qin and collaborators. Its repository publishes [Apache License 2.0](licenses/IS-Net-APACHE-2.0-LICENSE.txt). The dataset has separate terms and is not downloaded by this application.
- [SkyTNT Anime Segmentation](https://github.com/SkyTNT/anime-segmentation) supplies the illustrated-character model project and publishes [Apache License 2.0](licenses/Anime-Segmentation-APACHE-2.0-LICENSE.txt).
- [rembg](https://github.com/danielgatis/rembg) distributes the ONNX exports and documents their preprocessing and expected hashes. Its [MIT license](licenses/rembg-MIT-LICENSE.txt) is preserved; model weights retain their upstream terms independently of rembg's code license.
- [Microsoft.ML.OnnxRuntime 1.30.0](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.30.0) provides CPU inference. Its [MIT license](licenses/ONNX-Runtime-1.30.0-LICENSE.txt) and complete [third-party notices](licenses/ONNX-Runtime-1.30.0-ThirdPartyNotices.txt) are included with the portable app. The Windows x64 native runtime is supplied through the NuGet package; no Python installation or online account is required.

See [Third-party notices](THIRD-PARTY-NOTICES.md) for the other bundled components. Artwork, game logos, and videos retain their respective rights.
