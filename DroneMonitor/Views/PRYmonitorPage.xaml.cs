using Microsoft.Maui.Dispatching;
using Plugin.BLE.Abstractions.Contracts;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;


namespace DroneMonitor.Views
{
    public class Triangle
    {
        public SKPoint3[] Vertices { get; set; } // 3頂点
        public Triangle()
        {
            Vertices = new SKPoint3[3];
        }
    }

    public static class STLLoader
    {
        public static async Task<List<Triangle>> LoadFromResourceAsync(string resourceFilename)
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync(resourceFilename);
            using var reader = new BinaryReader(stream);

            var header = new string(reader.ReadChars(80));
            stream.Seek(0, SeekOrigin.Begin);

            if (header.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                using var sr = new StreamReader(stream);
                return LoadAscii((await sr.ReadToEndAsync()).Split('\n'));
            }
            else
            {
                return LoadBinary(reader);
            }
        }

        private static List<Triangle> LoadAscii(string[] lines)
        {
            var triangles = new List<Triangle>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("facet normal"))
                {
                    var triangle = new Triangle();
                    i += 2; // skip "outer loop"
                    for (int v = 0; v < 3; v++)
                    {
                        var parts = lines[i++].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        triangle.Vertices[v] = new Vector3(
                            float.Parse(parts[1], CultureInfo.InvariantCulture),
                            float.Parse(parts[2], CultureInfo.InvariantCulture),
                            float.Parse(parts[3], CultureInfo.InvariantCulture));
                    }
                    triangles.Add(triangle);
                }
            }
            return triangles;
        }

        private static List<Triangle> LoadBinary(BinaryReader reader)
        {
            Debug.WriteLine("バイナリSTLファイルの読み込みを開始");
            reader.BaseStream.Seek(80, SeekOrigin.Begin); // skip header
            uint triangleCount = reader.ReadUInt32();
            var triangles = new List<Triangle>((int)triangleCount);

            for (int i = 0; i < triangleCount; i++)
            {
                reader.ReadBytes(12); // skip normal
                var triangle = new Triangle();
                for (int v = 0; v < 3; v++)
                {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    triangle.Vertices[v] = new Vector3(x, y, z);
                }
                reader.ReadUInt16(); // skip attribute byte count
                triangles.Add(triangle);
            }
            Debug.WriteLine($"バイナリSTLファイルの読み込み完了: {triangleCount} triangles");
            return triangles;
        }
    }

    public partial class PRYmonitorPage : ContentPage
    {
        private BleService? _bleService;
        private float pitch, roll, yaw;

        // STL 読み込みデータ
        private List<Triangle> loadedTriangles = new();

        // スクリーン投影済み頂点＋カラー＋インデックス
        private readonly SKPaint fillPaint = new SKPaint
        {
            Color = SKColors.LightBlue,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // 回転行列・モデル中心・Z最大値
        private Matrix4x4 rotationMatrix = Matrix4x4.Identity;
        private Vector3 modelCenter;
        private float zMax;
        private float zMin;
        // 投影パラメータ
        private const float FOV = 500f;
        private const float SCALE = 2f;
        private const float OFFSET_X = 500f;
        private const float OFFSET_Y = 200f;
        // 回転後の三角形リスト
        private List<Triangle>? rotatedTriangles;

        public PRYmonitorPage()
        {
            InitializeComponent();
            // タイマー作成
            var timer = this.Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(33);  // ≒30fps
            timer.Tick += OnTimerTick;
            timer.Start();
        }
        // ① タイマーではUIスレッドを阻害しない
        private void OnTimerTick(object sender, EventArgs e)
        {
            // 重い処理はバックグラウンドへ
            _ = Task.Run(async () =>
            {
                await RotateAndProjectAsync(pitch, roll, yaw);
                // 描画リクエストはUIで
                MainThread.BeginInvokeOnMainThread(() =>
                    canvasView.InvalidateSurface());
            });
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 1) STL読み込み
            loadedTriangles = await STLLoader.LoadFromResourceAsync("modelv46l.stl");

            // 2) モデル重心を算出（回転中心を安定化）
            var allPoints = loadedTriangles.SelectMany(t => t.Vertices);
            modelCenter = new Vector3(0,0,0);

            ComputeModelCenterAndZRange();

            // 3) 初期回転を適用（例：Pitch=270で上向き補正）
            await RotateAndProjectAsync(90, roll, yaw);
            canvasView.InvalidateSurface();
        }
        protected override async void OnDisappearing()
        {
            base.OnDisappearing();
            if (_bleService != null)
            {
                // 必要な通知キーを指定して停止
                await _bleService.StopNotificationAsync("PRY_Telem");
                // 他にも通知を止めたいCharacteristicがあればここで追加
                _bleService.NotificationReceived -= OnNotificationReceived;
            }
        }

        public void StartNotificationAsync()
        {
            // 通知開始の成否をログ出力
            _bleService.StartNotificationAsync("PRY_Telem").ContinueWith(t =>
                Debug.WriteLine($"PRY_Telem通知開始: {t.Result}"));
        }
        public void SetBleService(BleService bleService)
        {
            _bleService = bleService;
            _bleService.NotificationReceived -= OnNotificationReceived;
            _bleService.NotificationReceived += OnNotificationReceived;
            if (_bleService != null && _bleService.IsConnected)
            {
                StartNotificationAsync();
            }
        }

        private void OnNotificationReceived(object? sender, byte[] data)
        {
            if (_bleService == null)
            {
                Debug.WriteLine("BLE 未接続");
                return;
            }

            string key = "";
            if (sender is ICharacteristic characteristic)
            {
                key = _bleService.Characteristics.FirstOrDefault(x => x.Value == characteristic).Key ?? "";
            }

            Debug.WriteLine($"受信: key={key}, data={BitConverter.ToString(data)}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (key == "PRY_Telem" && data.Length >= 12)
                {
                    float[] floats = new float[3];
                    for (int i = 0; i < 3; i++)
                        floats[i] = BitConverter.ToSingle(data, i * 4);

                    pitch = floats[0];
                    roll = floats[1];
                    yaw = floats[2];
                    pryWindow.Text = $"Pitch: {pitch:F2}°\nRoll: {roll:F2}°\nYaw: {yaw:F2}°";   
                    Debug.WriteLine($"{pitch:F2}, {roll:F2}, {yaw:F2}");
                }
            });
        }

        void ComputeModelCenterAndZRange()
        {
            var pts = loadedTriangles
                .SelectMany(t => t.Vertices)
                .Select(p => new Vector3(p.X, p.Y, p.Z))
                .ToArray();

            // 重心
            var sum = Vector3.Zero;
            foreach (var p in pts) sum += p;
            modelCenter = sum / pts.Length;

            // Z範囲
            zMin = pts.Min(p => p.Z);
            zMax = pts.Max(p => p.Z);
        }

        /// <summary>
        /// pitch/roll/yaw で回転し、rotatedTriangles を更新
        /// </summary>
        private async Task RotateAndProjectAsync(float pitchDeg, float rollDeg, float yawDeg)
        {
            if (loadedTriangles.Count == 0)
                return;

            await Task.Run(() =>
            {
                var r = MathF.PI / 180f;
                var rx = Matrix4x4.CreateRotationX((pitchDeg+ 90) * r);
                var ry = Matrix4x4.CreateRotationY(rollDeg * r);
                var rz = Matrix4x4.CreateRotationZ(yawDeg * r);
                rotationMatrix = rz * ry * rx;

                var list = new List<Triangle>(loadedTriangles.Count);
                foreach (var tri in loadedTriangles)
                {
                    var t = new Triangle();
                    for (int i = 0; i < 3; i++)
                    {
                        var v = tri.Vertices[i];
                        var v3 = new Vector3(v.X, v.Y, v.Z);
                        var rv = Vector3.Transform(v3, rotationMatrix);
                        t.Vertices[i] = new Vector3(rv.X, rv.Y, rv.Z);
                    }
                    list.Add(t);
                }

                MainThread.BeginInvokeOnMainThread(() =>
                    rotatedTriangles = list);
            });
        }

        void OnCanvasViewPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.White);

            var tris = rotatedTriangles ?? loadedTriangles;

            // 2) 平均 Z と共にソート
            var sorted = tris
                .Select(tri =>
                {
                    // 各頂点を回転＋移動した Vector3 を得る
                    var v0 = Transform(tri.Vertices[0]);
                    var v1 = Transform(tri.Vertices[1]);
                    var v2 = Transform(tri.Vertices[2]);
                    // 平均 Z を計算
                    float zAvg = GetAverageZ(v0, v1, v2);
                    // tri と zAvg のタプルを返す
                    return (tri, z: zAvg);
                })
                // Z の小さい（奥）順にソート
                .OrderBy(item => item.z)
                .ToArray();

            // 3) 描画
            foreach (var (tri, z) in sorted)
            {
                // 奥行きで色を決定（遠いほど０.２、手前ほど１.０）
                float t = 1f - (z - zMin) / (zMax - zMin);
                t = Math.Clamp(t, 0.2f, 1f);
                byte v = (byte)(255 * t);

                using var paint = new SKPaint
                {
                    Color = new SKColor(v, v, v),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };

                // 投影
                var p0 = ProjectTo2D(tri.Vertices[0]);
                var p1 = ProjectTo2D(tri.Vertices[1]);
                var p2 = ProjectTo2D(tri.Vertices[2]);

                using var path = new SKPath();
                path.MoveTo(p0);
                path.LineTo(p1);
                path.LineTo(p2);
                path.Close();

                canvas.DrawPath(path, paint);
            }

            DrawAxes(canvas);
        }

        SKPoint ProjectTo2D(Vector3 p)
        {
            // 遠近補正
            var factor = FOV / (FOV + p.Z);
            var s = SCALE * factor;
            return new SKPoint(
                p.X * s + OFFSET_X,
                -p.Y * s + OFFSET_Y);
        }
        // 3D→3D回転＋重心オフセット
        Vector3 Transform(SKPoint3 pt)
        {
            var v = new Vector3(pt.X, pt.Y, pt.Z) - modelCenter;
            return Vector3.Transform(v, rotationMatrix) + modelCenter;
        }

        // Vector3 の Z 成分の平均を返す
        float GetAverageZ(Vector3 a, Vector3 b, Vector3 c)
        {
            return (a.Z + b.Z + c.Z) / 3f;
        }
        void DrawAxes(SKCanvas canvas)
        {
            var o = modelCenter;
            var x = o + Vector3.Transform(new Vector3(1, 0, 0), rotationMatrix) * 100;
            var y = o + Vector3.Transform(new Vector3(0, 1, 0), rotationMatrix) * 100;
            var z = o + Vector3.Transform(new Vector3(0, 0, 1), rotationMatrix) * 100;

            var o2 = ProjectTo2D(o);
            var x2 = ProjectTo2D(x);
            var y2 = ProjectTo2D(y);
            var z2 = ProjectTo2D(z);

            using var px = new SKPaint { Color = SKColors.Red, StrokeWidth = 2 };
            using var py = new SKPaint { Color = SKColors.Green, StrokeWidth = 2 };
            using var pz = new SKPaint { Color = SKColors.Blue, StrokeWidth = 2 };

            canvas.DrawLine(o2, x2, px);
            canvas.DrawLine(o2, y2, py);
            canvas.DrawLine(o2, z2, pz);
        }
    }
}