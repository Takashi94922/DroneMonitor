using Microsoft.Maui.Graphics;

public class JoystickDrawable : IDrawable
{
    public PointF Center { get; set; } = new PointF(75, 75);

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        // 背景サークル
        canvas.FillColor = Colors.LightGray;
        canvas.FillCircle(dirtyRect.Center.X, dirtyRect.Center.Y, 70);

        // スティックノブ
        canvas.FillColor = Colors.Red;
        canvas.FillCircle(Center.X, Center.Y, 20);
    }
}
