using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Linq;
using System.Windows.Forms;

namespace ScannerPro
{
    public partial class Form1 : Form
    {
        private ScannerManager? _scannerManager;
        private bool _isAcquiring;
        private string _currentActionName = "scan";

        private ComboBox _cmbScanners = null!;
        private Button _btnPreview = null!;
        private Button _btnScan = null!;
        private Button _btnRefresh = null!;
        private Button _btnSave = null!;
        private PictureBox _pictureBox = null!;
        private Label _lblStatus = null!;

        private ComboBox _cmbScanType = null!;
        private ComboBox _cmbTargetSize = null!;
        private ComboBox _cmbTargetOrientation = null!;
        private ComboBox _cmbColorMode = null!;
        private ComboBox _cmbResolution = null!;
        private ComboBox _cmbFormat = null!;
        private ComboBox _cmbBitDepth = null!;
        private NumericUpDown _numBrightness = null!;
        private NumericUpDown _numContrast = null!;
        private Button _btnClearRegion = null!;
        private CheckBox _chkAppend = null!;
        private Button _btnNewDocument = null!;
        private Label _lblPages = null!;

        // Multi-page document state. Pages own their images; the picture box may
        // either show a page (not owned) or a transient preview (owned).
        private readonly List<Image> _pages = new();
        private bool _pictureOwnsImage;

        // Region-of-interest selection state.
        private bool _isSelecting;
        private Point _selectStartDisplay;
        private Rectangle _selectionDisplay;      // in PictureBox client coordinates
        private RectangleF? _selectionFraction;   // normalized (0..1) within the current image
        private RectangleF? _currentBedRectInches; // bed area the current image represents
        private ScanSettings? _lastAcquireSettings;
        private bool _lastWasPreview;
        private bool _settingsPanelReady;
        private bool _updatingDocumentControls;

        // Captured when a region scan starts, so the result can be cropped in
        // software if the scanner driver ignored the hardware region request.
        // Remembered so multi-page appends can reuse the region after the
        // on-screen selection has been cleared from the committed page view.
        private RectangleF? _lastDocumentRegionInches;

        public Form1()
        {
            InitializeUI();
        }

        private void InitializeUI()
        {
            Text = "ScannerPro";
            try { this.Icon = new Icon("Icon.ico"); } catch { }
            Size = new Size(1000, 850);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(780, 480);

            var panelTop = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(10) };

            _cmbScanners = new ComboBox
            {
                Width = 340,
                Left = 10,
                Top = 12,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DisplayMember = nameof(ScannerDevice.DisplayName)
            };
            _btnPreview = new Button { Text = "Preview", Left = 360, Top = 10, Width = 100, Enabled = false };
            _btnScan = new Button { Text = "Scan", Left = 470, Top = 10, Width = 100, Enabled = false };
            _btnRefresh = new Button { Text = "Refresh", Left = 580, Top = 10, Width = 100, Enabled = false };
            _btnSave = new Button { Text = "Export Scan", Left = 690, Top = 10, Width = 110, Enabled = false };

            panelTop.Controls.Add(_cmbScanners);
            panelTop.Controls.Add(_btnPreview);
            panelTop.Controls.Add(_btnScan);
            panelTop.Controls.Add(_btnRefresh);
            panelTop.Controls.Add(_btnSave);

            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = Color.WhiteSmoke,
                Cursor = Cursors.Cross
            };
            _pictureBox.MouseDown += PictureBox_MouseDown;
            _pictureBox.MouseMove += PictureBox_MouseMove;
            _pictureBox.MouseUp += PictureBox_MouseUp;
            _pictureBox.Paint += PictureBox_Paint;
            _pictureBox.Resize += (_, _) => SyncSelectionDisplayToImage();

            var panelBottom = new Panel { Dock = DockStyle.Bottom, Height = 32 };
            _lblStatus = new Label { Dock = DockStyle.Fill, Text = "Starting...", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
            panelBottom.Controls.Add(_lblStatus);

            var panelSettings = BuildSettingsPanel();

            // Docking is resolved outer-first from the last-added control, so add
            // the fill area first, then the right panel, then the full-width bars.
            Controls.Add(_pictureBox);
            Controls.Add(panelSettings);
            Controls.Add(panelTop);
            Controls.Add(panelBottom);

            _btnPreview.Click += BtnPreview_Click;
            _btnScan.Click += BtnScan_Click;
            _btnRefresh.Click += BtnRefresh_Click;
            _btnSave.Click += BtnSave_Click;
        }

