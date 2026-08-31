using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Reflection;

namespace TechDotNetLib.ActiveX.RectangleActiveX
{
    [ComVisible(true)]
    [Guid("8CF5E7A1-C1AE-4DD5-B64D-EE9D6E89313A")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IRectangleActiveX))]
    [ComSourceInterfaces(typeof(IRectangleActiveXEvents))]

    public partial class RectangleActiveXUI : UserControl, IRectangleActiveX
    {
        public RectangleActiveXUI()
        {
            InitializeComponent();
            this.SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
            //CommitInMaster_01_Khrolenko
        }

        #region Subscribe
        //[ComRegisterFunction()]
        //public static void RegisterClass(string key)
        //{
        //    // Удаляем HKEY_CLASSES_ROOT\ из переданного ключа
        //    StringBuilder sb = new StringBuilder(key);
        //    sb.Replace(@"HKEY_CLASSES_ROOT\", "");

        //    // Открываем ключ CLSID\{guid} в режиме записи
        //    RegistryKey k = Registry.ClassesRoot.OpenSubKey(sb.ToString(), true);

        //    // Создаем ключ элемента управления ActiveX – это позволяет ему появиться
        //    //в контейнере элемента управления ActiveX
        //    RegistryKey ctrl = k.CreateSubKey("Control");
        //    ctrl.Close();

        //    // Создаем запись CodeBase
        //    RegistryKey inprocServer32 = k.OpenSubKey("InprocServer32", true);
        //    inprocServer32.SetValue("CodeBase", Assembly.GetExecutingAssembly().CodeBase);
        //    inprocServer32.Close();

        //    // И в заключении закрываем ключ реестра 
        //    k.Close();
        //}

        //[ComUnregisterFunction()]
        //public static void UnregisterClass(string key)
        //{
        //    StringBuilder sb = new StringBuilder(key);
        //    sb.Replace(@"HKEY_CLASSES_ROOT\", "");

        //    // Открываем ключ HKCR\CLSID\{guid} в режиме записи
        //    RegistryKey k = Registry.ClassesRoot.OpenSubKey(sb.ToString(), true);

        //    // Удаляем ключ элемента управления ActiveX 
        //    k.DeleteSubKey("Control", false);

        //    // Затем открываем ключ InprocServer32
        //    RegistryKey inprocServer32 = k.OpenSubKey("InprocServer32", true);

        //    // Удаляем ключ CodeBase
        //    k.DeleteSubKey("CodeBase", false);

        //    // И в заключении закрываем ключ реестра
        //    k.Close();
        //}
        #endregion

        public void ShowRectangle(int TargetX, int TargetY, int ResolutionX, int ResolutionY, int RectangleWidth, int RectangleHeight, int NumberOfSteps)
        {
            RectangleActiveXForm form = new RectangleActiveXForm(TargetX, TargetY, ResolutionX, ResolutionY, RectangleWidth, RectangleHeight, NumberOfSteps);

            //Запускаем форму            
            form.Show();
        }

        public void TestMet()
        {
            throw new NotImplementedException();
        }

        private void Button1_Click(object sender, EventArgs e)
        {
            ShowRectangle(330, 300, 1920, 1080, 40, 20, 200);
        }
    }
}
