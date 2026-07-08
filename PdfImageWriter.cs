using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScannerPro
{
    /// <summary>
    /// Minimal, dependency-free PDF writer that embeds one raster image per page
    /// using PDF's native JPEG support (DCTDecode). Each page is placed at its
    /// physical size derived from the image's resolution, so a 300 DPI scan
    /// prints at its true dimensions.
    /// </summary>
    public static class PdfImageWriter
    {
        private const long JpegQuality = 90L;

        public static void Save(IReadOnlyList<Image> pages, string fileName)
        {
            using var stream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
            Write(pages, stream);
        }

        public static void Write(IReadOnlyList<Image> pages, Stream output)
        {
            if (pages.Count == 0)
            {
                throw new InvalidOperationException("There are no pages to write to the PDF.");
            }

            var encoded = new List<EncodedPage>(pages.Count);
            foreach (var page in pages)
            {
                encoded.Add(EncodePage(page));
            }

            var ascii = Encoding.ASCII;
            void WriteText(string text)
            {
                var bytes = ascii.GetBytes(text);
                output.Write(bytes, 0, bytes.Length);
            }

            // Two fixed objects (catalog + page tree) plus three per page
            // (page, content stream, image XObject).
            var totalObjects = 2 + encoded.Count * 3;
            var offsets = new long[totalObjects + 1];

            WriteText("%PDF-1.4\n");
            output.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6); // binary marker

            offsets[1] = output.Position;
            WriteText("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            offsets[2] = output.Position;
            var kids = new StringBuilder();
            for (var i = 0; i < encoded.Count; i++)
            {
                kids.Append(3 + i * 3).Append(" 0 R ");
            }
            WriteText($"2 0 obj\n<< /Type /Pages /Kids [ {kids.ToString().Trim()} ] /Count {encoded.Count} >>\nendobj\n");

            for (var i = 0; i < encoded.Count; i++)
            {
                var page = encoded[i];
                var pageObj = 3 + i * 3;
                var contentObj = pageObj + 1;
                var imageObj = pageObj + 2;

                var widthPt = page.WidthPoints.ToString("0.###", CultureInfo.InvariantCulture);
                var heightPt = page.HeightPoints.ToString("0.###", CultureInfo.InvariantCulture);

                offsets[pageObj] = output.Position;
                WriteText($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPt} {heightPt}] "
                    + $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

                var content = $"q\n{widthPt} 0 0 {heightPt} 0 0 cm\n/Im0 Do\nQ\n";
                var contentBytes = ascii.GetBytes(content);
                offsets[contentObj] = output.Position;
                WriteText($"{contentObj} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
                output.Write(contentBytes, 0, contentBytes.Length);
                WriteText("endstream\nendobj\n");

                offsets[imageObj] = output.Position;
                WriteText($"{imageObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} "
                    + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.JpegData.Length} >>\nstream\n");
                output.Write(page.JpegData, 0, page.JpegData.Length);
                WriteText("\nendstream\nendobj\n");
            }

            var xrefPos = output.Position;
            WriteText($"xref\n0 {totalObjects + 1}\n");
            WriteText("0000000000 65535 f \n");
            for (var i = 1; i <= totalObjects; i++)
            {
                WriteText($"{offsets[i]:D10} 00000 n \n");
            }

            WriteText($"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        }

        private static EncodedPage EncodePage(Image page)
        {
            var dpiX = page.HorizontalResolution > 1f ? page.HorizontalResolution : 300f;
            var dpiY = page.VerticalResolution > 1f ? page.VerticalResolution : 300f;

            // Flatten onto a white 24bpp surface: DCTDecode/DeviceRGB expects
            // 8-bit, 3-channel data, and this also drops any alpha channel.
            using var flattened = new Bitmap(page.Width, page.Height, PixelFormat.Format24bppRgb);
            flattened.SetResolution(dpiX, dpiY);
            using (var graphics = Graphics.FromImage(flattened))
            {
                graphics.Clear(Color.White);
                graphics.DrawImage(page, new Rectangle(0, 0, flattened.Width, flattened.Height));
            }

            using var buffer = new MemoryStream();
            using (var parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
                flattened.Save(buffer, GetJpegEncoder(), parameters);
            }

            return new EncodedPage
            {
                JpegData = buffer.ToArray(),
                PixelWidth = flattened.Width,
                PixelHeight = flattened.Height,
                WidthPoints = flattened.Width / dpiX * 72f,
                HeightPoints = flattened.Height / dpiY * 72f
            };
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                {
                    return codec;
                }
            }

            throw new InvalidOperationException("No JPEG encoder is available on this system.");
        }

        private sealed class EncodedPage
        {
            public byte[] JpegData = System.Array.Empty<byte>();
            public int PixelWidth;
            public int PixelHeight;
            public float WidthPoints;
            public float HeightPoints;
        }
    }
}
