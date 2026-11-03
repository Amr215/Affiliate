using SkiaSharp;

namespace Affiliate.Services
{
    /// <summary>
    /// Stamps the channel logo (circle + «عروض السندباد») onto publish screenshots.
    /// Product-page: bottom-left. Cart: bottom-center.
    /// </summary>
    public static class ImageLogoOverlay
    {
        private const float WidthRatio = 0.14f;
        private const float MarginRatio = 0.015f;

        private static readonly Lazy<SKBitmap?> Logo = new(LoadLogo);

        public static byte[] Apply(byte[] pngBytes, bool cartScreenshot = false)
        {
            var logo = Logo.Value;
            if (logo is null || pngBytes.Length == 0)
                return pngBytes;

            using var image = SKBitmap.Decode(pngBytes);
            if (image is null)
                return pngBytes;

            var targetW = Math.Max(64, (int)(image.Width * WidthRatio));
            var scale = targetW / (float)logo.Width;
            var targetH = Math.Max(64, (int)(logo.Height * scale));
            var margin = Math.Max(8, (int)(Math.Min(image.Width, image.Height) * MarginRatio));
            var x = cartScreenshot
                ? (image.Width - targetW) / 2f
                : margin;
            var dest = SKRect.Create(x, image.Height - targetH - margin, targetW, targetH);

            using var canvas = new SKCanvas(image);
            // Full logo asset (circle + Arabic wordmark) already has transparent background.
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(logo, dest, paint);

            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            return encoded?.ToArray() ?? pngBytes;
        }

        /// <summary>
        /// Always JPEG + downscale so Telegram photo upload stays small/fast on slow links.
        /// </summary>
        public static (byte[] Bytes, string ContentType, string FileName) CompactForTelegram(
            byte[] imageBytes,
            string preferredFileName)
        {
            const int maxWidth = 960;
            const int maxBytes = 400_000;
            var name = Path.ChangeExtension(preferredFileName, ".jpg") ?? "publish.jpg";

            if (imageBytes.Length == 0)
                return (imageBytes, "image/jpeg", name);

            using var bitmap = SKBitmap.Decode(imageBytes);
            if (bitmap is null)
                return (imageBytes, "image/png", preferredFileName);

            SKBitmap work = bitmap;
            SKBitmap? resized = null;
            try
            {
                if (bitmap.Width > maxWidth)
                {
                    var h = Math.Max(1, (int)(bitmap.Height * (maxWidth / (float)bitmap.Width)));
                    resized = bitmap.Resize(new SKImageInfo(maxWidth, h), SKFilterQuality.Medium);
                    if (resized is not null)
                        work = resized;
                }

                byte[]? best = null;
                for (var quality = 70; quality >= 40; quality -= 10)
                {
                    using var jpeg = work.Encode(SKEncodedImageFormat.Jpeg, quality);
                    var bytes = jpeg?.ToArray();
                    if (bytes is null || bytes.Length == 0)
                        continue;
                    best = bytes;
                    if (bytes.Length <= maxBytes)
                        break;
                }

                return best is null
                    ? (imageBytes, "image/png", preferredFileName)
                    : (best, "image/jpeg", name);
            }
            finally
            {
                resized?.Dispose();
            }
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
