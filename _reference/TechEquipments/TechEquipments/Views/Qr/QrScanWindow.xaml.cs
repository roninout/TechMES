using DevExpress.Xpf.Core;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace TechEquipments.Views.Qr
{
    /// <summary>
    /// Окно сканирования QR:
    /// - показывает список камер,
    /// - запоминает выбранную камеру,
    /// - при переключении камеры перезапускает capture loop.
    /// </summary>
    public partial class QrScanWindow : ThemedWindow
    {
        private readonly IQrScannerService _qrScannerService;
        private readonly IReadOnlyList<QrCameraInfo> _cameras;

        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private VideoCapture? _cap;
        private WriteableBitmap? _wb;
        private byte[]? _rgbBuffer;

        private readonly BarcodeReaderGeneric _readerFast;
        private readonly BarcodeReaderGeneric _readerHard;

        private int _cameraIndex;
        private bool _isChangingCamera;

        /// <summary>
        /// Результат (текст из QR), если DialogResult == true.
        /// </summary>
        public string? ScannedText { get; private set; }

        public QrScanWindow(IQrScannerService qrScannerService, IReadOnlyList<QrCameraInfo> cameras, int selectedCameraIndex)
        {
            _qrScannerService = qrScannerService ?? throw new ArgumentNullException(nameof(qrScannerService));
            _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
            _cameraIndex = selectedCameraIndex;

            InitializeComponent();

            _readerFast = CreateReader(tryHarder: false);
            _readerHard = CreateReader(tryHarder: true);

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        /// <summary>
        /// Создаёт ZXing reader строго под QR.
        /// </summary>
        private static BarcodeReaderGeneric CreateReader(bool tryHarder)
        {
            return new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = tryHarder,
                    PossibleFormats = new List<BarcodeFormat>
                    {
                        BarcodeFormat.QR_CODE
                    }
                }
            };
        }

        /// <summary>
        /// Инициализация окна: загружаем ComboBox и стартуем выбранную камеру.
        /// </summary>
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            CameraCombo.ItemsSource = _cameras;
            CameraCombo.SelectedValue = _cameraIndex;

            await RestartCaptureAsync();
        }

        /// <summary>
        /// Остановить loop и освободить камеру.
        /// </summary>
        private void OnClosed(object? sender, EventArgs e)
        {
            try { _cts?.Cancel(); } catch { }

            try
            {
                _loopTask?.Wait(300);
            }
            catch
            {
                // ignore
            }

            try { _cap?.Release(); } catch { }
            try { _cap?.Dispose(); } catch { }

            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
            _cap = null;
            _wb = null;
            _rgbBuffer = null;

            PreviewImage.Source = null;
        }

        /// <summary>
        /// Отмена сканирования.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Пользователь выбрал другую камеру.
        /// </summary>
        private async void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isChangingCamera)
                return;

            if (CameraCombo.SelectedItem is not QrCameraInfo cam)
                return;

            if (cam.Index == _cameraIndex)
                return;

            _isChangingCamera = true;
            try
            {
                _cameraIndex = cam.Index;

                // Сохраняем выбор между запусками.
                await _qrScannerService.SetPreferredCameraIndexAsync(_cameraIndex);

                // Перезапускаем захват уже на новой камере.
                await RestartCaptureAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Camera switch error: " + ex.Message;
            }
            finally
            {
                _isChangingCamera = false;
            }
        }

        /// <summary>
        /// Перезапуск захвата для текущего _cameraIndex.
        /// </summary>
        private async Task RestartCaptureAsync()
        {
            await StopCaptureAsync();

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => CaptureLoopAsync(_cameraIndex, _cts.Token));

            await Task.CompletedTask;
        }

        /// <summary>
        /// Остановить текущий loop.
        /// </summary>
        private async Task StopCaptureAsync()
        {
            try { _cts?.Cancel(); } catch { }

            if (_loopTask != null)
            {
                try
                {
                    await _loopTask;
                }
                catch
                {
                    // ignore
                }
            }

            try { _cap?.Release(); } catch { }
            try { _cap?.Dispose(); } catch { }

            _cap = null;

            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
        }

        /// <summary>
        /// Главный цикл:
        /// - открываем выбранную камеру,
        /// - читаем кадры,
        /// - показываем preview,
        /// - ищем QR.
        /// </summary>
        private async Task CaptureLoopAsync(int cameraIndex, CancellationToken ct)
        {
            try
            {
                _cap = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);

                if (!_cap.IsOpened())
                {
                    _cap.Dispose();
                    _cap = new VideoCapture(cameraIndex);
                }

                if (!_cap.IsOpened())
                {
                    await Dispatcher.InvokeAsync(() => StatusText.Text = $"Camera {cameraIndex}: can't open");
                    return;
                }

                // Немного ускоряем захват
                _cap.Set(VideoCaptureProperties.FrameWidth, 640);
                _cap.Set(VideoCaptureProperties.FrameHeight, 480);
                _cap.Set(VideoCaptureProperties.Fps, 30);
                _cap.Set(VideoCaptureProperties.BufferSize, 1);

                await Dispatcher.InvokeAsync(() =>
                    StatusText.Text = $"Camera {cameraIndex}: scanning...");

                using var frame = new Mat();
                using var rgb = new Mat();

                var sw = Stopwatch.StartNew();
                long lastDecodeMs = 0;

                while (!ct.IsCancellationRequested)
                {
                    bool ok = _cap.Read(frame);
                    if (!ok || frame.Empty())
                    {
                        await Task.Delay(20, ct);
                        continue;
                    }

                    // Показываем preview
                    await Dispatcher.InvokeAsync(() => UpdatePreview(frame));

                    // Не декодируем каждый кадр
                    var now = sw.ElapsedMilliseconds;
                    if (now - lastDecodeMs < 80)
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }

                    lastDecodeMs = now;

                    bool useTryHarder = now >= 2000;

                    string? decoded = TryDecodeQr(frame, rgb, useTryHarder);
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        decoded = decoded.Trim();

                        await Dispatcher.InvokeAsync(() =>
                        {
                            ScannedText = decoded;
                            StatusText.Text = "QR: OK";
                            DialogResult = true;
                            Close();
                        });

                        return;
                    }

                    await Task.Delay(5, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Окно закрыто / камера переключена.
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = "Camera error: " + ex.Message);
            }
        }

        /// <summary>
        /// Декодирование QR из Mat(BGR) через ZXing.
        /// </summary>
        private string? TryDecodeQr(Mat bgrFrame, Mat rgb, bool useTryHarder)
        {
            if (bgrFrame.Empty())
                return null;

            Cv2.CvtColor(bgrFrame, rgb, ColorConversionCodes.BGR2RGB);

            int w = rgb.Width;
            int h = rgb.Height;
            int bytesLen = w * h * 3;

            if (_rgbBuffer == null || _rgbBuffer.Length != bytesLen)
                _rgbBuffer = new byte[bytesLen];

            Marshal.Copy(rgb.Data, _rgbBuffer, 0, bytesLen);

            var source = new RGBLuminanceSource(_rgbBuffer, w, h, RGBLuminanceSource.BitmapFormat.RGB24);
            var result = (useTryHarder ? _readerHard : _readerFast).Decode(source);

            return result?.Text;
        }

        /// <summary>
        /// Обновить preview Image через WriteableBitmap.
        /// </summary>
        private void UpdatePreview(Mat bgrFrame)
        {
            int w = bgrFrame.Width;
            int h = bgrFrame.Height;

            if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
            {
                _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
                PreviewImage.Source = _wb;
            }

            int stride = (int)bgrFrame.Step();
            int bufferSize = stride * h;

            _wb.WritePixels(new Int32Rect(0, 0, w, h), bgrFrame.Data, bufferSize, stride);
        }
    }
}