using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace MeetingTranslator;

public partial class ScreenCaptureWindow : Window
{
    private Point _startPoint;
    private bool _isDrawing;
    public string? Base64CapturedImage { get; private set; }

    public ScreenCaptureWindow()
    {
        InitializeComponent();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _startPoint = e.GetPosition(CaptureCanvas);
            _isDrawing = true;
            SelectionRectangle.Visibility = Visibility.Visible;
            CaptureCanvas.CaptureMouse();
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDrawing)
        {
            var currentPoint = e.GetPosition(CaptureCanvas);
            
            var x = Math.Min(currentPoint.X, _startPoint.X);
            var y = Math.Min(currentPoint.Y, _startPoint.Y);
            var width = Math.Max(currentPoint.X, _startPoint.X) - x;
            var height = Math.Max(currentPoint.Y, _startPoint.Y) - y;

            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;
            System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, x);
            System.Windows.Controls.Canvas.SetTop(SelectionRectangle, y);
        }
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDrawing)
        {
            _isDrawing = false;
            CaptureCanvas.ReleaseMouseCapture();
            SelectionRectangle.Visibility = Visibility.Hidden;

            int screenWidth = (int)SelectionRectangle.Width;
            int screenHeight = (int)SelectionRectangle.Height;

            if (screenWidth > 0 && screenHeight > 0)
            {
                this.Hide(); // Esconder a janela para limpar a cor escura e bordas vermelhas da tela

                // Dar um pequeno delay para a UI do Windows processar o Hide() da transparência
                System.Threading.Thread.Sleep(50); 

                var ptScreen = this.PointToScreen(new Point(System.Windows.Controls.Canvas.GetLeft(SelectionRectangle), System.Windows.Controls.Canvas.GetTop(SelectionRectangle)));

                // Obter as coordenadas físicas se houver diferença de DPI
                PresentationSource source = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }

                int physicalWidth = (int)(screenWidth * dpiX);
                int physicalHeight = (int)(screenHeight * dpiY);

                using (var bitmap = new Bitmap(physicalWidth, physicalHeight, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen((int)ptScreen.X, (int)ptScreen.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    }

                    using (var ms = new MemoryStream())
                    {
                        bitmap.Save(ms, ImageFormat.Png);
                        Base64CapturedImage = Convert.ToBase64String(ms.ToArray());
                    }
                }
            }

            this.DialogResult = true;
            this.Close();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
