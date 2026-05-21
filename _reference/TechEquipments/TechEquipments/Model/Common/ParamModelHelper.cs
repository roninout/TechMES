using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Helper: если в CurrentParamModel лежит обертка AiModel/DiModel/...,
    /// возвращаем ее внутренний ParamObject.
    /// Если пришел уже "сырой" AIParam/DIParam/... - возвращаем как есть.
    /// </summary>
    public static class ParamModelHelper
    {
        public static object? Unwrap(object? modelOrParam) => modelOrParam is IParamModel model
                ? model.ParamObject
                : modelOrParam;
    }
}
