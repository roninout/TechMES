using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Implement this interface on param-models that expose Unit (engineering unit).
    /// </summary>
    public interface IHasUnit
    {
        string Unit { get; set; }
    }
}
