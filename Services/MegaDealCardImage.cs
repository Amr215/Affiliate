using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Affiliate.Services
{
    /// <summary>
    /// Renders a dark/red Mega Deal card image for Telegram (text messages cannot set a background color).
    /// Product name is intentionally omitted — it belongs in the caption as an Amazon URL.
    /// </summary>
    public static class MegaDealCardImage
    {
        private const int Width = 1280;
        private const int BannerHeight = 480;
        private const int SidePad = 56;
        private const float ProductBoxSize = 360f;

        private static readonly SKColor Bg = SKColor.Parse("#0B1220");
        private static readonly SKColor BannerTop = SKColor.Parse("#FF2D2D");
        private static readonly SKColor BannerBottom = SKColor.Parse("#9A0B0B");
        private static readonly SKColor Yellow = SKColor.Parse("#FFE566");
        private static readonly SKColor White = SKColors.White;
        private static readonly SKColor Muted = SKColor.Parse("#94A3B8");
        private static readonly SKColor PriceRed = SKColor.Parse("#FF4D4D");
        private static readonly SKColor Divider = SKColor.Parse("#1E293B");
        private static readonly SKColor Plate = SKColor.Parse("#F8FAFC");

        public static byte[] Render(ProductDropAlert alert, byte[]? productImageBytes = null)
        {
            var product = alert.Product;
            var name = product.Name?.Trim() ?? "منتج";
            var currency = product.Currency?.Trim() ?? "";
            var asin = product.Asin?.Trim() ?? "";
            var drop = alert.DropPercent.ToString("0.#");

            var bold = ResolveTypeface(bold: true);
            var regular = ResolveTypeface(bold: false);

            using var boldShaper = new SKShaper(bold);
            using var regularShaper = new SKShaper(regular);

            var nameLines = WrapText(boldShaper, name, 44, Width - SidePad * 2, bold);
            var bodyHeight = 64
                + nameLines.Count * 54
                + (string.IsNullOrWhiteSpace(asin) ? 0 : 44)
                + 48 + 84 + 76
                + (alert.BaselinePrice.HasValue ? 82 : 0)
                + (alert.AveragePrice.HasValue ? 84 : 0)
                + 56;

            var height = BannerHeight + bodyHeight;

            using var bitmap = new SKBitmap(Width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(Bg);

            DrawBanner(canvas, boldShaper, bold, drop, productImageBytes);
            var y = BannerHeight + 48f;

            foreach (var line in nameLines)
            {
                DrawCentered(canvas, boldShaper, line, y, 44, White, bold);
                y += 54;
            }

            if (!string.IsNullOrWhiteSpace(asin))
            {
                y += 8;
                DrawCentered(canvas, regularShaper, $"ASIN: {asin}", y, 28, Muted, regular);
                y += 44;
            }

            y += 8;
            DrawDivider(canvas, y);
            y += 42;

            DrawCentered(canvas, boldShaper, "السعر الحالي", y, 32, PriceRed, bold);
            y += 84;
            DrawCentered(canvas, boldShaper, FormatMoney(alert.CurrentPrice, currency), y, 72, PriceRed, bold);
            y += 72;

            if (alert.BaselinePrice.HasValue)
            {
                DrawCentered(canvas, regularShaper, "السعر السابق", y, 26, Muted, regular);
                y += 36;
                DrawCentered(canvas, regularShaper, FormatMoney(alert.BaselinePrice, currency), y, 32, Muted, regular);
                y += 44;
            }

            if (alert.AveragePrice.HasValue)
            {
                var avgColor = SKColor.Parse("#4ADE80");
                DrawCentered(canvas, regularShaper, "المتوسط", y, 26, avgColor, regular);
                y += 36;
                DrawCentered(
                    canvas,
                    regularShaper,
                    FormatMoney(alert.AveragePrice, currency, "0.##"),
                    y,
                    30,
                    avgColor,
                    regular);
                y += 46;
            }

            y += 10;
            DrawDivider(canvas, y);

            // Soft bottom fade so the card feels finished.
            using (var fade = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, height - 40),
                    new SKPoint(0, height),
                    new[] { new SKColor(11, 18, 32, 0), Bg },
                    new float[] { 0, 1 },
                    SKShaderTileMode.Clamp)
            })
            {
                canvas.DrawRect(0, height - 40, Width, 40, fade);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 92);
            return data.ToArray();
        }

        private static void DrawBanner(
            SKCanvas canvas,
            SKShaper shaper,
            SKTypeface typeface,
            string drop,
            byte[]? productImageBytes)
        {
            using var paint = new SKPaint { IsAntialias = true };
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(Width, BannerHeight),
                new[] { BannerTop, BannerBottom, SKColor.Parse("#6B0505") },
                new float[] { 0, 0.55f, 1 },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, Width, BannerHeight, paint);

            // Soft light blobs for depth
            using (var glow = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 35) })
            {
                canvas.DrawCircle(180, 60, 160, glow);
                canvas.DrawCircle(Width * 0.45f, BannerHeight + 40, 220, glow);
            }

            var hasImage = TryDecode(productImageBytes, out var productBitmap);
            using (productBitmap)
            {
                var textMaxX = hasImage
                    ? Width - SidePad - ProductBoxSize - 40
                    : Width - SidePad;

                // Discount block — left / center when no image, left-biased when image present
                if (hasImage)
                {
                    DrawInArea(canvas, shaper, "خصم استثنائي", SidePad, textMaxX, 110, 44, White, typeface);
                    DrawInArea(canvas, shaper, $"{drop}%", SidePad, textMaxX, 270, 160, Yellow, typeface);
                    DrawPairInArea(canvas, shaper, "أكثر من ", "50%!", SidePad, textMaxX, 380, 40, White, typeface);
                    DrawProductPlate(canvas, productBitmap!);
                }
                else
                {
                    DrawCentered(canvas, shaper, "خصم استثنائي", 110, 44, White, typeface);
                    DrawCentered(canvas, shaper, $"{drop}%", 270, 160, Yellow, typeface);
                    DrawCenteredPair(canvas, shaper, "أكثر من ", "50%!", 380, 40, White, typeface);
                }
            }
        }

        private static void DrawProductPlate(SKCanvas canvas, SKBitmap product)
        {
            var boxX = Width - SidePad - ProductBoxSize;
            var boxY = (BannerHeight - ProductBoxSize) / 2f;

            // Shadow
            using (var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 70),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12)
            })
            {
                canvas.DrawRoundRect(boxX + 6, boxY + 10, ProductBoxSize, ProductBoxSize, 32, 32, shadow);
            }

            using (var plate = new SKPaint { IsAntialias = true, Color = Plate })
                canvas.DrawRoundRect(boxX, boxY, ProductBoxSize, ProductBoxSize, 32, 32, plate);

            const float pad = 22f;
            var inner = new SKRect(boxX + pad, boxY + pad, boxX + ProductBoxSize - pad, boxY + ProductBoxSize - pad);
            var dest = FitRect(product.Width, product.Height, inner);

            canvas.Save();
            using (var clip = new SKRoundRect(new SKRect(boxX + 10, boxY + 10, boxX + ProductBoxSize - 10, boxY + ProductBoxSize - 10), 24))
                canvas.ClipRoundRect(clip);

            using (var imgPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
                canvas.DrawBitmap(product, dest, imgPaint);

            canvas.Restore();
        }

        private static SKRect FitRect(int srcW, int srcH, SKRect bounds)
        {
            if (srcW <= 0 || srcH <= 0)
                return bounds;

            var scale = Math.Min(bounds.Width / srcW, bounds.Height / srcH);
            var w = srcW * scale;
            var h = srcH * scale;
            var x = bounds.MidX - w / 2f;
            var y = bounds.MidY - h / 2f;
            return SKRect.Create(x, y, w, h);
        }

        private static bool TryDecode(byte[]? bytes, out SKBitmap? bitmap)
        {
            bitmap = null;
            if (bytes is not { Length: > 0 })
                return false;

            try
            {
                bitmap = SKBitmap.Decode(bytes);
                return bitmap is not null;
            }
            catch
            {
                bitmap?.Dispose();
                bitmap = null;
                return false;
            }
        }

        private static void DrawInArea(
            SKCanvas canvas,
            SKShaper shaper,
            string text,
            float left,
            float right,
            float baselineY,
            float size,
            SKColor color,
            SKTypeface typeface)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color,
                TextSize = size,
                Typeface = typeface
            };

            var shaped = shaper.Shape(text, paint);
            var areaW = right - left;
            var x = left + Math.Max(0, (areaW - shaped.Width) / 2f);
            canvas.DrawShapedText(shaper, text, x, baselineY, paint);
        }

        private static void DrawPairInArea(
            SKCanvas canvas,
            SKShaper shaper,
            string arabicPrefix,
            string latinSuffix,
            float left,
            float right,
            float baselineY,
            float size,
            SKColor color,
            SKTypeface typeface)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color,
                TextSize = size,
                Typeface = typeface
            };

            var prefixW = shaper.Shape(arabicPrefix, paint).Width;
            var suffixW = shaper.Shape(latinSuffix, paint).Width;
            var total = prefixW + suffixW;
            var areaW = right - left;
            var x = left + Math.Max(0, (areaW - total) / 2f);
            canvas.DrawShapedText(shaper, latinSuffix, x, baselineY, paint);
            canvas.DrawShapedText(shaper, arabicPrefix, x + suffixW, baselineY, paint);
        }

        private static void DrawCenteredPair(
            SKCanvas canvas,
            SKShaper shaper,
            string arabicPrefix,
            string latinSuffix,
            float baselineY,
            float size,
            SKColor color,
            SKTypeface typeface) =>
            DrawPairInArea(canvas, shaper, arabicPrefix, latinSuffix, SidePad, Width - SidePad, baselineY, size, color, typeface);

        private static string FormatMoney(decimal? amount, string currency, string format = "0.##") =>
            amount is null ? "—" : $"{amount.Value.ToString(format)} {currency}";

        private static void DrawDivider(SKCanvas canvas, float y)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = Divider,
                StrokeWidth = 3
            };
            canvas.DrawLine(SidePad, y, Width - SidePad, y, paint);
        }

        private static void DrawCentered(
            SKCanvas canvas,
            SKShaper shaper,
            string text,
            float baselineY,
            float size,
            SKColor color,
            SKTypeface typeface)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color,
                TextSize = size,
                Typeface = typeface,
                IsStroke = false
            };

            var shaped = shaper.Shape(text, paint);
            var x = (Width - shaped.Width) / 2f;
            canvas.DrawShapedText(shaper, text, x, baselineY, paint);
        }

        private static List<string> WrapText(
            SKShaper shaper,
            string text,
            float size,
            float maxWidth,
            SKTypeface typeface)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                TextSize = size,
                Typeface = typeface
            };

            if (shaper.Shape(text, paint).Width <= maxWidth)
                return [text];

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 1)
                return [Truncate(shaper, text, paint, maxWidth)];

            var lines = new List<string>();
            var current = words[0];
            for (var i = 1; i < words.Length; i++)
            {
                var candidate = current + " " + words[i];
                if (shaper.Shape(candidate, paint).Width <= maxWidth)
                {
                    current = candidate;
                }
                else
                {
                    lines.Add(current);
                    current = words[i];
                    if (lines.Count == 2)
                    {
                        var rest = string.Join(" ", words.Skip(i));
                        lines.Add(Truncate(shaper, rest, paint, maxWidth));
                        return lines;
                    }
                }
            }

            lines.Add(current);
            return lines;
        }

        private static string Truncate(SKShaper shaper, string text, SKPaint paint, float maxWidth)
        {
            if (shaper.Shape(text, paint).Width <= maxWidth)
                return text;

            const string ellipsis = "…";
            for (var len = text.Length - 1; len > 1; len--)
            {
                var candidate = text[..len].TrimEnd() + ellipsis;
                if (shaper.Shape(candidate, paint).Width <= maxWidth)
                    return candidate;
            }

            return ellipsis;
        }

        private static SKTypeface ResolveTypeface(bool bold)
        {
            var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
            foreach (var family in new[] { "Segoe UI", "Tahoma", "Arial", "Noto Sans Arabic", "DejaVu Sans" })
            {
                var tf = SKTypeface.FromFamilyName(family, style);
                if (tf is not null &&
                    !string.Equals(tf.FamilyName, "sans-serif", StringComparison.OrdinalIgnoreCase))
                {
                    return tf;
                }
            }

            return SKTypeface.Default;
        }
    }
}
