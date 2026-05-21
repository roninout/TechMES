using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Implement this interface on param-models that have "Chanel" property.
    /// We keep the name "Chanel" to not break existing bindings.
    /// </summary>
    public interface IHasChanel
    {
        string Chanel { get; set; }
    }
}
