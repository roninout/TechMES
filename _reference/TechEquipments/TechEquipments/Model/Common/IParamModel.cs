using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Общий контракт для новых UI-моделей вкладки Param.
    /// 
    /// Идея:
    /// - View работает не напрямую с AIParam/DIParam/...,
    ///   а с оберткой AiModel/DiModel/...
    /// - внутри обертки лежит "сырая" SCADA-модель ParamObject
    /// - дополнительные возможности (PLC / DI-DO / Alarm / TimeWork / DryRun)
    ///   описываются здесь, а не размазываются по всему приложению
    /// </summary>
    public interface IParamModel
    {
        /// <summary>Внутренняя "сырая" модель параметров (AIParam / DIParam / ...).</summary>
        object ParamObject { get; }

        /// <summary>Какие дополнительные страницы/секции поддерживает эта модель.</summary>
        IReadOnlyCollection<ParamSettingsPage> SupportedPages { get; }

        bool HasPlcPage { get; }
        bool HasDiDoPage { get; }
        bool HasAlarmPage { get; }
        bool HasTimeWorkPage { get; }
        bool HasDryRunPage { get; }
        bool HasAtvPage { get; }
    }
}
