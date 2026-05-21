using System;

namespace TechEquipments
{
    public class OperatorAct
    {
        public DateTime Date { get; set; } // timestamp
        public int Type { get; set; }            // integer
        public string Client { get; set; }       // char(128)
        public string User { get; set; }         // char(128)
        public string Tag { get; set; }          // char(128)
        public int Hash { get; set; }            // integer
        public string Equip { get; set; }        // char(256)
        public string Desc { get; set; }         // char(256)
        public string OldV { get; set; }         // char(64)
        public string NewV { get; set; }         // char(64)
    }
}
