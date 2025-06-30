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
        private List<Triangle> loadedTriangles; // STL読み込み結果

        public PRYmonitorPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            loadedTriangles = await STLLoader.LoadFromResourceAsync("modelv46.stl");
            canvasView.InvalidateSurface();
        }


        void OnCanvasViewPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.White);

            if (loadedTriangles == null) return;

            using var paint = new SKPaint
            {
                Color = SKColors.Blue,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            foreach (var tri in loadedTriangles)
            {
                var path = new SKPath();
                var p0 = ProjectTo2D(tri.Vertices[0]);
                var p1 = ProjectTo2D(tri.Vertices[1]);
                var p2 = ProjectTo2D(tri.Vertices[2]);

                path.MoveTo(p0);
                path.LineTo(p1);
                path.LineTo(p2);
                path.Close();

                canvas.DrawPath(path, paint);
            }
        }

        SKPoint ProjectTo2D(SKPoint3 pt3D)
        {
            float scale = 50;   // 拡大率
            float offsetX = 200; // 中心移動（X）
            float offsetY = 200; // 中心移動（Y）

            return new SKPoint(
                pt3D.X * scale + offsetX,
                -pt3D.Y * scale + offsetY // Y軸反転（上方向が正）
            );
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
                    Rotetef(pitch, roll, yaw);
                }
            });
        }
        async Task Rotetef(float ptich, float roll , float yaw)
        {
        }

        protected override async void OnDisappearing()
        {
            base.OnDisappearing();
            if (_bleService != null)
            {
                // 必要な通知キーを指定して停止
                _bleService.StopNotificationAsync("PRY_Telem");
                // 他にも通知を止めたいCharacteristicがあればここで追加
                _bleService.NotificationReceived -= OnNotificationReceived;
            }
        }
    }
}