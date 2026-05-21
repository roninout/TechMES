using QRCoder;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TechEquipments
{
    public sealed class QrCodeService : IQrCodeService
    {
        /// <summary>
        /// Возвращает детерминированный путь к файлу QR (без создания папок/файлов).
        /// </summary>
        public string GetExpectedQrPngPath(string text, string? outputDirectory = null, string? fileNameWithoutExt = null)
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("QR text is empty.", nameof(text));

            var dir = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QRCodes")
                : outputDirectory.Trim();

            var baseName = string.IsNullOrWhiteSpace(fileNameWithoutExt)
                ? MakeSafeFileName(text)
                : MakeSafeFileName(fileNameWithoutExt);

            return Path.Combine(dir, baseName + ".png");
        }

        /// <summary>
        /// Генерирует QR в PNG и сохраняет на диск.
        /// Если файл уже существует — не создаёт дубликаты.
        /// </summary>
        public async Task<string> GenerateQrPngAsync(string text, string? outputDirectory = null, string? fileNameWithoutExt = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("QR text is empty.", nameof(text));

            var path = GetExpectedQrPngPath(text, outputDirectory, fileNameWithoutExt);

            // Если уже есть — ничего не делаем, просто возвращаем путь
            if (File.Exists(path))
                return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data);

            // Базовый QR
            byte[] qrBytes = png.GetGraphic(pixelsPerModule: 12); // размера QR

            // Финальный PNG: текст сверху + QR ниже
            byte[] finalBytes = BuildQrWithCaptionPng(qrBytes, text);

            await File.WriteAllBytesAsync(path, finalBytes, ct);
            return path;
        }

        /// <summary>
        /// Собирает итоговое PNG:
        /// - белый фон,
        /// - заголовок (имя оборудования) сверху,
        /// - QR-код по центру ниже.
        /// </summary>
        private static byte[] BuildQrWithCaptionPng(byte[] qrBytes, string caption)
        {
            using var qrStream = new MemoryStream(qrBytes);

            var qrImage = new BitmapImage();
            qrImage.BeginInit();
            qrImage.CacheOption = BitmapCacheOption.OnLoad;
            qrImage.StreamSource = qrStream;
            qrImage.EndInit();
            qrImage.Freeze();

            double dpi = 96.0;

            double qrWidth = qrImage.PixelWidth;
            double qrHeight = qrImage.PixelHeight;

            double sidePadding = 24;    // отступ слева/справа
            double topPadding = 18;     // отступ сверху до текста
            double bottomPadding = 18;  // отступ снизу после QR
            double gap = 12;            // расстояние между текстом и QR

            double fontSize = 22;       // размер текста оборудования
            var typeface = new Typeface(
                new FontFamily("Segoe UI"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);

            // Ширина итогового изображения в первую очередь определяется QR,
            // а текст просто центрируется относительно всей картинки.
            double canvasWidth = qrWidth + sidePadding * 2;

            var formattedText = new FormattedText(
                caption ?? "",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.Black,
                dpi);

            // Ограничиваем текст по ширине и центрируем его
            formattedText.MaxTextWidth = canvasWidth - sidePadding * 2;
            formattedText.TextAlignment = TextAlignment.Center;

            double canvasHeight = topPadding + formattedText.Height + gap + qrHeight + bottomPadding;

            var visual = new DrawingVisual();

            using (var dc = visual.RenderOpen())
            {
                // Белый фон
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, canvasWidth, canvasHeight));

                // Текст строго по центру сверху
                var textRect = new Rect(
                    sidePadding,
                    topPadding,
                    canvasWidth - sidePadding * 2,
                    formattedText.Height);

                dc.DrawText(formattedText, new Point(textRect.X, textRect.Y));

                // QR по центру
                double qrX = (canvasWidth - qrWidth) / 2.0;
                double qrY = topPadding + formattedText.Height + gap;

                dc.DrawImage(qrImage, new Rect(qrX, qrY, qrWidth, qrHeight));
            }

            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling(canvasWidth),
                (int)Math.Ceiling(canvasHeight),
                dpi,
                dpi,
                PixelFormats.Pbgra32);

            rtb.Render(visual);
            rtb.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            using var output = new MemoryStream();
            encoder.Save(output);

            return output.ToArray();
        }

        /// <summary>
        /// Делает имя файла безопасным для файловой системы.
        /// </summary>
        private static string MakeSafeFileName(string text)
        {
            var name = (text ?? "").Trim();
            if (name.Length == 0)
                name = "qr";

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            if (safe.Length > 80)
                safe = safe.Substring(0, 80);

            return safe;
        }
    }
}

