using CtApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public class EquipModel
    {
        public EquipRefModel MainModel { get; set; } = null;

        public List<EquipRefModel> RefEquipments { get; set; } = new List<EquipRefModel>();
    }
}
