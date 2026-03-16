using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Point = System.Windows.Point;

namespace MeetingTranslator;

public partial class ScreenCaptureWindow : Window
{
    private Point _startPoint;
    private bool _isDrawing;
    public string? Base64CapturedImage { get; private set; }
    public bool IsStealthModeActive { get; set; }

    [DllImport("user32.dll")]
    public static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public ScreenCaptureWindow()
    {
        InitializeComponent();
        
        // Configura a janela para cobrir toda a área virtual (todos os monitores)
        this.Left = SystemParameters.VirtualScreenLeft;
        this.Top = SystemParameters.VirtualScreenTop;
        this.Width = SystemParameters.VirtualScreenWidth;
        this.Width = SystemParameters.VirtualScreenWidth;
        this.Height = SystemParameters.VirtualScreenHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (IsStealthModeActive)
        {
            var helper = new WindowInteropHelper(this);
            SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);
            this.Cursor = Cursors.None;
        }
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

    private async void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDrawing)
        {
            _isDrawing = false;
            CaptureCanvas.ReleaseMouseCapture();
            SelectionRectangle.Visibility = Visibility.Hidden;

            double width = SelectionRectangle.Width;
            double height = SelectionRectangle.Height;
            double left = System.Windows.Controls.Canvas.GetLeft(SelectionRectangle);
            double top = System.Windows.Controls.Canvas.GetTop(SelectionRectangle);

            System.Diagnostics.Debug.WriteLine($"[Capture] MouseUp detectado. Seleção: L={left}, T={top}, W={width}, H={height}");

            if (width > 5 && height > 5)
            {
                // Obter as coordenadas físicas se houver diferença de DPI
                PresentationSource source = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }
                else
                {
                    dpiX = VisualTreeHelper.GetDpi(this).DpiScaleX;
                    dpiY = VisualTreeHelper.GetDpi(this).DpiScaleY;
                }

                System.Diagnostics.Debug.WriteLine($"[Capture] DPI Detectado: X={dpiX}, Y={dpiY}");

                // Coordenadas absolutas na tela virtual
                double virtualLeft = this.Left + left;
                double virtualTop = this.Top + top;

                // Deixa a janela invisível mas ativa para não interromper ShowDialog()
                this.Opacity = 0; 
                this.IsHitTestVisible = false; // Evita cliques extras enquanto processa
                await System.Threading.Tasks.Task.Delay(150); 

                try
                {
                    // Processamento pesado em Task.Run para não travar dispatcher
                    Base64CapturedImage = await Task.Run(() => 
                    {
                        int screenX = (int)(virtualLeft * dpiX);
                        int screenY = (int)(virtualTop * dpiY);
                        int physicalWidth = (int)(width * dpiX);
                        int physicalHeight = (int)(height * dpiY);

                        using (var bitmap = new Bitmap(physicalWidth, physicalHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (var graphics = Graphics.FromImage(bitmap))
                            {
                                graphics.CopyFromScreen(screenX, screenY, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                            }

                            using (var ms = new MemoryStream())
                            {
                                bitmap.Save(ms, ImageFormat.Png);
                                var base64 = Convert.ToBase64String(ms.ToArray());
                                return base64;
                            }
                        }
                    });

                    System.Diagnostics.Debug.WriteLine($"[Capture] Sucesso Gerando Base64. Tamanho: {Base64CapturedImage?.Length ?? 0}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Capture] Erro CRÍTICO no processamento do Bitmap: {ex.Message}\n{ex.StackTrace}");
                    Base64CapturedImage = null;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Capture] Seleção muito pequena, ignorando.");
                Base64CapturedImage = null;
            }

            if (string.IsNullOrEmpty(Base64CapturedImage))
            {
                this.DialogResult = false;
            }
            else
            {
                this.DialogResult = true;
            }
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
