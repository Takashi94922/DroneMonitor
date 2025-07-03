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
        private SKVertices? skVerts;
        private readonly SKPaint fillPaint = new SKPaint
        {
            Color = SKColors.LightBlue,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            BlendMode = SKBlendMode.SrcOver
        };

        // 回転行列・モデル中心・Z最大値
        private Matrix4x4 rotationMatrix = Matrix4x4.Identity;
        private Vector3 modelCenter;
        private float zMax;

        // 投影パラメータ
        private const float FOV = 500f;
        private const float SCALE = 2f;
        private const float OFFSET_X = 500f;
        private const float OFFSET_Y = 200f;


        public PRYmonitorPage()
        {
            InitializeComponent();
            // タイマー作成
            var timer = this.Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(33);  // ≒30fps
            timer.Tick += (s, e) =>
            {
                canvasView.InvalidateSurface();
            };
            timer.Start();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 1) STL読み込み
            loadedTriangles = await STLLoader.LoadFromResourceAsync("modelv46.stl");

            // 2) モデル重心を算出（回転中心を安定化）
            var allPoints = loadedTriangles.SelectMany(t => t.Vertices);
            modelCenter = new Vector3(0,0,0);

            // 3) 初期回転を適用（例：Pitch=270で上向き補正）
            await ApplyRotationAsync(270, 0, 0);
            canvasView.InvalidateSurface();
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
            else
            {
                // senderがBleService自身の場合（Invoke(this, data)のような実装）
                // dataが一致するCharacteristicを探す
                foreach (var pair in _bleService.Characteristics)
                {
                    // ここではValueUpdatedイベントのsenderがCharacteristicであることが前提
                    // それ以外の場合はキーを特定できないので空文字
                }
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
                    Debug.WriteLine($"{pitch:F2}, {roll:F2}, {yaw:F2}");
                    // バックグラウンドで重い回転計算を走らせる
                    _ = ApplyRotationAsync(pitch, roll, yaw);
                }
            });
        }

        void OnCanvasViewPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.White);

            // SKVertices を一括描画
            if (skVerts != null)
                canvas.DrawVertices(skVerts, SKBlendMode.SrcOver, fillPaint);

            // 軸表示
            DrawAxes(canvas);
        }

        async Task ApplyRotationAsync(float pitch, float roll, float yaw)
        {
            if (loadedTriangles == null || loadedTriangles.Count == 0)
                return;

            // 回転行列更新
            var r = MathF.PI / 180f;
            var rx = Matrix4x4.CreateRotationX(pitch * r);
            var ry = Matrix4x4.CreateRotationY(roll * r);
            var rz = Matrix4x4.CreateRotationZ(yaw * r);
            rotationMatrix = rz * ry * rx;

            // 頂点数×3 のバッファを確保
            var projected = new SKPoint[loadedTriangles.Count * 3];
            var colors = new SKColor[loadedTriangles.Count * 3];
            var indices = new ushort[loadedTriangles.Count * 3];

            ushort baseIndex = 0;
            for (int i = 0; i < loadedTriangles.Count; i++)
            {
                var tri = loadedTriangles[i];

                // 3D 回転＋重心移動 → 2D 投影
                var v0 = Transform(tri.Vertices[0]);
                var v1 = Transform(tri.Vertices[1]);
                var v2 = Transform(tri.Vertices[2]);

                projected[baseIndex + 0] = ProjectTo2D(v0);
                projected[baseIndex + 1] = ProjectTo2D(v1);
                projected[baseIndex + 2] = ProjectTo2D(v2);

                // インデックス
                indices[baseIndex + 0] = (ushort)(baseIndex + 0);
                indices[baseIndex + 1] = (ushort)(baseIndex + 1);
                indices[baseIndex + 2] = (ushort)(baseIndex + 2);

                // Z に応じた擬似陰影（遠くほど暗く）
                float zAvg = (v0.Z + v1.Z + v2.Z) / 3f;
                float shade = Math.Clamp(1f - (zAvg / zMax), 0.2f, 1f);
                byte bright = (byte)(shade * 255f);

                var c = new SKColor(bright, bright, 255);

                colors[baseIndex + 0] = c;
                colors[baseIndex + 1] = c;
                colors[baseIndex + 2] = c;

                baseIndex += 3;
            }

            // SKVertices を一度だけ構築
            skVerts = SKVertices.CreateCopy(
                SKVertexMode.Triangles,
                projected,
                null,
                colors,
                indices);

            // 再描画リクエスト
            MainThread.BeginInvokeOnMainThread(() =>
                canvasView.InvalidateSurface());
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
            zMax = pts.Max(p => p.Z);
        }

        // SKPoint3 → 回転行列適用後の Vector3
        Vector3 Transform(SKPoint3 pt)
        {
            var v = new Vector3(pt.X, pt.Y, pt.Z) - modelCenter;
            return Vector3.Transform(v, rotationMatrix) + modelCenter;
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