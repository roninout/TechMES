using DevExpress.Xpf.Core;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace TechEquipments.Views.Info
{
    /// <summary>
    /// Окно съёмки фото:
    /// - показывает live preview с камеры,
    /// - позволяет выбрать камеру,
    /// - сохраняет один снимок во временный jpg.
    /// </summary>
    public partial class PhotoCaptureWindow : ThemedWindow
    {
        private readonly IQrScannerService _qrScannerService;
        private readonly IReadOnlyList<QrCameraInfo> _cameras;

        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private VideoCapture? _cap;
        private WriteableBitmap? _wb;

        private int _cameraIndex;
        private bool _isChangingCamera;

        private readonly object _frameSync = new();
        private Mat? _lastFrame;

        /// <summary>
        /// Путь к временному jpg, если DialogResult == true.
        /// </summary>
        public string? CapturedFilePath { get; private set; }

        public PhotoCaptureWindow(IQrScannerService qrScannerService, IReadOnlyList<QrCameraInfo> cameras, int selectedCameraIndex)
        {
            _qrScannerService = qrScannerService ?? throw new ArgumentNullException(nameof(qrScannerService));
            _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
            _cameraIndex = selectedCameraIndex;

            InitializeComponent();

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            CameraCombo.ItemsSource = _cameras;
            CameraCombo.SelectedValue = _cameraIndex;

            await RestartCaptureAsync();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            try { _cts?.Cancel(); } catch { }

            try { _loopTask?.Wait(300); } catch { }

            try { _cap?.Release(); } catch { }
            try { _cap?.Dispose(); } catch { }

            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
            _cap = null;
            _wb = null;

            lock (_frameSync)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }

            PreviewImage.Source = null;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

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

                // Используем тот же persisted camera index, что и для QR.
                await _qrScannerService.SetPreferredCameraIndexAsync(_cameraIndex);

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

        private async Task RestartCaptureAsync()
        {
            await StopCaptureAsync();

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => CaptureLoopAsync(_cameraIndex, _cts.Token));

            await Task.CompletedTask;
        }

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
                    await Dispatcher.InvokeAsync(() =>
                        StatusText.Text = $"Camera {cameraIndex}: can't open");
                    return;
                }

                //_cap.Set(VideoCaptureProperties.FrameWidth, 640);
                //_cap.Set(VideoCaptureProperties.FrameHeight, 480);

                _cap.Set(VideoCaptureProperties.FrameWidth, 1280);
                _cap.Set(VideoCaptureProperties.FrameHeight, 720);

                _cap.Set(VideoCaptureProperties.Fps, 30);
                _cap.Set(VideoCaptureProperties.BufferSize, 1);

                await Dispatcher.InvokeAsync(() =>
                    StatusText.Text = $"Camera {cameraIndex}: ready");

                using var frame = new Mat();

                while (!ct.IsCancellationRequested)
                {
                    bool ok = _cap.Read(frame);
                    if (!ok || frame.Empty())
                    {
                        await Task.Delay(20, ct);
                        continue;
                    }

                    lock (_frameSync)
                    {
                        _lastFrame?.Dispose();
                        _lastFrame = frame.Clone();
                    }

                    await Dispatcher.InvokeAsync(() => UpdatePreview(frame));
                    await Task.Delay(10, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // normal
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    StatusText.Text = "Camera error: " + ex.Message);
            }
        }

        private void UpdatePreview(Mat bgrFrame)
        {
            int w = bgrFrame.Width;
            int h = bgrFrame.Height;

            if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
            {
                _wb = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
                PreviewImage.Source = _wb;
            }

            int stride = (int)bgrFrame.Step();
            int bufferSize = stride * h;

            _wb.WritePixels(new Int32Rect(0, 0, w, h), bgrFrame.Data, bufferSize, stride);
        }

        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Mat? snapshot = null;

                lock (_frameSync)
                {
                    if (_lastFrame != null && !_lastFrame.Empty())
                        snapshot = _lastFrame.Clone();
                }

                if (snapshot == null || snapshot.Empty())
                {
                    StatusText.Text = "No frame available yet.";
                    snapshot?.Dispose();
                    return;
                }

                using (snapshot)
                {
                    var folder = Path.Combine(Path.GetTempPath(), "TechEquipments", "InfoPhotoCapture");
                    Directory.CreateDirectory(folder);

                    var filePath = Path.Combine(
                        folder,
                        $"photo_{DateTime.Now:yyyyMMdd_HHmmssfff}.jpg");

                    Cv2.ImWrite(filePath, snapshot);

                    CapturedFilePath = filePath;
                    DialogResult = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Capture error: " + ex.Message;
            }
        }
    }
}