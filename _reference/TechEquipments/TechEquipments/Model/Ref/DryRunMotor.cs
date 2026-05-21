namespace TechEquipments
{
    /// <summary>
    /// DryRun-настройки мотора.
    /// 
    /// IMPORTANT:
    /// - основные DryRun* поля читаются/пишутся с оборудования DryRunEquipName (например S17.P01.P01)
    /// - дополнительно можем подтянуть связанные DI/AI через WinOpened/ASSOC:
    ///   _dryRunDI / _dryRunAI
    /// - ссылки на модели нужны для отображения дополнительных блоков в MotorParamView
    /// </summary>
    public sealed class DryRunMotor
    {
        // =========================
        // Основные DryRun теги
        // =========================

        // DIGITAL (0/1)
        public double? DryRunAEn { get; set; }   // Enable DryRun
        public double? DryRunA { get; set; }     // Alarm/Active

        // INT (но в TagRead обычно приходит как double)
        public double? DryRunLimToOff { get; set; }
        public double? DryRunTimeToOn { get; set; }
        public double? DryRunTimeToOff { get; set; }

        // =========================
        // Связанные linked-equipment для DryRun
        // =========================

        /// <summary>
        /// Имя связанного DI equipment (из WinOpened ASSOC="_dryRunDI").
        /// Например: S17.P01.XS01
        /// </summary>
        public string? DryRunDiEquipName { get; set; }

        /// <summary>
        /// Имя связанного AI equipment (из WinOpened ASSOC="_dryRunAI").
        /// Например: S17.P01.PT01
        /// </summary>
        public string? DryRunAiEquipName { get; set; }

        /// <summary>
        /// Обертка над DIParam для отображения в UI.
        /// Если ссылка не найдена — null.
        /// </summary>
        public DiModel? DryRunDiModel { get; set; }

        /// <summary>
        /// Обертка над AIParam для отображения в UI.
        /// Если ссылка не найдена — null.
        /// </summary>
        public AiModel? DryRunAiModel { get; set; }

        /// <summary>
        /// Текст заголовка для linked AI в DryRun.
        /// Формируем из найденного EquipListBoxItem:
        /// "Equipment: Description"
        /// </summary>
        public string? DryRunAiTitle { get; set; }

        /// <summary>
        /// Готовая UI-строка для DI, чтобы показывать её тем же шаблоном,
        /// что и обычные строки в секции DI/DO.
        /// </summary>
        public DiDoRefRow? DryRunDiRow { get; set; }
    }
}