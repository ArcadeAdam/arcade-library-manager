using System.Globalization;
using System.Text;

namespace ArcadeLibraryManager.Core;

internal sealed record ThemeScene(string Graph, ThemeScreenBounds? Screen, string BackgroundTreatment, int CutoutCount);
public sealed record ThemeScreenBounds(int X, int Y, int Width, int Height, int Border);

internal static class ThemeComposition
{
    private static string N(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
    private static int E(double value) => Math.Max(2, ((int)Math.Round(value) / 2) * 2);

    public static ThemeScene Build(AppSettings settings, MediaAssets assets, int width, int height, int fps, int duration,
        int background, ThemeReferenceInfo? backgroundInfo, int snap, ThemeReferenceInfo? snapInfo, int logo,
        IReadOnlyList<int> cutouts, string staging)
    {
        var graph = new StringBuilder();
        // Decode/resize/blur static layers once; replay that frame before time-dependent effects.
        var cachedFrame = $"loop=loop=-1:size=1:start=0,setpts=N/({fps}*TB)";
        var motion = Math.Clamp(settings.ThemeMotionStrength, 0, 100) / 100d;
        var side = settings.ThemeVideoSide.Equals("Left", StringComparison.OrdinalIgnoreCase) ? "Left" : "Right";
        var layout = settings.ThemeLayout == "Cinema" ? "Cinema" : "Fanart";
        bool hasScreen = snap >= 0;
        ThemeScreenBounds? screen = hasScreen ? MeasureScreen(width, height, layout, side, snapInfo) : null;
        var artSpan = screen is null ? width : side == "Right" ? screen.X : width - screen.X - screen.Width;
        var artCenter = screen is null ? width * .5 : side == "Right" ? artSpan * .5 : width - artSpan * .5;
        string backgroundTreatment;
        // Portrait flyers and screenshots are texture sources, not artwork to crop into giant faces.
        // Only wide fanart is displayed edge-to-edge with a small bounded drift.
        var wideFanart = background >= 0 && backgroundInfo is { Height: > 0 } && backgroundInfo.Width / (double)backgroundInfo.Height >= 1.35
            && assets.BackgroundKind is not ("Screenshot" or "Flyer" or "Box" or "Box front");
        if (wideFanart)
        {
            var bw = E(width * 1.025); var bh = E(height * 1.025);
            graph.Append($"[{background}:v]scale={bw}:{bh}:force_original_aspect_ratio=increase,{cachedFrame},crop={width}:{height}:x='(iw-ow)/2+{N(width * .008 * motion)}*sin(t*0.19)':y='(ih-oh)/2+{N(height * .008 * motion)}*cos(t*0.17)',setsar=1,eq=brightness=-0.04:saturation=1.08[bg];");
            backgroundTreatment = "Wide fanart with bounded drift";
        }
        else if (background >= 0)
        {
            // Blur at a small intermediate size. No legible, over-scaled screenshot survives at the edges.
            graph.Append($"[{background}:v]scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2:color=0x192047,gblur=sigma=14,scale={width}:{height},setsar=1,eq=brightness=-0.12:saturation=1.35,{cachedFrame}[bg];");
            backgroundTreatment = "Soft artwork color backdrop";
        }
        else
        {
            graph.Append($"color=c=0x111a37:s={width}x{height}:r={fps}:d={duration}[bg];");
            backgroundTreatment = "Arcade color backdrop";
        }
        var current = "bg";
        // With no usable subject mask, present the complete flyer rather than pretend it is a cutout.
        if (background >= 0 && cutouts.Count == 0 && !wideFanart && assets.BackgroundKind is "Flyer" or "Box" or "Box front")
        {
            var maxW = E(hasScreen ? Math.Min(width*.40,artSpan*.82) : width*.74); var maxH = E(height * .69);
            graph.Append($"[{background}:v]scale={maxW}:{maxH}:force_original_aspect_ratio=decrease,format=rgba,{cachedFrame}[poster];");
            graph.Append($"[{current}][poster]overlay=x='{N(artCenter)}-w/2+{N(5*motion)}*sin(t*0.4)':y='H-h-{E(height*.035)}+{N(4*motion)}*sin(t*0.35)':shortest=1[posterbg];");
            current = "posterbg";
        }
        // Independent foreground layers bounce within the artwork space; gameplay stays fixed.
        for (var i = 0; i < cutouts.Count; i++)
        {
            var maxW = E(hasScreen ? Math.Min(width*(i==0?.39:.22),artSpan*(i==0?.82:.46)) : width*.48);
            var maxH = E(height * (i == 0 ? .76 : .49));
            var cx = artCenter + (i == 0 ? 0 : artSpan * (side == "Right" ? .15 : -.15));
            var baseY = height * (i == 0 ? .955 : .985);
            var angle = N(motion * (i == 0 ? .035 : .042));
            var margin = Math.Max(4, E(width*.0125));
            var artStart = !hasScreen || side == "Right" ? 0 : width-artSpan;
            var artEnd = !hasScreen || side == "Left" ? width : artSpan;
            // Account for the transparent rotation padding so visible pixels retain an edge margin.
            var minX = artStart+margin-16; var maxX = artEnd-margin+16;
            var minY = margin-16; var maxY = height-margin+16;
            graph.Append($"[{cutouts[i]}:v]scale={maxW}:{maxH}:force_original_aspect_ratio=decrease,format=rgba,pad=iw+32:ih+32:16:16:color=black@0,{cachedFrame},rotate=angle='{angle}*sin(t*1.10+{i*2})':ow=iw:oh=ih:c=none[subject{i}];");
            var entry = motion == 0 ? "" : $"-{N(width*.13)}*pow(max(0,1-t/{N(.9+i*.18)}),3)";
            graph.Append($"[{current}][subject{i}]overlay=x='max({N(minX)},min({N(maxX)}-w,{N(cx)}-w/2+{N((24+i*4)*motion)}*sin(t*1.10+{i*2}){entry}))':y='max({N(minY)},min({N(maxY)}-h,{N(baseY)}-h-{N((22+i*3)*motion)}*(1-cos(t*1.60+{N(i*1.4)}))))':shortest=1[art{i}];");
            current = "art" + i;
        }
        if (screen is not null)
        {
            var sw = screen.Width; var sh = screen.Height; var x = screen.X; var y = screen.Y; var border = screen.Border;
            var fw = sw + border * 2; var fh = sh + border * 2;
            var glow = E(height * .026);
            graph.Append($"color=c=black@0:s={fw+glow*6}x{fh+glow*6}:r={fps}:d={duration},format=rgba,drawbox=x={glow*3}:y={glow*3}:w={fw}:h={fh}:color=0x03eaff@0.9:t={border*2}:replace=1,gblur=sigma={E(glow*.6)},{cachedFrame},hue=h='{N(48*motion)}*t'[glow];");
            graph.Append($"[{current}][glow]overlay=x={x-border-glow*3}:y={y-border-glow*3}:shortest=1[lit];");
            graph.Append($"color=c=0x09ddff:s={fw}x{fh}:r={fps}:d={duration},hue=h='{N(48*motion)}*t',format=rgba[frame];");
            var shine = E(fw * .20);
            graph.Append($"color=c=white@0.95:s={shine}x{Math.Max(2,border/2)}:r={fps}:d={duration},format=rgba[shine];");
            graph.Append($"[frame][shine]overlay=x='(W-w)*(0.5+0.5*sin(t*{N(1.55*motion)}))':y=0:shortest=1[framefx];");
            graph.Append($"[lit][framefx]overlay=x={x-border}:y={y-border}:shortest=1[framed];");
            graph.Append($"[{snap}:v]scale={sw}:{sh},setsar=1[gameplay];[framed][gameplay]overlay=x={x}:y={y}:shortest=1[screen];");
            current = "screen";
        }
        if (logo >= 0)
        {
            var lw = E(hasScreen ? Math.Min(width*.37,artSpan*.82) : width*.47); var lh = E(height * .20);
            graph.Append($"[{logo}:v]scale={lw}:{lh}:force_original_aspect_ratio=decrease,format=rgba,{cachedFrame}[logo];");
            graph.Append($"[{current}][logo]overlay=x='{N(artCenter)}-w/2':y='{N(height*.035)}+{N(3*motion)}*sin(t*0.7)':shortest=1[brand];"); current = "brand";
        }
        else
        {
            var font = FindFont();
            if (font is not null)
            {
                File.Copy(font, Path.Combine(staging,"font.ttf"));
                File.WriteAllText(Path.Combine(staging,"title.txt"),new string(assets.Title.Where(c=>!char.IsControl(c)).Take(60).ToArray()),new UTF8Encoding(false));
                graph.Append($"[{current}]drawtext=fontfile=font.ttf:textfile=title.txt:expansion=none:fontcolor=white:fontsize={Math.Max(16,height/27)}:x=(w-text_w)/2:y={E(height*.04)}:shadowcolor=black:shadowx=2:shadowy=2[brand];"); current = "brand";
            }
        }
        var fade = Math.Min(.35,duration/8d);
        graph.Append($"[{current}]fps={fps},trim=duration={duration},setpts=PTS-STARTPTS,fade=t=in:st=0:d={N(fade)},fade=t=out:st={N(duration-fade)}:d={N(fade)},format=yuv420p[outv]");
        return new(graph.ToString(),screen,backgroundTreatment,cutouts.Count);
    }
    private static ThemeScreenBounds MeasureScreen(int width, int height, string layout, string side, ThemeReferenceInfo? snap)
    {
        var maxW = width * (layout == "Cinema" ? .63 : .515);
        var maxH = height * (layout == "Cinema" ? .72 : .635);
        var ratio = snap is { Width: > 0, Height: > 0 } ? snap.Width * snap.PixelAspectRatio / snap.Height : 16d/9;
        var sw = E(Math.Min(maxW,maxH*ratio)); var sh = E(sw/ratio);
        var x = side == "Right" ? E(width-sw-width*.048) : E(width*.048);
        var y = E((height-sh)*.54+height*.045);
        return new(x,y,sw,sh,Math.Max(3,E(height*.008)));
    }
    private static string? FindFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return new[] { Path.Combine(fonts,"segoeui.ttf"),Path.Combine(fonts,"arial.ttf"),"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }.FirstOrDefault(File.Exists);
    }
}
