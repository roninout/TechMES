using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public interface IQrCodeService
    {
        /// <summary>
        /// Генерирует QR-код из текста и сохраняет PNG-файл.
        /// Возвращает полный путь к сохранённому файлу.
        /// </summary>
        Task<string> GenerateQrPngAsync(string text, string? outputDirectory = null, string? fileNameWithoutExt = null, CancellationToken ct = default);

        /// <summary>
        /// Возвращает ожидаемый путь к PNG-файлу QR без создания файлов.
        /// Путь детерминированный: {dir}\{SafeName}.png
        /// </summary>
        string GetExpectedQrPngPath(string text, string? outputDirectory = null, string? fileNameWithoutExt = null);
    }
}
