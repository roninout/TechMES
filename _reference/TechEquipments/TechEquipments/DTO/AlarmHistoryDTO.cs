using System;

namespace TechEquipments
{
    public class AlarmHistoryDTO
    {
        public DateTime Date { get; set; }
        public string Time { get; set; }
        
        private string _categoryRaw;
        public string Category
        {
            get
            {
                if (string.IsNullOrEmpty(_categoryRaw))
                    return "";

                string s = _categoryRaw.Trim();

                if (s.StartsWith("@(") && s.EndsWith(")"))
                    return s.Substring(2, s.Length - 3).Trim();

                return s
                    .Replace("@(", "")
                    .Replace(")", "")
                    .Trim();
            }
            set
            {
                _categoryRaw = value; // сохраняем оригинал
            }
        }
        
        public string User { get; set; }
        public string Location { get; set; }
        public string Equipment { get; set; }
        public string Item { get; set; }
        public string Comment { get; set; }
        public string State { get; set; }

    }
}
