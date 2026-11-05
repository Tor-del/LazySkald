using System.Drawing;

namespace ScreenReader.App;

internal static class OverlayPlacement
{
    // All values are physical screen pixels; WPF sizes are converted by the window itself.
    public static Point Calculate(Rectangle screen,Rectangle workingArea,int width,int height,double centerX=0.5,double centerY=0.75)
    {
        int x=(int)Math.Round(screen.Left+screen.Width*centerX-width/2.0);
        int y=(int)Math.Round(screen.Top+screen.Height*centerY-height/2.0);
        return new(Math.Clamp(x,workingArea.Left,Math.Max(workingArea.Left,workingArea.Right-width)),
            Math.Clamp(y,workingArea.Top,Math.Max(workingArea.Top,workingArea.Bottom-height)));
    }
}
