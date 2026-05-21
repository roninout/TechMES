using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Фабрика: превращает "сырые" SCADA-модели в новые UI-модели.
    /// </summary>
    public static class ParamModelFactory
    {
        public static object? Create(EquipTypeGroup typeGroup, object? rawParam)
        {
            if (rawParam == null)
                return null;

            return typeGroup switch
            {
                EquipTypeGroup.AI when rawParam is AIParam ai => new AiModel(ai),
                EquipTypeGroup.DI when rawParam is DIParam di => new DiModel(di),
                EquipTypeGroup.DO when rawParam is DOParam @do => new DoModel(@do),
                EquipTypeGroup.Atv when rawParam is AtvParam atv => new AtvModel(atv),
                EquipTypeGroup.Motor when rawParam is MotorParam motor => new MotorModel(motor),
                EquipTypeGroup.VGA_EL when rawParam is VGA_ElParam vgaEl => new VgaElModel(vgaEl),
                EquipTypeGroup.VGA when rawParam is VGAParam vga => new VgaModel(vga),
                EquipTypeGroup.VGD when rawParam is VGDParam vgd => new VgdModel(vgd),

                // fallback: на всякий случай не ломаем старое поведение
                _ => rawParam
            };
        }
    }
}
