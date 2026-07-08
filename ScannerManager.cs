using NTwain;
using NTwain.Data;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ScannerPro
{
    public enum ScanMode
    {
        Preview,
        Full
    }

    public enum ScannerBackend
    {
        Wia,
        Twain
    }

    public sealed class ScannerDevice
    {
        private ScannerDevice(string name, ScannerBackend backend, DataSource? twainSource, string? wiaDeviceId)
        {
            Name = name;
            Backend = backend;
            TwainSource = twainSource;
            WiaDeviceId = wiaDeviceId;
        }

        public string Name { get; }
        public ScannerBackend Backend { get; }
        public DataSource? TwainSource { get; }
        public string? WiaDeviceId { get; }
        public string DisplayName => Backend == ScannerBackend.Wia ? $"{Name} (Windows WIA)" : $"{Name} (EPSON TWAIN fallback)";

        public static ScannerDevice FromTwain(DataSource source) => new(source.Name, ScannerBackend.Twain, source, null);

        public static ScannerDevice FromWia(string name, string deviceId) => new(name, ScannerBackend.Wia, null, deviceId);

        public override string ToString() => DisplayName;
    }

    public class ScannerManager : IDisposable
    {
        private const int WiaScannerDeviceType = 1;

        // WIA item property ids (wiadef.h).
        private const int WiaIpsCurIntent = 6146;
        private const int WiaIpsXRes = 6147;
        private const int WiaIpsYRes = 6148;
        private const int WiaIpaDataType = 4103;
        private const int WiaIpaDepth = 4104;
        private const int WiaIpsBrightness = 6154;
        private const int WiaIpsContrast = 6155;

        // WIA scan-region properties, in pixels at the current resolution.
        private const int WiaIpsXPos = 6149;
        private const int WiaIpsYPos = 6150;
        private const int WiaIpsXExtent = 6151;
        private const int WiaIpsYExtent = 6152;

        // WIA_IPS_CUR_INTENT flags.
        private const int WiaIntentColor = 0x00000001;
        private const int WiaIntentGrayscale = 0x00000002;
        private const int WiaIntentText = 0x00000004;

        // WIA_IPA_DATATYPE values.
        private const int WiaDataThreshold = 0;
        private const int WiaDataGrayscale = 2;
        private const int WiaDataColor = 3;

        // WIA image format GUIDs (wiadef.h). BMP is a lossless intermediate;
        // JPEG is a broadly supported fallback for drivers that reject BMP.
        private const string WiaFormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
        private const string WiaFormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

        private readonly TwainSession _twainSession;
        private WindowsFormsMessageLoopHook? _messageLoopHook;
        private IntPtr _windowHandle;

        public TwainSession Session => _twainSession;
        public bool IsTwainOpen => _twainSession.State >= 3;

        public event Action<Image>? ImageScanned;
        public event Action<string>? StateChanged;

        public ScannerManager()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
            _twainSession = new TwainSession(appId);
            _twainSession.TransferReady += OnTransferReady;
            _twainSession.DataTransferred += OnDataTransferred;
            _twainSession.StateChanged += OnStateChanged;
        }

        public void OpenSession(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException("A valid window handle is required before opening TWAIN.", nameof(windowHandle));
            }

            if (IsTwainOpen)
            {
                return;
            }

            _windowHandle = windowHandle;
            _messageLoopHook = new WindowsFormsMessageLoopHook(windowHandle);
            _twainSession.Open(_messageLoopHook);
        }

        public void CloseSession()
        {
            if (_twainSession.State == 4)
            {
                _twainSession.CurrentSource?.Close();
            }
            if (_twainSession.State == 3)
            {
                _twainSession.Close();
            }
        }

        public IList<ScannerDevice> GetDevices(IntPtr windowHandle)
        {
            var wiaDevices = GetWiaDevices().ToList();
            if (wiaDevices.Count > 0)
            {
                CloseSession();
                return wiaDevices
                    .OrderBy(device => device.Name)
                    .ToList();
            }

            StateChanged?.Invoke("No Windows WIA scanner found. Loading EPSON TWAIN fallback...");
            try
            {
                OpenSession(windowHandle);
            }
            catch (Exception ex)
            {
                StateChanged?.Invoke($"TWAIN fallback unavailable: {ex.Message}");
                return new List<ScannerDevice>();
            }

            if (_twainSession.State < 3)
            {
                return new List<ScannerDevice>();
            }

            return _twainSession.GetSources()
                .OrderBy(source => source.Name)
                .Select(ScannerDevice.FromTwain)
                .ToList();
        }

        public void Preview(ScannerDevice device, ScanSettings settings)
        {
            Acquire(device, ScanMode.Preview, settings);
        }

        public void Scan(ScannerDevice device, ScanSettings settings)
        {
            Acquire(device, ScanMode.Full, settings);
        }

        private void Acquire(ScannerDevice device, ScanMode mode, ScanSettings settings)
        {
            if (device.Backend == ScannerBackend.Wia)
            {
                CloseSession();
                AcquireWithWia(device, mode, settings);
                return;
            }

            if (device.TwainSource == null)
            {
                throw new InvalidOperationException("The selected TWAIN source is no longer available.");
            }

            AcquireWithTwain(device.TwainSource, mode, settings);
        }

        private void AcquireWithWia(ScannerDevice device, ScanMode mode, ScanSettings settings)
        {
            if (string.IsNullOrWhiteSpace(device.WiaDeviceId))
            {
                throw new InvalidOperationException("The selected WIA scanner is missing its device id.");
            }

            object? deviceInfo = null;
            object? connectedDevice = null;
            object? scannerItem = null;
            object? imageFile = null;

            try
            {
                var actionName = mode == ScanMode.Preview ? "preview" : "scan";
                var dpi = settings.ResolveDpi(mode);

                StateChanged?.Invoke($"Starting {actionName} through Windows WIA at {dpi} DPI...");

                deviceInfo = FindWiaDeviceInfo(device.WiaDeviceId);
                connectedDevice = InvokeCom(deviceInfo, "Connect");
                scannerItem = GetIndexedComProperty(GetComProperty(connectedDevice, "Items"), 1);

                ApplyWiaSettings(scannerItem, settings, dpi);

                imageFile = TransferWia(scannerItem);
                var fileData = GetComProperty(imageFile, "FileData");
                byte[] imageBytes = ToByteArray(GetComProperty(fileData, "BinaryData"));

                using var stream = new MemoryStream(imageBytes);
                using var image = Image.FromStream(stream);
                ImageScanned?.Invoke((Image)image.Clone());
            }
            catch (COMException ex) when (IsWiaBusy(ex))
            {
                throw new InvalidOperationException("The scanner is busy. Close EPSON Scan and any scanner dialogs, wait a few seconds, then press Refresh and try Preview again. If it stays busy, power-cycle the scanner.", ex);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException($"Windows WIA could not acquire from this scanner: {ex.Message}", ex);
            }
            finally
            {
                ReleaseComObject(imageFile);
                ReleaseComObject(scannerItem);
                ReleaseComObject(connectedDevice);
                ReleaseComObject(deviceInfo);
            }
        }

        private void ApplyWiaSettings(object scannerItem, ScanSettings settings, int dpi)
        {
            var (intent, dataType) = settings.ColorMode switch
            {
                ColorMode.Grayscale => (WiaIntentGrayscale, WiaDataGrayscale),
                ColorMode.BlackWhite => (WiaIntentText, WiaDataThreshold),
                _ => (WiaIntentColor, WiaDataColor)
            };

            TrySetWiaProperty(GetComProperty(scannerItem, "Properties"), WiaIpsCurIntent, intent);
            TrySetWiaProperty(GetComProperty(scannerItem, "Properties"), WiaIpaDataType, dataType);
            TrySetWiaProperty(GetComProperty(scannerItem, "Properties"), WiaIpaDepth, settings.BitDepth);
            TrySetWiaProperty(GetComProperty(scannerItem, "Properties"), WiaIpsXRes, dpi);
            TrySetWiaProperty(GetComProperty(scannerItem, "Properties"), WiaIpsYRes, dpi);

            ApplyWiaRegion(scannerItem, settings, dpi);

            // The UI exposes -100..100; WIA drivers typically use -1000..1000.
            // Leave the scanner default untouched when the user hasn't adjusted it.
            if (settings.Brightness != 0)
            {
                TrySetWiaProperty(GetComProperty(scannerItem, "Properties"), WiaIpsBrightness, settings.Brightness * 10);
            }
            if (settings.Contrast != 0)
            {
                TrySetWiaProperty(GetComProperty(scannerItem, "Properties"), WiaIpsContrast, settings.Contrast * 10);
            }
        }

        private void ApplyWiaRegion(object scannerItem, ScanSettings settings, int dpi)
        {
            if (settings.RegionInches is not RectangleF region)
            {
                return;
            }

            // WIA region properties are in pixels at the current resolution.
            // Set origin before extent; several drivers validate extent against
            // the current origin and reject/off-by-shift windows if this is reversed.
            var xExtent = Math.Max(1, (int)Math.Round(region.Width * dpi));
            var yExtent = Math.Max(1, (int)Math.Round(region.Height * dpi));
            var xPos = Math.Max(0, (int)Math.Round(region.X * dpi));
            var yPos = Math.Max(0, (int)Math.Round(region.Y * dpi));
            var properties = GetComProperty(scannerItem, "Properties");

            TrySetWiaProperty(properties, WiaIpsXPos, xPos);
            TrySetWiaProperty(properties, WiaIpsYPos, yPos);
            TrySetWiaProperty(properties, WiaIpsXExtent, xExtent);
            TrySetWiaProperty(properties, WiaIpsYExtent, yExtent);
            ReleaseComObject(properties);

            StateChanged?.Invoke($"Scanning region {region.Width:0.00}\" x {region.Height:0.00}\" at {dpi} DPI...");
        }

        private object TransferWia(object scannerItem)
        {
            // Prefer a lossless BMP transfer for archival/film quality; fall back
            // to JPEG for drivers that don't support the BMP intermediate.
            try
            {
                return InvokeCom(scannerItem, "Transfer", WiaFormatBmp);
            }
            catch (COMException)
            {
                return InvokeCom(scannerItem, "Transfer", WiaFormatJpeg);
            }
        }

        private void AcquireWithTwain(DataSource source, ScanMode mode, ScanSettings settings)
        {
            if (_twainSession.State == 4)
            {
                if (_twainSession.CurrentSource != null && _twainSession.CurrentSource.Name != source.Name)
                {
                    _twainSession.CurrentSource.Close();
                }
            }

            if (_twainSession.State == 3)
            {
                StateChanged?.Invoke($"Opening {source.Name}...");
                source.Open();
            }

            var currentSource = _twainSession.CurrentSource ?? source;
            if (_twainSession.State != 4)
            {
                throw new InvalidOperationException("TWAIN source could not be opened.");
            }

            ApplyTwainScanProfile(currentSource, mode, settings);

            var actionName = mode == ScanMode.Preview ? "preview" : "scan";
            StateChanged?.Invoke($"Starting {actionName} through TWAIN NoUI...");
            currentSource.Enable(SourceEnableMode.NoUI, false, _windowHandle);
        }

        private IEnumerable<ScannerDevice> GetWiaDevices()
        {
            var devices = new List<ScannerDevice>();
            object? deviceManager = null;

            try
            {
                deviceManager = CreateComObject("WIA.DeviceManager");
                var deviceInfos = GetComProperty(deviceManager, "DeviceInfos");
                foreach (var deviceInfo in EnumerateComCollection(deviceInfos))
                {
                    if (Convert.ToInt32(GetComProperty(deviceInfo, "Type")) != WiaScannerDeviceType)
                    {
                        ReleaseComObject(deviceInfo);
                        continue;
                    }

                    var deviceId = Convert.ToString(GetComProperty(deviceInfo, "DeviceID")) ?? string.Empty;
                    var properties = GetComProperty(deviceInfo, "Properties");
                    var name = GetWiaPropertyValue(properties, "Name")
                        ?? GetWiaPropertyValue(properties, "Description")
                        ?? deviceId;

                    if (!string.IsNullOrWhiteSpace(deviceId))
                    {
                        devices.Add(ScannerDevice.FromWia(name, deviceId));
                    }

                    ReleaseComObject(properties);
                    ReleaseComObject(deviceInfo);
                }

                ReleaseComObject(deviceInfos);
            }
            catch (COMException ex)
            {
                StateChanged?.Invoke($"Windows WIA is not available: {ex.Message}");
            }
            catch
            {
                // WIA is optional. TWAIN fallback may still be available.
            }
            finally
            {
                ReleaseComObject(deviceManager);
            }

            return devices;
        }

        private object FindWiaDeviceInfo(string deviceId)
        {
            object? deviceManager = null;
            object? deviceInfos = null;

            try
            {
                deviceManager = CreateComObject("WIA.DeviceManager");
                deviceInfos = GetComProperty(deviceManager, "DeviceInfos");
                foreach (var deviceInfo in EnumerateComCollection(deviceInfos))
                {
                    if (string.Equals(Convert.ToString(GetComProperty(deviceInfo, "DeviceID")), deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return deviceInfo;
                    }

                    ReleaseComObject(deviceInfo);
                }
            }
            finally
            {
                ReleaseComObject(deviceInfos);
                ReleaseComObject(deviceManager);
            }

            throw new InvalidOperationException("The selected WIA scanner is no longer available.");
        }

        private static object CreateComObject(string progId)
        {
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new InvalidOperationException($"COM component {progId} is not registered on this computer.");

            return Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Could not create COM component {progId}.");
        }

        private static string? GetWiaPropertyValue(object properties, string propertyName)
        {
            try
            {
                var property = GetIndexedComProperty(properties, propertyName);
                var value = Convert.ToString(GetComProperty(property, "Value"));
                ReleaseComObject(property);
                return value;
            }
            catch
            {
                try
                {
                    foreach (var property in EnumerateComCollection(properties))
                    {
                        if (string.Equals(Convert.ToString(GetComProperty(property, "Name")), propertyName, StringComparison.OrdinalIgnoreCase))
                        {
                            var value = Convert.ToString(GetComProperty(property, "Value"));
                            ReleaseComObject(property);
                            return value;
                        }

                        ReleaseComObject(property);
                    }
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static void TrySetWiaProperty(object properties, object propertyId, object value)
        {
            object? property = null;
            try
            {
                property = GetIndexedComProperty(properties, propertyId);
                SetComProperty(property, "Value", value);
            }
            catch
            {
                // WIA drivers vary. Use defaults when a property is read-only or unsupported.
            }
            finally
            {
                ReleaseComObject(property);
            }
        }

        private static IEnumerable<object> EnumerateComCollection(object collection)
        {
            if (collection is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null)
                    {
                        yield return item;
                    }
                }
            }
        }

        private static object GetComProperty(object target, string propertyName)
        {
            return target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, target, null)
                ?? throw new InvalidOperationException($"COM property {propertyName} returned no value.");
        }

        private static object GetIndexedComProperty(object target, object index)
        {
            return target.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, target, new[] { index })
                ?? throw new InvalidOperationException("COM indexed property returned no value.");
        }

        private static object InvokeCom(object target, string methodName, params object[] args)
        {
            return target.GetType().InvokeMember(methodName, BindingFlags.InvokeMethod, null, target, args)
                ?? throw new InvalidOperationException($"COM method {methodName} returned no value.");
        }

        private static void SetComProperty(object target, string propertyName, object value)
        {
            target.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, null, target, new[] { value });
        }

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        private static bool IsWiaBusy(COMException ex)
        {
            return ex.ErrorCode == unchecked((int)0x80210006)
                || ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] ToByteArray(object binaryData)
        {
            if (binaryData is byte[] bytes)
            {
                return bytes;
            }

            if (binaryData is Array array)
            {
                var bytesFromArray = new byte[array.Length];
                for (var i = 0; i < array.Length; i++)
                {
                    bytesFromArray[i] = Convert.ToByte(array.GetValue(i));
                }

                return bytesFromArray;
            }

            throw new InvalidOperationException("WIA returned image data in an unsupported format.");
        }

        private void ApplyTwainScanProfile(DataSource source, ScanMode mode, ScanSettings settings)
        {
            var dpi = settings.ResolveDpi(mode);
            var capabilities = source.Capabilities;

            TrySetTwainCapability(capabilities, "ICapXferMech", "Native");
            TrySetTwainCapability(capabilities, "ICapUnits", "Inches");

            // Candidate names vary by driver; the first accepted value wins.
            var pixelCandidates = settings.ColorMode switch
            {
                ColorMode.Grayscale => new object[] { "Gray", "Grayscale" },
                ColorMode.BlackWhite => new object[] { "BlackWhite", "BW", "BlackAndWhite" },
                _ => new object[] { "RGB", "Color" }
            };
            TrySetTwainCapability(capabilities, "ICapPixelType", pixelCandidates);

            // Bit depth is per driver: some report total bpp (24/48), others
            // report bits per channel (8/16). Offer both so one is accepted.
            var depthCandidates = settings.ColorMode switch
            {
                ColorMode.BlackWhite => new object[] { 1 },
                ColorMode.Grayscale => settings.HighBitDepth ? new object[] { 16 } : new object[] { 8 },
                _ => settings.HighBitDepth ? new object[] { 48, 16 } : new object[] { 24, 8 }
            };
            TrySetTwainCapability(capabilities, "ICapBitDepth", depthCandidates);

            TrySetTwainCapability(capabilities, "ICapXResolution", (float)dpi, (double)dpi, dpi);
            TrySetTwainCapability(capabilities, "ICapYResolution", (float)dpi, (double)dpi, dpi);

            TrySetTwainRegion(source, settings);

            // TWAIN brightness/contrast use a -1000..1000 fixed-point range.
            if (settings.Brightness != 0)
            {
                var brightness = settings.Brightness * 10f;
                TrySetTwainCapability(capabilities, "ICapBrightness", brightness, (double)brightness, (int)brightness);
            }
            if (settings.Contrast != 0)
            {
                var contrast = settings.Contrast * 10f;
                TrySetTwainCapability(capabilities, "ICapContrast", contrast, (double)contrast, (int)contrast);
            }
        }

        private void TrySetTwainRegion(DataSource source, ScanSettings settings)
        {
            if (settings.RegionInches is not RectangleF region)
            {
                return;
            }

            try
            {
                // ICapUnits is negotiated to Inches above, so the frame is in inches.
                var layout = new TWImageLayout
                {
                    Frame = new TWFrame
                    {
                        Left = region.Left,
                        Top = region.Top,
                        Right = region.Right,
                        Bottom = region.Bottom
                    }
                };

                source.DGImage.ImageLayout.Set(layout);
                StateChanged?.Invoke($"Scanning region {region.Width:0.00}\" x {region.Height:0.00}\"...");
            }
            catch
            {
                // Frame selection is best-effort; fall back to a full-frame scan.
            }
        }

        private void TrySetTwainCapability(object capabilities, string propertyName, params object[] candidateValues)
        {
            try
            {
                var capability = capabilities.GetType().GetProperty(propertyName)?.GetValue(capabilities);
                if (capability == null)
                {
                    return;
                }

                var setValueMethods = capability.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(method => method.Name == "SetValue" && method.GetParameters().Length == 1)
                    .ToList();

                foreach (var candidateValue in candidateValues)
                {
                    foreach (var method in setValueMethods)
                    {
                        var parameterType = method.GetParameters()[0].ParameterType;
                        if (!TryConvertCapabilityValue(candidateValue, parameterType, out var convertedValue))
                        {
                            continue;
                        }

                        try
                        {
                            method.Invoke(capability, new[] { convertedValue });
                            return;
                        }
                        catch
                        {
                            // Drivers often reject individual caps. Keep the acquisition path moving.
                        }
                    }
                }
            }
            catch
            {
                // Capability negotiation is best-effort for broad TWAIN driver compatibility.
            }
        }

        private bool TryConvertCapabilityValue(object value, Type targetType, out object? convertedValue)
        {
            convertedValue = null;

            if (value != null && targetType.IsInstanceOfType(value))
            {
                convertedValue = value;
                return true;
            }

            if (targetType.IsEnum && value is string enumName)
            {
                try
                {
                    convertedValue = Enum.Parse(targetType, enumName, ignoreCase: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (targetType.FullName == "NTwain.Data.TWFix32")
            {
                var numericValue = Convert.ToSingle(value);
                var conversion = targetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method => method.Name == "op_Implicit"
                        && method.ReturnType == targetType
                        && method.GetParameters().Length == 1
                        && method.GetParameters()[0].ParameterType == typeof(float));

                if (conversion != null)
                {
                    convertedValue = conversion.Invoke(null, new object[] { numericValue });
                    return true;
                }
            }

            if (targetType == typeof(float))
            {
                convertedValue = Convert.ToSingle(value);
                return true;
            }

            if (targetType == typeof(double))
            {
                convertedValue = Convert.ToDouble(value);
                return true;
            }

            if (targetType == typeof(int))
            {
                convertedValue = Convert.ToInt32(value);
                return true;
            }

            return false;
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            StateChanged?.Invoke($"TWAIN state: {_twainSession.State}");
        }

        private void OnTransferReady(object? sender, TransferReadyEventArgs e)
        {
            StateChanged?.Invoke("Scanner is transferring image...");
        }

        private void OnDataTransferred(object? sender, DataTransferredEventArgs e)
        {
            if (e.NativeData != IntPtr.Zero)
            {
                using (var stream = e.GetNativeImageStream())
                {
                    if (stream != null)
                    {
                        using var bitmap = Image.FromStream(stream);
                        ImageScanned?.Invoke((Image)bitmap.Clone());
                    }
                }
            }
        }

        public void Dispose()
        {
            CloseSession();
        }
    }
}


