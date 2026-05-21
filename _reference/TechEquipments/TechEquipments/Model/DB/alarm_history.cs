using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public class alarm_history
    {
        [Key]
        public int id { get; set; }
        public string date { get; set; }
        public string time { get; set; }
        public string category { get; set; }
        public string almcomment { get; set; }
        public string fullname { get; set; }
        public string userlocation { get; set; }
        public string state { get; set; }
        public string tag { get; set; }
        public string type { get; set; }
        public string typenum { get; set; }
        public string value { get; set; }
        public string equipment { get; set; }
        public string item { get; set; }
        public string message { get; set; }
        public string desc_ { get; set; }
        public string logstate { get; set; }
        public string dataext { get; set; }

        public DateTime localtimedate { get; set; }
    }
}
