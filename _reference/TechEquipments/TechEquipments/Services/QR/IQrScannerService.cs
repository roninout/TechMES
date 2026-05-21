using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TechEquipments
{
    public interface IQrScannerService
    {
        /// <summary>
        /// Открывает окно камеры, ждёт QR и возвращает текст
        /// (или null, если отмена/ошибка).
        /// </summary>
        Task<string?> ScanFromCameraAsync(Window owner, CancellationToken ct = default);

        /// <summary>
        /// Возвращает список доступных камер.
        /// </summary>
        Task<IReadOnlyList<QrCameraInfo>> GetAvailableCamerasAsync(CancellationToken ct = default);

        /// <summary>
        /// Возвращает запомненный индекс камеры.
        /// </summary>
        Task<int> GetPreferredCameraIndexAsync(CancellationToken ct = default);

        /// <summary>
        /// Сохраняет выбранный индекс камеры.
        /// </summary>
        Task SetPreferredCameraIndexAsync(int cameraIndex, CancellationToken ct = default);
    }
}