        private Panel BuildSettingsPanel()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = 250, Padding = new Padding(10, 8, 10, 8) };

            var group = new GroupBox { Dock = DockStyle.Fill, Text = "Scan Settings", Padding = new Padding(10) };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoScroll = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _cmbScanType = CreateComboBox(
                new Option<DocumentType>(DocumentType.Document, "Document"),
                new Option<DocumentType>(DocumentType.Photo, "Photo"),
                new Option<DocumentType>(DocumentType.Film, "Film / Negative"));
            _cmbScanType.SelectedIndexChanged += ScanType_SelectedIndexChanged;

            _cmbTargetSize = CreateComboBox(CreateTargetSizeOptions());
            _cmbTargetSize.SelectedIndexChanged += TargetSize_SelectedIndexChanged;

            _cmbTargetOrientation = CreateComboBox(
                new Option<ScanOrientation>(ScanOrientation.Portrait, "Portrait"),
                new Option<ScanOrientation>(ScanOrientation.Landscape, "Landscape"));
            _cmbTargetOrientation.SelectedIndexChanged += TargetOrientation_SelectedIndexChanged;

            _cmbColorMode = CreateComboBox(
                new Option<ColorMode>(ColorMode.Color, "Color"),
                new Option<ColorMode>(ColorMode.Grayscale, "Grayscale"),
                new Option<ColorMode>(ColorMode.BlackWhite, "Black && White"));

            var resolutionOptions = ScanSettings.SupportedDpi
                .Select(dpi => new Option<int>(dpi, $"{dpi} DPI"))
                .ToArray();
            _cmbResolution = CreateComboBox(resolutionOptions);

            _cmbBitDepth = CreateComboBox(
                new Option<bool>(false, "24-bit color / 8-bit gray"),
                new Option<bool>(true, "48-bit color / 16-bit gray"));

            _cmbFormat = CreateComboBox();
            _cmbFormat.SelectedIndexChanged += Format_SelectedIndexChanged;

            _numBrightness = CreateAdjustment();
            _numContrast = CreateAdjustment();

            AddSetting(layout, "Scan type", _cmbScanType);
            AddSetting(layout, "Target size", _cmbTargetSize);
            AddSetting(layout, "Orientation", _cmbTargetOrientation);
            AddSetting(layout, "Color mode", _cmbColorMode);
            AddSetting(layout, "Bit depth", _cmbBitDepth);
            AddSetting(layout, "Resolution", _cmbResolution);
            AddSetting(layout, "Output format", _cmbFormat);
            AddSetting(layout, "Brightness (-100 to 100)", _numBrightness);
            AddSetting(layout, "Contrast (-100 to 100)", _numContrast);

            _chkAppend = new CheckBox { Text = "Append pages (multi-page PDF)", Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 2), Enabled = false };
            _chkAppend.CheckedChanged += (_, _) =>
            {
                if (!_updatingDocumentControls)
                {
                    UpdateDocumentControls();
                }
            };
            layout.Controls.Add(_chkAppend);

            _lblPages = new Label { Text = "Pages: 0", Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            layout.Controls.Add(_lblPages);

            _btnNewDocument = new Button { Text = "New document (clear pages)", Dock = DockStyle.Top, Height = 30, Margin = new Padding(0, 0, 0, 4), Enabled = false };
            _btnNewDocument.Click += (_, _) => ClearAllPages();
            layout.Controls.Add(_btnNewDocument);

            _btnClearRegion = new Button { Text = "Clear scan region", Dock = DockStyle.Top, Height = 30, Margin = new Padding(0, 4, 0, 0), Enabled = false };
            _btnClearRegion.Click += (_, _) => ClearSelection();
            layout.Controls.Add(_btnClearRegion);

            var btnReset = new Button { Text = "Reset to defaults", Dock = DockStyle.Top, Height = 30, Margin = new Padding(0, 4, 0, 0) };
            btnReset.Click += (_, _) => ApplyDefaults();
            layout.Controls.Add(btnReset);

            group.Controls.Add(layout);
            panel.Controls.Add(group);

            _settingsPanelReady = true;
            ApplyDefaults();
            return panel;
        }

        private static object[] CreateTargetSizeOptions()
        {
            return new object[]
            {
                new Option<ScanTargetSize>(ScanTargetSize.FullBed, ScanTargetSize.FullBed.Text),
                new Option<ScanTargetSize>(ScanTargetSize.ManualRoi, ScanTargetSize.ManualRoi.Text),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("Letter - 8.5 x 11 in", 8.5f, 11f), "Letter - 8.5 x 11 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("Legal - 8.5 x 14 in", 8.5f, 14f), "Legal - 8.5 x 14 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("Ledger/Tabloid - 11 x 17 in", 11f, 17f), "Ledger/Tabloid - 11 x 17 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("A5 - 5.83 x 8.27 in", 5.83f, 8.27f), "A5 - 5.83 x 8.27 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("A4 - 8.27 x 11.69 in", 8.27f, 11.69f), "A4 - 8.27 x 11.69 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("A3 - 11.69 x 16.54 in", 11.69f, 16.54f), "A3 - 11.69 x 16.54 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("B5 - 6.93 x 9.84 in", 6.93f, 9.84f), "B5 - 6.93 x 9.84 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("B4 - 9.84 x 13.9 in", 9.84f, 13.9f), "B4 - 9.84 x 13.9 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("Photo - 4 x 6 in", 4f, 6f), "Photo - 4 x 6 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("Photo - 5 x 7 in", 5f, 7f), "Photo - 5 x 7 in"),
                new Option<ScanTargetSize>(ScanTargetSize.FromSize("Photo - 8 x 10 in", 8f, 10f), "Photo - 8 x 10 in")
            };
        }

        private static ComboBox CreateComboBox(params object[] items)
        {
            var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 0, 0, 6) };
            combo.Items.AddRange(items);
            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
            return combo;
        }

        private static NumericUpDown CreateAdjustment()
        {
            return new NumericUpDown { Dock = DockStyle.Top, Minimum = -100, Maximum = 100, Value = 0, Margin = new Padding(0, 0, 0, 6) };
        }

        private static void AddSetting(TableLayoutPanel layout, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
            layout.Controls.Add(control);
        }

        private void ApplyDefaults()
        {
            SelectByValue(_cmbScanType, DocumentType.Document);
            SelectByValue(_cmbColorMode, ColorMode.Color);
            SelectByValue(_cmbBitDepth, false);
            SelectByValue(_cmbResolution, 300);
            SelectByValue(_cmbTargetSize, ScanTargetSize.FullBed);
            SelectByValue(_cmbTargetOrientation, ScanOrientation.Portrait);
            RebuildFormatList(DocumentType.Document);
            SelectByValue(_cmbFormat, OutputFormat.Pdf);
            _numBrightness.Value = 0;
            _numContrast.Value = 0;
            UpdateDocumentControls();
        }

        private void ScanType_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var scanType = GetSelectedValue(_cmbScanType, DocumentType.Document);

            // Seed a sensible resolution for the chosen original. Users can still override.
            var suggested = scanType switch
            {
                DocumentType.Photo => 600,
                DocumentType.Film => 2400,
                _ => 300
            };
            SelectByValue(_cmbResolution, suggested);

            RebuildFormatList(scanType);
            UpdateDocumentControls();
        }

        private void Format_SelectedIndexChanged(object? sender, EventArgs e)
        {
            UpdateDocumentControls();
        }

        private void TargetSize_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var target = GetSelectedTargetSize();
            EnsureTargetOrientationFits(target);

            _lastDocumentRegionInches = null;

            if (_settingsPanelReady && !target.IsManualRoi)
            {
                ClearSelection();
            }

            UpdateTargetStatus();
        }

        private void TargetOrientation_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var target = GetSelectedTargetSize();
            EnsureTargetOrientationFits(target);

            _lastDocumentRegionInches = null;

            if (_settingsPanelReady && !target.IsManualRoi)
            {
                ClearSelection();
            }

            UpdateTargetStatus();
        }

        private void EnsureTargetOrientationFits(ScanTargetSize target)
        {
            if (_cmbTargetOrientation == null)
            {
                return;
            }

            _cmbTargetOrientation.Enabled = target.HasFixedRegion;
            if (!target.HasFixedRegion || target.GetRegion(GetSelectedTargetOrientation()) != null)
            {
                return;
            }

            if (target.GetRegion(ScanOrientation.Portrait) != null)
            {
                SelectByValue(_cmbTargetOrientation, ScanOrientation.Portrait);
            }
            else if (target.GetRegion(ScanOrientation.Landscape) != null)
            {
                SelectByValue(_cmbTargetOrientation, ScanOrientation.Landscape);
            }
        }

        private void UpdateTargetStatus()
        {
            if (!_settingsPanelReady)
            {
                return;
            }

            var target = GetSelectedTargetSize();
            if (target.IsManualRoi)
            {
                _lblStatus.Text = "Target size: ROI / manual selection. Preview the bed, drag a region, then scan.";
                return;
            }

            if (target.GetRegion(GetSelectedTargetOrientation()) is RectangleF region)
            {
                _lblStatus.Text = $"Target size: {target.Text}, {GetSelectedTargetOrientation().ToString().ToLowerInvariant()} - {region.Width:0.##}\" x {region.Height:0.##}\" from the scanner bed origin.";
                return;
            }

            _lblStatus.Text = "Target size: full scanner bed.";
        }
        /// <summary>Repopulates the format list for the selected scan type.</summary>
        private void RebuildFormatList(DocumentType scanType)
        {
            var current = GetSelectedValue(_cmbFormat, scanType == DocumentType.Document ? OutputFormat.Pdf : OutputFormat.Jpeg);

            _cmbFormat.SelectedIndexChanged -= Format_SelectedIndexChanged;
            _cmbFormat.Items.Clear();
            if (scanType == DocumentType.Document)
            {
                _cmbFormat.Items.Add(new Option<OutputFormat>(OutputFormat.Pdf, "PDF (.pdf)"));
            }
            else
            {
                _cmbFormat.Items.Add(new Option<OutputFormat>(OutputFormat.Jpeg, "JPEG (.jpg)"));
                _cmbFormat.Items.Add(new Option<OutputFormat>(OutputFormat.Png, "PNG (.png)"));
                _cmbFormat.Items.Add(new Option<OutputFormat>(OutputFormat.Tiff, "TIFF (.tif)"));
                _cmbFormat.Items.Add(new Option<OutputFormat>(OutputFormat.Bmp, "Bitmap (.bmp)"));
            }
            _cmbFormat.SelectedIndexChanged += Format_SelectedIndexChanged;

            SelectByValue(_cmbFormat, scanType == DocumentType.Document ? OutputFormat.Pdf : current);
            if (_cmbFormat.SelectedIndex < 0)
            {
                _cmbFormat.SelectedIndex = 0;
            }
            _cmbFormat.Enabled = _cmbFormat.Items.Count > 1;
        }

        /// <summary>
        /// Enables the append/multi-page controls only when they apply
        /// (document scan type saving to PDF) and refreshes the page counter.
        /// </summary>
        private void UpdateDocumentControls()
        {
            if (!_settingsPanelReady || _updatingDocumentControls)
            {
                return;
            }

            _updatingDocumentControls = true;
            try
            {
                var isDocument = GetSelectedValue(_cmbScanType, DocumentType.Document) == DocumentType.Document;
                var isPdf = GetSelectedValue(_cmbFormat, OutputFormat.Jpeg) == OutputFormat.Pdf;
                var canAppend = isDocument && isPdf;

                _chkAppend.Enabled = canAppend;
                if (!canAppend && _chkAppend.Checked)
                {
                    _chkAppend.Checked = false;
                }

                _btnNewDocument.Enabled = _pages.Count > 0;
                UpdatePagesLabel();
            }
            finally
            {
                _updatingDocumentControls = false;
            }
        }

        private void UpdatePagesLabel()
        {
            _lblPages.Text = _pages.Count == 1 ? "Pages: 1" : $"Pages: {_pages.Count}";
        }

        private ScanSettings BuildScanSettings()
        {
            var target = GetSelectedTargetSize();
            var targetRegion = GetSelectedTargetRegionInches();
            var selectedRegion = ResolveRegionInches();
            var rememberedRegion = _chkAppend.Checked ? _lastDocumentRegionInches : null;
            var region = target.IsManualRoi
                ? selectedRegion ?? rememberedRegion
                : targetRegion;

            return new ScanSettings
            {
                DocumentType = GetSelectedValue(_cmbScanType, DocumentType.Document),
                ColorMode = GetSelectedValue(_cmbColorMode, ColorMode.Color),
                HighBitDepth = GetSelectedValue(_cmbBitDepth, false),
                Dpi = GetSelectedValue(_cmbResolution, 300),
                OutputFormat = GetSelectedValue(_cmbFormat, OutputFormat.Pdf),
                Brightness = (int)_numBrightness.Value,
                Contrast = (int)_numContrast.Value,
                RegionInches = region
            };
        }

        private ScanTargetSize GetSelectedTargetSize()
        {
            return GetSelectedValue(_cmbTargetSize, ScanTargetSize.FullBed);
        }

        private ScanOrientation GetSelectedTargetOrientation()
        {
            return GetSelectedValue(_cmbTargetOrientation, ScanOrientation.Portrait);
        }

        private RectangleF? GetSelectedTargetRegionInches()
        {
            var target = GetSelectedTargetSize();
            var orientation = GetSelectedTargetOrientation();
            return target.GetRegion(orientation)
                ?? target.GetRegion(ScanOrientation.Portrait)
                ?? target.GetRegion(ScanOrientation.Landscape);
        }
        /// <summary>
        /// Maps the on-screen selection (a fraction of the current image) onto the
        /// physical bed rectangle that image represents, yielding scan-region inches.
        /// </summary>
        private RectangleF? ResolveRegionInches()
        {
            if (_selectionFraction is not RectangleF fraction || _currentBedRectInches is not RectangleF bed)
            {
                return null;
            }

            return new RectangleF(
                bed.X + fraction.X * bed.Width,
                bed.Y + fraction.Y * bed.Height,
                fraction.Width * bed.Width,
                fraction.Height * bed.Height);
        }

        private static void SelectByValue<T>(ComboBox combo, T value)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is Option<T> option && EqualityComparer<T>.Default.Equals(option.Value, value))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private static T GetSelectedValue<T>(ComboBox combo, T fallback)
        {
            return combo.SelectedItem is Option<T> option ? option.Value : fallback;
        }

        private sealed class Option<T>
        {
            public Option(T value, string text)
            {
                Value = value;
                Text = text;
            }

            public T Value { get; }
            public string Text { get; }
            public override string ToString() => Text;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await InitializeScannerAsync();
        }

        private async Task InitializeScannerAsync()
        {
            SetScannerControlsEnabled(false);
            _btnRefresh.Enabled = false;
            _lblStatus.Text = "Initializing scanner backends...";

            await Task.Yield();

            try
            {
                _scannerManager = new ScannerManager();
                _scannerManager.StateChanged += OnStateChanged;
                _scannerManager.ImageScanned += OnImageScanned;

                _lblStatus.Text = "Searching for Windows WIA scanners...";
                LoadScanners();
            }
            catch (Exception ex)
            {
                SetScannerControlsEnabled(false);
                _btnRefresh.Enabled = true;
                _lblStatus.Text = "Scanner initialization failed.";
                MessageBox.Show($"Failed to initialize scanner backends: {ex.Message}", "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadScanners()
        {
            if (_scannerManager == null)
            {
                _lblStatus.Text = "Scanner manager is not ready.";
                SetScannerControlsEnabled(false);
                _btnRefresh.Enabled = true;
                return;
            }

            var devices = _scannerManager.GetDevices(Handle);
            _cmbScanners.Items.Clear();
            foreach (var device in devices)
            {
                _cmbScanners.Items.Add(device);
            }

            if (_cmbScanners.Items.Count > 0)
            {
                _cmbScanners.SelectedIndex = 0;
                SetScannerControlsEnabled(true);
                _btnRefresh.Enabled = true;
                if (_cmbScanners.Items[_cmbScanners.SelectedIndex] is ScannerDevice selectedDevice)
                {
                    var backendLabel = selectedDevice.Backend == ScannerBackend.Wia ? "Windows WIA" : "EPSON TWAIN fallback";
                    _lblStatus.Text = $"Ready - using {backendLabel}.";
                }
            }
            else
            {
                SetScannerControlsEnabled(false);
                _btnRefresh.Enabled = true;
                _lblStatus.Text = "No scanners found. Confirm the scanner is powered on and the Windows/WIA driver is installed.";
            }
        }

        private async void BtnRefresh_Click(object? sender, EventArgs e)
        {
            if (_scannerManager == null)
            {
                await InitializeScannerAsync();
                return;
            }

            _lblStatus.Text = "Refreshing scanner list...";
            LoadScanners();
        }

        private void BtnPreview_Click(object? sender, EventArgs e)
        {
            StartAcquisition(ScanMode.Preview);
        }

        private void BtnScan_Click(object? sender, EventArgs e)
        {
            StartAcquisition(ScanMode.Full);
        }

        private void StartAcquisition(ScanMode mode)
        {
            if (_scannerManager == null)
            {
                MessageBox.Show("Scanner backends have not initialized yet.", "Scanner Not Ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_cmbScanners.SelectedItem is not ScannerDevice selectedDevice)
            {
                MessageBox.Show("Please select a scanner.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _isAcquiring = true;
                _currentActionName = mode == ScanMode.Preview ? "preview" : "scan";
                _lblStatus.Text = mode == ScanMode.Preview ? "Starting preview..." : "Starting scan...";
                SetScannerControlsEnabled(false);
                _btnRefresh.Enabled = false;

                var settings = BuildScanSettings();

                // Remember the region so multi-page appends can reuse it after the
                // on-screen selection is cleared from the committed page view. The
                // software crop itself keys off settings.RegionInches directly.
                if (settings.RegionInches is RectangleF region)
                {
                    _lastDocumentRegionInches = region;
                }

                _lastAcquireSettings = settings;
                _lastWasPreview = mode == ScanMode.Preview;

                if (mode == ScanMode.Preview)
                {
                    _scannerManager.Preview(selectedDevice, settings);
                }
                else
                {
                    _scannerManager.Scan(selectedDevice, settings);
                }
            }
            catch (Exception ex)
            {
                _isAcquiring = false;
                MessageBox.Show($"Failed to start {_currentActionName}: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetScannerControlsEnabled(true);
                _btnRefresh.Enabled = true;
                _lblStatus.Text = $"{Capitalize(_currentActionName)} failed.";
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            // Save the accumulated document pages, or the single displayed image.
            var pages = _pages.Count > 0
                ? _pages
                : (_pictureBox.Image != null ? new List<Image> { _pictureBox.Image } : new List<Image>());
            if (pages.Count == 0)
            {
                return;
            }

            var settings = BuildScanSettings();

            using var sfd = new SaveFileDialog
            {
                Filter = "PDF Document|*.pdf|JPEG Image|*.jpg|PNG Image|*.png|TIFF Image|*.tif|Bitmap Image|*.bmp",
                FilterIndex = SaveFilterIndex(settings.OutputFormat, pages.Count),
                Title = pages.Count > 1 ? "Save Scanned Document" : "Save Scanned Image",
                FileName = (pages.Count > 1 ? "Document" : "Scan") + SaveExtension(settings, pages.Count),
                OverwritePrompt = true
            };

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            try
            {
                var isPdf = Path.GetExtension(sfd.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                if (isPdf)
                {
                    PdfImageWriter.Save(pages, sfd.FileName);
                }
                else
                {
                    // Non-PDF containers hold a single image; save the first/only page.
                    var format = GetImageFormatForFile(sfd.FileName, settings.ImageFormat);
                    SaveImageToFile(pages[0], sfd.FileName, format);
                }

                _lblStatus.Text = $"Saved {(pages.Count > 1 ? $"{pages.Count} pages" : "image")} to {Path.GetFileName(sfd.FileName)}.";
                MessageBox.Show("Saved successfully.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is ExternalException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                _lblStatus.Text = "Save failed.";
                MessageBox.Show($"Failed to save: {ex.Message}", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static int SaveFilterIndex(OutputFormat format, int pageCount)
        {
            // Filter order: PDF, JPEG, PNG, TIFF, BMP (1-based).
            if (pageCount > 1)
            {
                return 1; // multi-page must be PDF
            }

            return format switch
            {
                OutputFormat.Pdf => 1,
                OutputFormat.Jpeg => 2,
                OutputFormat.Png => 3,
                OutputFormat.Tiff => 4,
                OutputFormat.Bmp => 5,
                _ => 2
            };
        }

        private static string SaveExtension(ScanSettings settings, int pageCount)
        {
            return pageCount > 1 ? ".pdf" : settings.FileExtension;
        }

        private static ImageFormat GetImageFormatForFile(string fileName, ImageFormat fallback)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension switch
            {
                ".png" => ImageFormat.Png,
                ".tif" or ".tiff" => ImageFormat.Tiff,
                ".bmp" => ImageFormat.Bmp,
                ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                _ => fallback
            };
        }

        private static void SaveImageToFile(Image sourceImage, string fileName, ImageFormat format)
        {
            using var stream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);

            // Preserve high bit depth (e.g. 48-bit film scans) when saving to a
            // format that retains it. Flattening through a 24bpp surface would
            // discard the extra tonal range that made high-depth scanning worth it.
            if (IsHighBitDepth(sourceImage.PixelFormat)
                && (format.Equals(ImageFormat.Tiff) || format.Equals(ImageFormat.Png)))
            {
                sourceImage.Save(stream, format);
                return;
            }

            using var bitmap = new Bitmap(sourceImage.Width, sourceImage.Height, PixelFormat.Format24bppRgb);
            bitmap.SetResolution(sourceImage.HorizontalResolution, sourceImage.VerticalResolution);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.DrawImage(sourceImage, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }

            bitmap.Save(stream, format);
        }

        private static bool IsHighBitDepth(PixelFormat format)
        {
            return format == PixelFormat.Format48bppRgb
                || format == PixelFormat.Format64bppArgb
                || format == PixelFormat.Format64bppPArgb
                || format == PixelFormat.Format16bppGrayScale;
        }

        private void OnStateChanged(string stateMsg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnStateChanged(stateMsg)));
                return;
            }

            if (!stateMsg.StartsWith("TWAIN state:", StringComparison.OrdinalIgnoreCase) || _isAcquiring)
            {
                _lblStatus.Text = stateMsg;
            }

            if (_isAcquiring && _scannerManager?.Session.State == 3)
            {
                _isAcquiring = false;
                SetScannerControlsEnabled(true);
                _btnRefresh.Enabled = true;
                _lblStatus.Text = $"{Capitalize(_currentActionName)} ended without receiving an image.";
                return;
            }

            if (!_isAcquiring && (_scannerManager?.Session.State == 4 || _scannerManager?.Session.State == 3))
            {
                SetScannerControlsEnabled(true);
                _btnRefresh.Enabled = true;
            }
        }

        private void OnImageScanned(Image img)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnImageScanned(img)));
                return;
            }

            var finalImage = EnforceSelectedRegion(img);
            var displayedImage = finalImage;
            var wasCropped = !ReferenceEquals(finalImage, img);

            string statusSuffix;
            if (_lastWasPreview)
            {
                // A preview is transient - the picture box owns and disposes it.
                ShowImage(finalImage, ownedByPictureBox: true);
                statusSuffix = " Drag on the image to select a scan region.";
            }
            else
            {
                // A full scan becomes an app-owned document page (appended or replacing).
                displayedImage = CommitScannedPage(finalImage);
                statusSuffix = _chkAppend.Checked
                    ? $" Added page {_pages.Count}. Press Scan again to reuse this ROI, or Preview to choose a new one."
                    : (wasCropped ? " Cropped to selected region." : string.Empty);
            }

            RecordBedRect(displayedImage);
            var clearSavedRegion = _lastWasPreview && !(GetSelectedTargetSize().IsManualRoi && _lastAcquireSettings?.RegionInches is RectangleF);
            ClearSelection(clearSavedRegion);

            _isAcquiring = false;
            _btnSave.Enabled = true;
            SetScannerControlsEnabled(true);
            _btnRefresh.Enabled = true;
            _lblStatus.Text = $"{Capitalize(_currentActionName)} complete.{statusSuffix}";
        }

        /// <summary>Shows an image, disposing the previous one only if the box owned it.</summary>
        private void ShowImage(Image img, bool ownedByPictureBox)
        {
            var old = _pictureBox.Image;
            _pictureBox.Image = img;
            if (_pictureOwnsImage && old != null && !ReferenceEquals(old, img))
            {
                old.Dispose();
            }
            _pictureOwnsImage = ownedByPictureBox;
        }

        /// <summary>Adds a scanned page to the document, appending or replacing.</summary>
        private Image CommitScannedPage(Image page)
        {
            var documentPage = CreateStableDocumentPage(page);
            if (!ReferenceEquals(documentPage, page))
            {
                page.Dispose();
            }

            var append = _chkAppend.Enabled && _chkAppend.Checked;
            if (append)
            {
                _pages.Add(documentPage);
                ShowImage(documentPage, ownedByPictureBox: false);
            }
            else
            {
                var previous = _pages.ToList();
                _pages.Clear();
                _pages.Add(documentPage);
                ShowImage(documentPage, ownedByPictureBox: false);
                foreach (var old in previous)
                {
                    if (!ReferenceEquals(old, documentPage))
                    {
                        old.Dispose();
                    }
                }
            }

            _btnNewDocument.Enabled = _pages.Count > 0;
            UpdatePagesLabel();
            return documentPage;
        }

        private Image CreateStableDocumentPage(Image source)
        {
            var dpi = _lastAcquireSettings is { Dpi: > 0 } settings ? settings.Dpi : 300;
            var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            bitmap.SetResolution(dpi, dpi);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }

            return bitmap;
        }

        /// <summary>Discards all accumulated pages and starts a fresh document.</summary>
        private void ClearAllPages()
        {
            if (!_pictureOwnsImage)
            {
                // The box is showing a page we are about to dispose.
                _pictureBox.Image = null;
                _pictureOwnsImage = false;
            }

            foreach (var page in _pages)
            {
                page.Dispose();
            }
            _pages.Clear();

            _btnNewDocument.Enabled = false;
            _btnSave.Enabled = _pictureBox.Image != null;
            UpdatePagesLabel();
            _lblStatus.Text = "Started a new document.";
            _pictureBox.Invalidate();
        }

        /// <summary>
        /// Guarantees the result matches the selected region. If the scanner
        /// honored the hardware region the image is already the ROI and is
        /// returned unchanged; if it returned the full bed, we crop in software.
        /// </summary>
        private Image EnforceSelectedRegion(Image img)
        {
            // The requested region (in inches from the bed origin) is the source of
            // truth for both fixed target sizes and manual ROI selections.
            if (_lastAcquireSettings is not { RegionInches: RectangleF region } settings)
            {
                return img;
            }

            // A manual-ROI preview must show the whole bed so a region can be drawn;
            // a fixed target-size preview is cropped so it shows exactly that size.
            if (_lastWasPreview && !GetSelectedTargetSize().HasFixedRegion)
            {
                return img;
            }

            // Work from the image's OWN resolution, not the DPI we requested:
            // scanners often honor the region but return it at a different DPI
            // (e.g. clamping a low-DPI preview up to their minimum). Assuming the
            // requested DPI here is what made the crop grab only a tiny corner.
            float fallbackDpi = _lastWasPreview ? ScanSettings.PreviewDpi : (settings.Dpi <= 0 ? 1 : settings.Dpi);
            float dpiX = ResolveImageDpi(img.HorizontalResolution, fallbackDpi);
            float dpiY = ResolveImageDpi(img.VerticalResolution, dpiX);

            float physicalWidth = img.Width / dpiX;
            float physicalHeight = img.Height / dpiY;
            var bed = ScanTargetSize.BedRectInches;

            // Decide whether the scanner already returned just the region, or the
            // full bed, by whichever the measured physical size is closest to.
            // Nearest-match tolerates DPI reporting that is off but proportional,
            // and it distinguishes sizes that share the bed's aspect ratio (A4).
            float distanceToRegion = Math.Abs(physicalWidth - region.Width) + Math.Abs(physicalHeight - region.Height);
            float distanceToBed = Math.Abs(physicalWidth - bed.Width) + Math.Abs(physicalHeight - bed.Height);
            if (distanceToRegion <= distanceToBed)
            {
                return img; // already the requested region
            }

            var cropped = CropRegionPixels(img, region, dpiX, dpiY);
            if (cropped is Bitmap croppedBitmap)
            {
                croppedBitmap.SetResolution(dpiX, dpiY);
            }
            if (!ReferenceEquals(cropped, img))
            {
                img.Dispose();
            }
            return cropped;
        }

        /// <summary>
        /// Crops a full-bed image to a region expressed in inches from the bed
        /// origin, converting to pixels via the image's actual resolution.
        /// </summary>
        private static Image CropRegionPixels(Image img, RectangleF regionInches, float dpiX, float dpiY)
        {
            var x = (int)Math.Round(regionInches.X * dpiX);
            var y = (int)Math.Round(regionInches.Y * dpiY);
            var w = (int)Math.Round(regionInches.Width * dpiX);
            var h = (int)Math.Round(regionInches.Height * dpiY);

            x = Math.Max(0, Math.Min(x, img.Width - 1));
            y = Math.Max(0, Math.Min(y, img.Height - 1));
            w = Math.Max(1, Math.Min(w, img.Width - x));
            h = Math.Max(1, Math.Min(h, img.Height - y));
            var rect = new Rectangle(x, y, w, h);

            // Bitmap.Clone preserves the source pixel format (e.g. 48-bit depth).
            if (img is Bitmap bitmap)
            {
                try
                {
                    return bitmap.Clone(rect, bitmap.PixelFormat);
                }
                catch (OutOfMemoryException)
                {
                    // Some indexed formats can't be cloned this way; fall through.
                }
            }

            var target = new Bitmap(w, h);
            target.SetResolution(img.HorizontalResolution, img.VerticalResolution);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.DrawImage(img, new Rectangle(0, 0, w, h), rect, GraphicsUnit.Pixel);
            }
            return target;
        }

        /// <summary>
        /// Remembers the physical bed area the freshly acquired image covers, so a
        /// region drawn on it can be translated back into scan-bed inches.
        /// </summary>
        private void RecordBedRect(Image img)
        {
            if (_lastWasPreview && _lastAcquireSettings?.RegionInches is RectangleF previewRegion)
            {
                _currentBedRectInches = previewRegion;
            }
            else if (_lastWasPreview)
            {
                var previewDpiX = ResolveImageDpi(img.HorizontalResolution, ScanSettings.PreviewDpi);
                var previewDpiY = ResolveImageDpi(img.VerticalResolution, previewDpiX);
                _currentBedRectInches = new RectangleF(0, 0,
                    img.Width / previewDpiX,
                    img.Height / previewDpiY);
            }
            else if (_lastAcquireSettings?.RegionInches is RectangleF region)
            {
                _currentBedRectInches = region;
            }
            else if (_lastAcquireSettings is { } settings)
            {
                var dpi = settings.Dpi > 0 ? settings.Dpi : 300;
                _currentBedRectInches = new RectangleF(0, 0,
                    img.Width / (float)dpi,
                    img.Height / (float)dpi);
            }
            else
            {
                _currentBedRectInches = null;
            }
        }

        private static float ResolveImageDpi(float imageDpi, float fallbackDpi)
        {
            return imageDpi > 1f && imageDpi < 10000f ? imageDpi : fallbackDpi;
        }

        // ----- Region-of-interest (ROI) selection -----

        private void PictureBox_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var display = GetImageDisplayRect();
            if (display.IsEmpty || !display.Contains(e.Location))
            {
                return;
            }

            _isSelecting = true;
            _selectStartDisplay = e.Location;
            _selectionDisplay = new Rectangle(e.Location, Size.Empty);
            _pictureBox.Invalidate();
        }

        private void PictureBox_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_isSelecting)
            {
                return;
            }

            var current = ClampToRect(e.Location, GetImageDisplayRect());
            _selectionDisplay = RectFromPoints(_selectStartDisplay, current);
            _pictureBox.Invalidate();
        }

        private void PictureBox_MouseUp(object? sender, MouseEventArgs e)
        {
            if (!_isSelecting)
            {
                return;
            }

            _isSelecting = false;
            var display = GetImageDisplayRect();

            // A tiny drag is treated as a click that clears the region.
            if (display.IsEmpty || _selectionDisplay.Width < 5 || _selectionDisplay.Height < 5)
            {
                ClearSelection();
                return;
            }

            _selectionFraction = new RectangleF(
                (_selectionDisplay.X - display.X) / (float)display.Width,
                (_selectionDisplay.Y - display.Y) / (float)display.Height,
                _selectionDisplay.Width / (float)display.Width,
                _selectionDisplay.Height / (float)display.Height);

            SelectByValue(_cmbTargetSize, ScanTargetSize.ManualRoi);
            _btnClearRegion.Enabled = true;
            _pictureBox.Invalidate();
            UpdateRegionStatus();
        }

        private void PictureBox_Paint(object? sender, PaintEventArgs e)
        {
            if (!_isSelecting && _selectionFraction == null)
            {
                return;
            }

            if (_selectionDisplay.Width <= 0 || _selectionDisplay.Height <= 0)
            {
                return;
            }

            using var fill = new SolidBrush(Color.FromArgb(48, Color.DodgerBlue));
            e.Graphics.FillRectangle(fill, _selectionDisplay);
            using var pen = new Pen(Color.DodgerBlue, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, _selectionDisplay);
        }

        private void SyncSelectionDisplayToImage()
        {
            if (_selectionFraction is not RectangleF fraction)
            {
                _selectionDisplay = Rectangle.Empty;
                return;
            }

            var display = GetImageDisplayRect();
            if (display.IsEmpty)
            {
                return;
            }

            _selectionDisplay = new Rectangle(
                display.X + (int)Math.Round(fraction.X * display.Width),
                display.Y + (int)Math.Round(fraction.Y * display.Height),
                (int)Math.Round(fraction.Width * display.Width),
                (int)Math.Round(fraction.Height * display.Height));
            _pictureBox.Invalidate();
        }

        private void ClearSelection(bool clearSavedRegion = true)
        {
            _isSelecting = false;
            _selectionFraction = null;
            if (clearSavedRegion)
            {
                _lastDocumentRegionInches = null;
            }
            _selectionDisplay = Rectangle.Empty;
            if (_btnClearRegion != null)
            {
                _btnClearRegion.Enabled = false;
            }
            _pictureBox.Invalidate();
        }

        private void UpdateRegionStatus()
        {
            if (ResolveRegionInches() is RectangleF r)
            {
                _lblStatus.Text = $"Scan region: {r.Width:0.00}\" x {r.Height:0.00}\" at ({r.X:0.00}\", {r.Y:0.00}\"). Press Scan to capture it.";
            }
        }

        /// <summary>Rectangle (in client coords) where the zoomed image is drawn.</summary>
        private Rectangle GetImageDisplayRect()
        {
            var img = _pictureBox.Image;
            if (img == null)
            {
                return Rectangle.Empty;
            }

            float cw = _pictureBox.ClientSize.Width;
            float ch = _pictureBox.ClientSize.Height;
            if (cw <= 0 || ch <= 0 || img.Width <= 0 || img.Height <= 0)
            {
                return Rectangle.Empty;
            }

            var scale = Math.Min(cw / img.Width, ch / img.Height);
            var w = (int)Math.Round(img.Width * scale);
            var h = (int)Math.Round(img.Height * scale);
            return new Rectangle((int)((cw - w) / 2), (int)((ch - h) / 2), w, h);
        }

        private static Point ClampToRect(Point p, Rectangle r)
        {
            if (r.IsEmpty)
            {
                return p;
            }

            return new Point(
                Math.Max(r.Left, Math.Min(p.X, r.Right)),
                Math.Max(r.Top, Math.Min(p.Y, r.Bottom)));
        }

        private static Rectangle RectFromPoints(Point a, Point b)
        {
            return new Rectangle(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X),
                Math.Abs(a.Y - b.Y));
        }

        private void SetScannerControlsEnabled(bool enabled)
        {
            var hasScanner = _cmbScanners.SelectedItem is ScannerDevice;
            _cmbScanners.Enabled = enabled;
            _btnPreview.Enabled = enabled && hasScanner;
            _btnScan.Enabled = enabled && hasScanner;
        }

        private static string Capitalize(string value)
        {
            return string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
        }

        private enum ScanOrientation
        {
            Portrait,
            Landscape
        }

        private sealed class ScanTargetSize
        {
            private const float BedWidthInches = 12.2f;
            private const float BedHeightInches = 17.2f;

            public static readonly ScanTargetSize FullBed = new("Full bed - 12.2 x 17.2 in", null);
            public static readonly ScanTargetSize ManualRoi = new("ROI / manual selection", null, isManualRoi: true);

            /// <summary>The scanner's full scan-bed area, in inches from the origin.</summary>
            public static RectangleF BedRectInches => new(0, 0, BedWidthInches, BedHeightInches);

            private ScanTargetSize(string text, RectangleF? regionInches, bool isManualRoi = false)
            {
                Text = text;
                RegionInches = regionInches;
                IsManualRoi = isManualRoi;
            }

            public string Text { get; }
            public RectangleF? RegionInches { get; }
            public bool IsManualRoi { get; }
            public bool HasFixedRegion => RegionInches.HasValue && !IsManualRoi;

            public RectangleF? GetRegion(ScanOrientation orientation)
            {
                if (RegionInches is not RectangleF region)
                {
                    return null;
                }

                var width = region.Width;
                var height = region.Height;
                if (orientation == ScanOrientation.Landscape && height > width)
                {
                    (width, height) = (height, width);
                }
                else if (orientation == ScanOrientation.Portrait && width > height)
                {
                    (width, height) = (height, width);
                }

                return FitsBed(width, height) ? new RectangleF(0, 0, width, height) : null;
            }

            public static ScanTargetSize FromSize(string text, float widthInches, float heightInches)
            {
                if (widthInches > BedWidthInches || heightInches > BedHeightInches)
                {
                    throw new ArgumentOutOfRangeException(nameof(widthInches), "Target size exceeds the EPSON Expression 10000XL scan bed.");
                }

                return new ScanTargetSize(text, new RectangleF(0, 0, widthInches, heightInches));
            }

            private static bool FitsBed(float widthInches, float heightInches)
            {
                return widthInches <= BedWidthInches && heightInches <= BedHeightInches;
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _scannerManager?.Dispose();
            foreach (var page in _pages)
            {
                page.Dispose();
            }
            _pages.Clear();
            base.OnFormClosing(e);
        }
    }
}


















