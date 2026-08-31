using System;
using System.Runtime.InteropServices;

namespace TechDotNetLib.ActiveX.RectangleActiveX
{
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]    
    [Guid("981114EF-B54A-496F-AB34-2649EBDBDA6D")]


    public interface IRectangleActiveX
    {
        #region Methods
        // Здесь определяем предоставляемые элементом управления ActiveX методы...
        [DispId(1)]
        void ShowRectangle(int TargetX, int TargetY, int ResolutionX, int ResolutionY, int RectangleWidth, int RectangleHeight, int NumberOfSteps);

        [DispId(2)]
        void TestMet();

        #endregion
    }
}
