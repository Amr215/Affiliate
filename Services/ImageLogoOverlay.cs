using SkiaSharp;

namespace Affiliate.Services
{
    /// <summary>
    /// Stamps the channel logo onto publish screenshots.
    /// Product-page: bottom-left. Cart: bottom-center.
    /// </summary>
    public static class ImageLogoOverlay
    {
        private const float WidthRatio = 0.18f;
        private const float MarginRatio = 0.025f;

        private static readonly Lazy<SKBitmap?> Logo = new(LoadLogo);

        public static byte[] Apply(byte[] pngBytes, bool cartScreenshot = false)
        {
            var logo = Logo.Value;
            if (logo is null || pngBytes.Length == 0)
                return pngBytes;

            using var image = SKBitmap.Decode(pngBytes);
            if (image is null)
                return pngBytes;

            var targetW = Math.Max(48, (int)(image.Width * WidthRatio));
            var scale = targetW / (float)logo.Width;
            var targetH = Math.Max(48, (int)(logo.Height * scale));
            var margin = Math.Max(8, (int)(Math.Min(image.Width, image.Height) * MarginRatio));
            var x = cartScreenshot
                ? (image.Width - targetW) / 2f
                : margin;
            var dest = SKRect.Create(x, image.Height - targetH - margin, targetW, targetH);

            using var canvas = new SKCanvas(image);
            canvas.Save();
            using (var clip = new SKPath())
            {
                // Logo asset is a square with white corners outside the circle — clip them away.
                clip.AddCircle(dest.MidX, dest.MidY, Math.Min(dest.Width, dest.Height) / 2f);
                canvas.ClipPath(clip, antialias: true);
            }

            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(logo, dest, paint);
            canvas.Restore();

            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            return encoded?.ToArray() ?? pngBytes;
        }

        private static SKBitmap? LoadLogo()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "sinbad-logo.png");
            if (!File.Exists(path))
                return null;

            return SKBitmap.Decode(path);
        }
    }
}
