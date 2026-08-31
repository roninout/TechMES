using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading;

namespace TechDotNetLib.ActiveX.RectangleActiveX
{
    public partial class RectangleActiveXForm : Form
    {

        #region Инициализация свойств и объявление переменных
            private Graphics graph;
            private BufferedGraphicsContext currentContext;
            private BufferedGraphics myBuffer;
            private Pen pen = new Pen(Color.Red);
            private BackgroundWorker backgroundWorker = new BackgroundWorker();

            private int targetX,
                        targetY,
                        resolutionX,
                        resolutionY,
                        rectangleWidth,
                        rectangleHeight,
                        numberOfSteps;

            public int TargetX { get => targetX; set => targetX = value; }
            public int TargetY { get => targetY; set => targetY = value; }
            public int ResolutionX { get => resolutionX; set => resolutionX = value; }
            public int ResolutionY { get => resolutionY; set => resolutionY = value; }
            public int RectangleWidth { get => rectangleWidth; set => rectangleWidth = value; }
            public int RectangleHeight { get => rectangleHeight; set => rectangleHeight = value; }
            public int NumberOfSteps { get => numberOfSteps; set => numberOfSteps = value; }
        #endregion

        public RectangleActiveXForm(int targetX, int targetY, int resolutionX, int resolutionY, int rectangleWidth, int rectangleHeight, int numberOfSteps)
        {
            InitializeComponent();

            TargetX = targetX;
            TargetY = targetY;
            ResolutionX = resolutionX;
            ResolutionY = resolutionY;
            RectangleWidth = rectangleWidth;
            RectangleHeight = rectangleHeight;
            NumberOfSteps = numberOfSteps;


            this.Width = this.resolutionX;
            this.Height = this.resolutionY;


            currentContext = BufferedGraphicsManager.Current;
            myBuffer = currentContext.Allocate(this.CreateGraphics(), this.DisplayRectangle);
            graph = myBuffer.Graphics;

            pen.Width = 3.0f;
        }

        private void RectangleActiveXForm_Shown(object sender, EventArgs e)
        {
            Action action = new Action(backgroundWorker_Form1_Paint);
            IAsyncResult res = action.BeginInvoke(new AsyncCallback(CallBack), this);
        }

        public void backgroundWorker_Form1_Paint(/*object sender, DoWorkEventArgs e*/)
        {


            float StepOffsetXL = (float)this.targetX / this.numberOfSteps;
            float StepOffsetYL = (float)this.targetY / this.numberOfSteps;

            float StepOffsetXR = (float)(this.Size.Width - (this.targetX + this.rectangleWidth)) / this.numberOfSteps;
            float StepOffsetYR = (float)(this.Size.Height - (this.targetY + this.rectangleHeight)) / this.numberOfSteps;

            RectangleF rectF = new RectangleF(0.0F, 0.0F, (float)(this.Size.Width - pen.Width), (float)(this.Size.Height - pen.Width));

            for (int i = 0; i <= this.numberOfSteps; i++)
            {
                rectF.X = i * StepOffsetXL;
                rectF.Y = i * StepOffsetYL;
                rectF.Width = this.Size.Width - (i * StepOffsetXR) - (i * StepOffsetXL) - pen.Width;
                rectF.Height = this.Size.Height - (i * StepOffsetYR) - (i * StepOffsetYL) - pen.Width;

                //DrawRectangle(rectF);      
                graph.DrawRectangle(pen, Rectangle.Round(rectF));
                myBuffer.Render();

                Thread.Sleep(4);
                if (i < this.numberOfSteps)
                    graph.Clear(this.BackColor);
            }

            myBuffer.Dispose();                 //Освобождаем ресурсы, занятые объектом myBuffer в потоке
            Thread.Sleep(10000);                 //Оставляем прорисованным прямоугольник конечной формы на некоторое время 

        }

        //Действие при завершении работы backgroundWorker

        void backgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.Close();
        }
        
        public void CallBack(IAsyncResult res)
        {
            this.Close();
        }
    }
    
}
