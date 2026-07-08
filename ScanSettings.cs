using System.Drawing;
using System.Drawing.Imaging;

namespace ScannerPro
{
    /// <summary>
    /// Color acquisition mode requested from the scanner.
    /// </summary>
    public enum ColorMode
    {
        Color,
        Grayscale,
        BlackWhite
    }

    /// <summary>
    /// The kind of original being scanned. Used to seed sensible defaults
    /// (documents scan fine at 300 DPI, film/negatives need very high DPI).
    /// </summary>
    public enum DocumentType
    {
        Document,
        Photo,
        Film
    }

    /// <summary>
    /// Output file format for saving the acquired image.
    /// </summary>
    public enum OutputFormat
    {
        Jpeg,
        Png,
        Tiff,
        Bmp,
        Pdf
    }

    /// <summary>
    /// User-configurable acquisition settings shared by the WIA and TWAIN backends.
    /// </summary>
    public sealed class ScanSettings
    {
        /// <summary>Resolution in dots per inch for a full scan.</summary>
        public int Dpi { get; set; } = 300;

        public ColorMode ColorMode { get; set; } = ColorMode.Color;

        public DocumentType DocumentType { get; set; } = DocumentType.Document;

        public OutputFormat OutputFormat { get; set; } = OutputFormat.Jpeg;

        /// <summary>Brightness offset, -100..100 (0 = scanner default).</summary>
        public int Brightness { get; set; }

        /// <summary>Contrast offset, -100..100 (0 = scanner default).</summary>
        public int Contrast { get; set; }

        /// <summary>
        /// When true, request the scanner's high color depth (48-bit color /
        /// 16-bit grayscale) instead of the standard 24-bit / 8-bit. Ideal for
        /// film and negatives where extra tonal range aids post-processing.
        /// </summary>
        public bool HighBitDepth { get; set; }

        /// <summary>
        /// Region of interest to scan, expressed in inches from the top-left of
        /// the scan bed. Null means scan the full bed / frame.
        /// </summary>
        public RectangleF? RegionInches { get; set; }

        /// <summary>Bits-per-pixel to request from the scanner for the current color mode.</summary>
        public int BitDepth => ColorMode switch
        {
            ColorMode.BlackWhite => 1,
            ColorMode.Grayscale => HighBitDepth ? 16 : 8,
            _ => HighBitDepth ? 48 : 24
        };

        /// <summary>Resolutions offered in the UI, spanning documents through film.</summary>
        public static readonly int[] SupportedDpi =
            { 75, 100, 150, 200, 300, 400, 600, 1200, 2400, 4800 };

        /// <summary>Resolution used for the fast on-screen preview. Kept low so the
        /// full-bed preview is quick; the final scan is always at the chosen DPI,
        /// so this never affects output quality, only preview/selection precision.</summary>
        public const int PreviewDpi = 50;

        /// <summary>Effective resolution for the given mode (preview is always fast).</summary>
        public int ResolveDpi(ScanMode mode) => mode == ScanMode.Preview ? PreviewDpi : Dpi;

        /// <summary>True when the output is a multi-page document container (PDF).</summary>
        public bool IsDocumentFormat => OutputFormat == OutputFormat.Pdf;

        public string FileExtension => OutputFormat switch
        {
            OutputFormat.Png => ".png",
            OutputFormat.Tiff => ".tif",
            OutputFormat.Bmp => ".bmp",
            OutputFormat.Pdf => ".pdf",
            _ => ".jpg"
        };

        public ImageFormat ImageFormat => OutputFormat switch
        {
            OutputFormat.Png => System.Drawing.Imaging.ImageFormat.Png,
            OutputFormat.Tiff => System.Drawing.Imaging.ImageFormat.Tiff,
            OutputFormat.Bmp => System.Drawing.Imaging.ImageFormat.Bmp,
            _ => System.Drawing.Imaging.ImageFormat.Jpeg
        };
    }
}
