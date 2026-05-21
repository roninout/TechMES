using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TechEquipments.Views.Qr;

namespace TechEquipments.Services.QR
{
    /// <summary>
    /// Сервис QR-сканирования:
    /// - ищет доступные камеры,
    /// - хранит выбранный индекс камеры в user-state.json,
    /// - открывает QR-окно с нужной камерой.
    /// </summary>
    public sealed class QrScannerService : IQrScannerService
    {
        private readonly IUserStateService _userStateService;

        public QrScannerService(IUserStateService userStateService)
        {
            _userStateService = userStateService ?? throw new ArgumentNullException(nameof(userStateService));
        }

        /// <summary>
        /// Открывает окно QR-сканирования.
        /// </summary>
        public async Task<string?> ScanFromCameraAsync(System.Windows.Window owner, CancellationToken ct = default)
        {
            var cameras = await GetAvailableCamerasAsync(ct);
            if (cameras.Count == 0)
                return null;

            int preferredIndex = await GetPreferredCameraIndexAsync(ct);

            // Если сохранённая камера не найдена — берём первую доступную.
            if (!cameras.Any(x => x.Index == preferredIndex))
                preferredIndex = cameras[0].Index;

            var w = new QrScanWindow(this, cameras, preferredIndex)
            {
                Owner = owner
            };

            bool? ok = w.ShowDialog();
            return ok == true ? w.ScannedText : null;
        }

        /// <summary>
        /// Возвращает список доступных камер по индексам 0..5.
        /// </summary>
        public async Task<IReadOnlyList<QrCameraInfo>> GetAvailableCamerasAsync(CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var result = new List<QrCameraInfo>();

                // Обычно 0..5 достаточно.
                for (int i = 0; i <= 5; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);

                    if (cap.IsOpened())
                    {
                        result.Add(new QrCameraInfo
                        {
                            Index = i,
                            DisplayName = $"Camera {i}"
                        });
                    }
                }

                return (IReadOnlyList<QrCameraInfo>)result;
            }, ct);
        }

        /// <summary>
        /// Читает сохранённый индекс камеры.
        /// </summary>
        public async Task<int> GetPreferredCameraIndexAsync(CancellationToken ct = default)
        {
            var state = await _userStateService.LoadAsync(ct);
            return state?.QrCameraIndex ?? 0;
        }

        /// <summary>
        /// Сохраняет индекс выбранной камеры.
        /// </summary>
        public async Task SetPreferredCameraIndexAsync(int cameraIndex, CancellationToken ct = default)
        {
            var state = await _userStateService.LoadAsync(ct) ?? new UserState();
            state.QrCameraIndex = cameraIndex;
            await _userStateService.SaveAsync(state, ct);
        }
    }
}