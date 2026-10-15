using System;
using System.Globalization;

namespace ScreenTask
{
    internal static class HdrColorCorrection
    {
        // Windows floating-point desktop duplication is linear scRGB (BT.709
        // primaries), with 1.0 representing 80 nits. It is not HDR10/PQ.
        internal static string ToneMapFilter(double exposure, int peakNits, bool jpeg)
        {
            exposure = Math.Max(-2, Math.Min(2, exposure));
            var peak = Math.Max(400, Math.Min(4000, peakNits)) / 80.0;
            return "format=gbrpf32le," +
                "zscale=pin=bt709:tin=linear:min=gbr:rin=full:p=bt709:t=linear:m=gbr:r=full," +
                "exposure=exposure=" + exposure.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                "tonemap=tonemap=mobius:param=0.3:desat=0:peak=" + peak.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                (jpeg ? "zscale=p=bt709:t=iec61966-2-1:m=bt470bg:r=full" : "zscale=p=bt709:t=bt709:m=bt709:r=limited");
        }

        internal static string CaptureFilter(int screen, int fps, bool mouse, int width, bool hdr, double exposure, int peakNits, bool jpeg)
        {
            var filter = String.Format(CultureInfo.InvariantCulture,
                "ddagrab=output_idx={0}:framerate={1}:draw_mouse={2}:dup_frames=1:output_fmt={3},hwdownload,format={4}",
                screen, Math.Max(1, fps), mouse ? 1 : 0, hdr ? "16bit" : "8bit", hdr ? "rgbaf16le" : "bgra");
            // Scale while still floating point, before tone mapping/8-bit quantization.
            if (hdr) filter += ",format=gbrpf32le";
            if (width > 0) filter += ",scale=" + width + ":-2:flags=fast_bilinear" + (hdr ? "" : jpeg ? ":out_color_matrix=bt601:out_range=full" : ":out_color_matrix=bt709:out_range=tv");
            if (hdr) filter += "," + ToneMapFilter(exposure, peakNits, jpeg);
            return filter + (jpeg ? ",format=yuvj420p" : ",format=nv12");
        }
    }
}
