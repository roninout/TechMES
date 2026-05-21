using System;

namespace TechEquipments
{
    public class OperatorActDTO
    {
        public DateTime Date { get; set; }
        public string Time { get; set; }
        public int Type { get; set; }
        public string Client { get; set; }
        public string User { get; set; }
        public string Tag { get; set; }
        public string Equip { get; set; }
        public string Desc { get; set; }
        public string OldV { get; set; }
        public string NewV { get; set; }

        /// <summary> Текст в квадратных скобках из Desc </summary>
        public string Page
        {
            get
            {
                if (string.IsNullOrEmpty(Desc)) return "";
                int start = Desc.IndexOf('[');
                int end = Desc.IndexOf(']');
                if (start >= 0 && end > start)
                    return Desc.Substring(start + 1, end - start - 1);
                return "";
            }
        }

        /// <summary> Комментарий без квадратных скобок </summary>
        public string Comment
        {
            get
            {
                if (string.IsNullOrEmpty(Desc)) return "";
                int start = Desc.IndexOf('[');
                int end = Desc.IndexOf(']');
                if (start >= 0 && end > start)
                {
                    // удаляем часть с [..]
                    return (Desc.Remove(start, end - start + 1)).Trim();
                }
                return Desc.Trim();
            }
        }


    }
}
