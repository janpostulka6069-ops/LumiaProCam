using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Sensors;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace LumiaProCam
{
    public sealed partial class MainPage : Page
    {
        private MediaCapture _mediaCapture;
        private MediaFrameReader _frameReader;
        private SoftwareBitmap _latestBitmap;
        private readonly object _frameLock = new object();

        private Accelerometer _accelerometer;
        private float _currentRollRad = 0f;
        private float _zoomLevel = 1f;
        private bool _isRecording = false;

        private StreamSocketListener _httpListener;
        private byte[] _latestJpegBytes;
        private bool _isStreaming = false;
        private bool _isEncoding = false;

        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += MainPage_Loaded;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitCameraAsync();
                InitSensors();
                TxtStatus.Text = "Kamera bezi";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Chyba: " + ex.Message;
            }
        }

        private async Task InitCameraAsync()
        {
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };
            await _mediaCapture.InitializeAsync(settings);

            var frameSource = _mediaCapture.FrameSources.Values
                .FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview);

            if (frameSource != null)
            {
                _frameReader = await _mediaCapture.CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8);
                _frameReader.FrameArrived += FrameReader_FrameArrived;
                await _frameReader.StartAsync();
            }

            var focus = _mediaCapture.VideoDeviceController.FocusControl;
            if (focus.Supported)
            {
                SliderFocus.Minimum = focus.Min;
                SliderFocus.Maximum = focus.Max;
                SliderFocus.Value = focus.Value;
            }
        }

        private void InitSensors()
        {
            _accelerometer = Accelerometer.GetDefault();
            if (_accelerometer != null)
            {
                _accelerometer.ReportInterval = 20;
                _accelerometer.ReadingChanged += (s, args) =>
                {
                    var r = args.Reading;
                    _currentRollRad = (float)Math.Atan2(r.AccelerationX, -r.AccelerationY);
                };
            }
        }

        private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            using (var frame = sender.TryAcquireLatestFrame())
            {
                var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bmp == null) return;

                var converted = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                SoftwareBitmap streamCopy = null;

                lock (_frameLock)
                {
                    _latestBitmap?.Dispose();
                    _latestBitmap = converted;
                    if (_isStreaming && !_isEncoding)
                    {
                        _isEncoding = true;
                        streamCopy = SoftwareBitmap.Copy(converted);
                    }
                }

                PreviewCanvas.Invalidate();

                if (streamCopy != null)
                {
                    _ = EncodeFrameForStreamAsync(streamCopy);
                }
            }
        }

        private void PreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            SoftwareBitmap bmpCopy = null;
            lock (_frameLock)
            {
                if (_latestBitmap != null)
                    bmpCopy = SoftwareBitmap.Copy(_latestBitmap);
            }
            if (bmpCopy == null) return;

            using (bmpCopy)
            using (var canvasBmp = CanvasBitmap.CreateFromSoftwareBitmap(sender, bmpCopy))
            {
                ICanvasImage imageToDraw = canvasBmp;

                if (BtnSharp.IsChecked == true)
                {
                    imageToDraw = new SharpenEffect
                    {
                        Source = imageToDraw,
                        Amount = _zoomLevel > 10 ? 5.0f : 2.0f
                    };
                }

                float imgW = (float)canvasBmp.Size.Width;
                float imgH = (float)canvasBmp.Size.Height;
                float screenW = (float)sender.ActualWidth;
                float screenH = (float)sender.ActualHeight;

                float baseScale = Math.Max(screenW / imgW, screenH / imgH);
                float scale = _zoomLevel;
                float rotation = (BtnHorizon.IsChecked == true) ? -_currentRollRad : 0f;

                if (BtnHorizon.IsChecked == true && scale < 1.45f)
                    scale = 1.45f;

                var matrix =
                    Matrix3x2.CreateTranslation(-imgW / 2f, -imgH / 2f) *
                    Matrix3x2.CreateRotation(rotation) *
                    Matrix3x2.CreateScale(baseScale * scale) *
                    Matrix3x2.CreateTranslation(screenW / 2f, screenH / 2f);

                var transformEffect = new Transform2DEffect
                {
                    Source = imageToDraw,
                    TransformMatrix = matrix,
                    InterpolationMode = CanvasImageInterpolation.HighQualityCubic
                };

                args.DrawingSession.DrawImage(transformEffect);
            }
        }

        private async void SliderFocus_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_mediaCapture == null) return;
            try
            {
                var focus = _mediaCapture.VideoDeviceController.FocusControl;
                if (!focus.Supported) return;
                focus.Configure(new FocusSettings
                {
                    Mode = FocusMode.Manual,
                    Value = (uint)e.NewValue,
                    DisableDriverFallback = true
                });
                await focus.FocusAsync();
            }
            catch { }
        }

        private void SliderZoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _zoomLevel = (float)e.NewValue;
            if (TxtZoom != null) TxtZoom.Text = $"Zoom {_zoomLevel:0}x:";
        }

        private async void SliderIso_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_mediaCapture == null) return;
            try
            {
                var iso = _mediaCapture.VideoDeviceController.IsoSpeedControl;
                if (iso.Supported)
                    await iso.SetValueAsync((uint)e.NewValue);
            }
            catch { }
        }

        private async void BtnMoon_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaCapture == null) return;
            try
            {
                var vdc = _mediaCapture.VideoDeviceController;
                if (BtnMoon.IsChecked == true)
                {
                    if (vdc.IsoSpeedControl.Supported) await vdc.IsoSpeedControl.SetValueAsync(50);
                    if (vdc.ExposureControl.Supported)
                    {
                        await vdc.ExposureControl.SetAutoAsync(false);
                        await vdc.ExposureControl.SetValueAsync(TimeSpan.FromMilliseconds(2));
                    }
                    SliderFocus.Value = SliderFocus.Maximum;
                    SliderZoom.Value = 30;
                    TxtStatus.Text = "SuperMoon aktivni";
                }
                else
                {
                    if (vdc.ExposureControl.Supported) await vdc.ExposureControl.SetAutoAsync(true);
                    if (vdc.IsoSpeedControl.Supported) await vdc.IsoSpeedControl.SetAutoAsync();
                    SliderZoom.Value = 1;
                    TxtStatus.Text = "Auto rezim";
                }
            }
            catch (Exception ex) { TxtStatus.Text = ex.Message; }
        }

        private async void BtnPhoto_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaCapture == null) return;
            try
            {
                var file = await KnownFolders.CameraRoll.CreateFileAsync(
                    $"LUMIA_{DateTime.Now:yyyyMMdd_HHmmss}.jpg", CreationCollisionOption.GenerateUniqueName);
                await _mediaCapture.CapturePhotoToStorageFileAsync(ImageEncodingProperties.CreateJpeg(), file);
                TxtStatus.Text = $"Ulozeno: {file.Name}";
            }
            catch (Exception ex) { TxtStatus.Text = "Chyba fotky: " + ex.Message; }
        }

        private async void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaCapture == null) return;
            try
            {
                if (!_isRecording)
                {
                    var file = await KnownFolders.CameraRoll.CreateFileAsync(
                        $"LUMIA_{DateTime.Now:yyyyMMdd_HHmmss}.mp4", CreationCollisionOption.GenerateUniqueName);
                    var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
                    await _mediaCapture.StartRecordToStorageFileAsync(profile, file);
                    _isRecording = true;
                    BtnRecord.Content = "STOP REC";
                    TxtStatus.Text = "Natacim video...";
                }
                else
                {
                    await _mediaCapture.StopRecordAsync();
                    _isRecording = false;
                    BtnRecord.Content = "REC VIDEO";
                    TxtStatus.Text = "Video ulozeno";
                }
            }
            catch (Exception ex) { TxtStatus.Text = "Chyba videa: " + ex.Message; }
        }

        private async void BtnStream_Click(object sender, RoutedEventArgs e)
        {
            if (BtnStream.IsChecked == true)
            {
                try
                {
                    _httpListener = new StreamSocketListener();
                    _httpListener.ConnectionReceived += HttpListener_ConnectionReceived;
                    await _httpListener.BindServiceNameAsync("8080");
                    _isStreaming = true;
                    var ip = NetworkInformation.GetHostNames()
                        .FirstOrDefault(h => h.IPInformation != null && h.Type == HostNameType.Ipv4)?.CanonicalName ?? "IP";
                    TxtStatus.Text = $"Stream: http://{ip}:8080";
                }
                catch (Exception ex) { TxtStatus.Text = "Chyba streamu: " + ex.Message; }
            }
            else
            {
                _isStreaming = false;
                _httpListener?.Dispose();
                TxtStatus.Text = "Stream zastaven";
            }
        }

        private async Task EncodeFrameForStreamAsync(SoftwareBitmap bmp)
        {
            try
            {
                using (bmp)
                using (var ms = new InMemoryRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
                    encoder.SetSoftwareBitmap(bmp);
                    encoder.BitmapTransform.ScaledWidth = 640;
                    encoder.BitmapTransform.ScaledHeight = 360;
                    await encoder.FlushAsync();

                    var bytes = new byte[ms.Size];
                    await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
                    _latestJpegBytes = bytes;
                }
            }
            catch { }
            finally { _isEncoding = false; }
        }

        private async void HttpListener_ConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            try
            {
                using (var output = args.Socket.OutputStream.AsStreamForWrite())
                {
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\n\r\n");
                    await output.WriteAsync(header, 0, header.Length);

                    while (_isStreaming)
                    {
                        var frame = _latestJpegBytes;
                        if (frame != null)
                        {
                            var boundary = Encoding.ASCII.GetBytes(
                                $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                            await output.WriteAsync(boundary, 0, boundary.Length);
                            await output.WriteAsync(frame, 0, frame.Length);
                            await output.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), 0, 2);
                            await output.FlushAsync();
                        }
                        await Task.Delay(50);
                    }
                }
            }
            catch { }
        }
    }
}
