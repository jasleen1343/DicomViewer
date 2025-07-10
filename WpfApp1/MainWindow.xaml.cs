using FellowOakDicom;
using FellowOakDicom.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Controls;
using PathIO = System.IO.Path;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private MatrixTransform leftTransform = new MatrixTransform();
        private MatrixTransform rightTransform = new MatrixTransform();

        private double leftZoom = 1.0;
        private double rightZoom = 1.0;

        public double SliceThickness { get; set; }
        public ObservableCollection<SeriesThumbnailGroup> MriThumbnails { get; set; }
        public string SelectedViewer { get; set; } = "None";
        public ObservableCollection<string> DicomTags { get; set; } = new ObservableCollection<string>();

        private int leftImageIndex = 0;
        private int rightImageIndex = 0;
        private SeriesThumbnailGroup leftSeries;
        private SeriesThumbnailGroup rightSeries;

        private bool _isCrossReferenceActive = false;
        private bool _isFovActive = false;

        public MainWindow()
        {
            InitializeComponent();
            LeftImageContainer.RenderTransform = leftTransform;
            RightImageContainer.RenderTransform = rightTransform;
            MriThumbnails = new ObservableCollection<SeriesThumbnailGroup>();
            this.DataContext = this;
        }

        private void OpenDicomFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            var result = dialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                LoadDicomSeries(dialog.SelectedPath);
            }
        }

        private void LoadDicomSeries(string folderPath)
        {
            MainImageLeft.Source = null;
            MainImageRight.Source = null;
            DicomTags.Clear();
            MriThumbnails.Clear();
            leftImageIndex = 0;
            rightImageIndex = 0;
            leftSeries = null;
            rightSeries = null;
            ClearOverlays();
            leftTransform.Matrix = Matrix.Identity;
            rightTransform.Matrix = Matrix.Identity;
            leftZoom = 1.0;
            rightZoom = 1.0;
            var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(PathIO.GetExtension(f)))
                .ToList();

            var seriesDict = new Dictionary<string, SeriesThumbnailGroup>();

            foreach (var file in allFiles)
            {
                try
                {
                    var dicomFile = DicomFile.Open(file);
                    var dataset = dicomFile.Dataset;
                    
                    string seriesUID = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "UnknownSeries");
                    string seriesDesc = dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, $"Series {seriesUID}");

                    if (!dataset.TryGetValues(DicomTag.ImageOrientationPatient, out double[] iop) || iop.Length < 6) continue;
                    if (!dataset.TryGetValues(DicomTag.ImagePositionPatient, out double[] ipp) || ipp.Length < 3) continue;
                    if (!dataset.TryGetValues(DicomTag.PixelSpacing, out double[] spacing) || spacing.Length < 2) continue;

                    var rowDir = new Vector3D(iop[0], iop[1], iop[2]);
                    var colDir = new Vector3D(iop[3], iop[4], iop[5]);
                    var origin = new Vector3D(ipp[0], ipp[1], ipp[2]);

                    if (!seriesDict.TryGetValue(seriesUID, out var group))
                    {
                        int width = dataset.GetSingleValue<int>(DicomTag.Columns);
                        int height = dataset.GetSingleValue<int>(DicomTag.Rows);
                        double thickness = dataset.GetSingleValueOrDefault(DicomTag.SliceThickness, 1.0);

                        group = new SeriesThumbnailGroup
                        {
                            SeriesUID = seriesUID,
                            SeriesDescription = seriesDesc,
                            ImagePaths = new List<string>(),
                            Thumbnails = new ObservableCollection<BitmapImage>(),
                            SlicePositions = new List<double>(),
                            ImageOrigins = new List<Vector3D>(),
                            RowDirection = rowDir,
                            ColDirection = colDir,
                            PixelSpacingX = spacing[0],
                            PixelSpacingY = spacing[1],
                            Width = width,
                            Height = height,
                            SliceThickness = thickness
                        };

                        seriesDict[seriesUID] = group;
                    }

                    group.ImagePaths.Add(file);
                    group.ImageOrigins.Add(origin);
                    group.SlicePositions.Add(GetSliceLocation(dicomFile));
                }
                catch
                {
                    continue;
                }
            }

            foreach (var series in seriesDict.Values)
            {
                foreach (var path in series.ImagePaths)
                {
                    try
                    {
                        series.Thumbnails.Add(ConvertDicomToBitmapImage(path));
                    }
                    catch { continue; }
                }

                MriThumbnails.Add(series);
            }
        }

        private static double GetSliceLocation(DicomFile file)
        {
            var pos = file.Dataset.GetValues<double>(DicomTag.ImagePositionPatient);
            return pos.Length >= 3 ? pos[2] : 0;
        }

        private void DrawFOVBox( SeriesThumbnailGroup sourceSeries,int sourceSliceIndex,SeriesThumbnailGroup targetSeries,Canvas targetCanvas)
        {
            // Clear any previous drawings
            targetCanvas.Children.Clear();

            // Determine zoom
            double zoom = targetCanvas == OverlayCanvasLeft ? leftZoom : rightZoom;

            // Loop over all slices
            for (int i = 0; i < sourceSeries.ImageOrigins.Count; i++)
            {
                var origin = sourceSeries.ImageOrigins[i];
                var row = sourceSeries.RowDirection;
                var col = sourceSeries.ColDirection;

                double width = sourceSeries.Width;
                double height = sourceSeries.Height;

                var c1 = origin;
                var c2 = origin + row * sourceSeries.PixelSpacingX * width;
                var c3 = c2 + col * sourceSeries.PixelSpacingY * height;
                var c4 = origin + col * sourceSeries.PixelSpacingY * height;

                // Project corners
                Point p1 = ProjectToViewer(targetSeries, c1);
                Point p2 = ProjectToViewer(targetSeries, c2);
                Point p3 = ProjectToViewer(targetSeries, c3);
                Point p4 = ProjectToViewer(targetSeries, c4);

                // Apply zoom scaling
                p1 = new Point(p1.X * zoom, p1.Y * zoom);
                p2 = new Point(p2.X * zoom, p2.Y * zoom);
                p3 = new Point(p3.X * zoom, p3.Y * zoom);
                p4 = new Point(p4.X * zoom, p4.Y * zoom);

                // Highlight selected slice
                Brush brush = i == sourceSliceIndex ? Brushes.Red : Brushes.LimeGreen;

                var polygon = new Polygon
                {
                    Points = new PointCollection { p1, p2, p3, p4 },
                    Stroke = brush,
                    StrokeThickness = 2,
                    Fill = null
                };
                targetCanvas.Children.Add(polygon);
            }
        }

        private Point ProjectToViewer(SeriesThumbnailGroup targetSeries, Vector3D point)
        {
            var vector = point - targetSeries.ImageOrigins[0];
            double x = Vector3D.Dot(vector, targetSeries.RowDirection) / targetSeries.PixelSpacingX;
            double y = Vector3D.Dot(vector, targetSeries.ColDirection) / targetSeries.PixelSpacingY;
            x = Math.Max(0, Math.Min(OverlayCanvasRight.ActualWidth, x));
            y = Math.Max(0, Math.Min(OverlayCanvasRight.ActualHeight, y));
            return new Point(x, y);
        }

        private BitmapImage ConvertDicomToBitmapImage(string filePath)
        {
            var dicomImage = new DicomImage(filePath);
            var renderImage = dicomImage.RenderImage();
            var pixels = renderImage.Pixels;
            int width = renderImage.Width;
            int height = renderImage.Height;
            int stride = width * 4;

            var bitmapSource = BitmapSource.Create(
                width, height, 96, 96,
                PixelFormats.Bgra32, null,
                pixels.Data, stride);

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(ms);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(ms.ToArray());
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }


        private void Thumbnail_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Image image && image.Source is BitmapImage bitmap)
            {
                var clickedSeries = MriThumbnails
                    .SelectMany(group => group.Thumbnails.Select((thumb, index) => new { group, thumb, index }))
                    .FirstOrDefault(x => x.thumb == bitmap);

                if (clickedSeries != null)
                {
                    if (SelectedViewer == "Left")
                    {
                        MainImageLeft.Source = bitmap;
                        leftImageIndex = clickedSeries.index;
                        leftSeries = clickedSeries.group;
                    }
                    else if (SelectedViewer == "Right")
                    {
                        MainImageRight.Source = bitmap;
                        rightImageIndex = clickedSeries.index;
                        rightSeries = clickedSeries.group;
                    }

                    string filePath = clickedSeries.group.ImagePaths[clickedSeries.index];
                    LoadDicomTags(filePath);

                    if (_isCrossReferenceActive)
                        UpdateCrossReferenceLines();
                    else if (_isFovActive)
                        UpdateFovOverlay();
                }
            }
        }

        private void LoadDicomTags(string filePath)
        {
            DicomTags.Clear();

            try
            {
                var dicomFile = DicomFile.Open(filePath);

                foreach (var item in dicomFile.Dataset)
                {
                    string tag = item.Tag.DictionaryEntry.Name;
                    string value = dicomFile.Dataset.GetValueOrDefault(item.Tag, 0, string.Empty);
                    DicomTags.Add($"{tag}: {value}");
                }
            }
            catch (Exception ex)
            {
                DicomTags.Add($"Error reading DICOM tags: {ex.Message}");
            }

            DataContext = null;
            DataContext = this;
        }

        private void LeftViewer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SelectedViewer = "Left";
            DataContext = null;
            DataContext = this;
        }

        private void RightViewer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SelectedViewer = "Right";
            DataContext = null;
            DataContext = this;
        }

        private void MainImageLeft_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ZoomImage(leftTransform, LeftImageContainer, e);
        }

        private void MainImageRight_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ZoomImage(rightTransform, RightImageContainer, e);
        }

        

        private bool AreVectorsOrthogonal(Vector3D a, Vector3D b)
        {
            double dot = Vector3D.Dot(a, b);
            return Math.Abs(dot) < 0.05;
        }

        private bool AreVectorsApproximatelyEqual(Vector3D a, Vector3D b, double tolerance = 0.01)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz < tolerance * tolerance;
        }

        private void CrossReferenceButton_Click(object sender, RoutedEventArgs e)
        {
            _isCrossReferenceActive = !_isCrossReferenceActive;
            _isFovActive = false;

            ClearOverlays();

            if (_isCrossReferenceActive)
                UpdateCrossReferenceLines();
        }

        private void FovButton_Click(object sender, RoutedEventArgs e)
        {
            _isFovActive = !_isFovActive;
            _isCrossReferenceActive = false;

            ClearOverlays();

            if (_isFovActive)
                UpdateFovOverlay();
        }

        private void ClearOverlays()
        {
            OverlayCanvasLeft.Children.Clear();
            OverlayCanvasRight.Children.Clear();
        }

        private void UpdateCrossReferenceLines()
        {
            OverlayCanvasLeft.Children.Clear();
            OverlayCanvasRight.Children.Clear();

            if (leftSeries == null || rightSeries == null) return;

            var normalLeft = leftSeries.SliceNormal;
            var normalRight = rightSeries.SliceNormal;

            if (AreVectorsApproximatelyEqual(normalLeft, normalRight))
                return;

            if (!AreVectorsOrthogonal(normalLeft, normalRight))
                return;

            DrawCrossReferenceLine("Left", leftSeries, rightSeries, leftImageIndex, OverlayCanvasRight);
            DrawCrossReferenceLine("Right", rightSeries, leftSeries, rightImageIndex, OverlayCanvasLeft);
        }

        private void DrawCrossReferenceLine(
            string viewer,
            SeriesThumbnailGroup sourceSeries,
            SeriesThumbnailGroup targetSeries,
            int sourceIndex,
            Canvas targetCanvas)
        {
            if (sourceSeries.ImageOrigins.Count <= sourceIndex) return;

            var originA = sourceSeries.ImageOrigins[sourceIndex];
            var rowA = sourceSeries.RowDirection;
            var colA = sourceSeries.ColDirection;

            double centerX = sourceSeries.Width / 2.0;
            double centerY = sourceSeries.Height / 2.0;

            Vector3D pointA = originA +
                              rowA * sourceSeries.PixelSpacingX * centerX +
                              colA * sourceSeries.PixelSpacingY * centerY;

            var originB = targetSeries.ImageOrigins[0];
            var normalB = targetSeries.SliceNormal;
            var rowB = targetSeries.RowDirection;
            var colB = targetSeries.ColDirection;

            Vector3D diff = pointA - originB;
            double d = Vector3D.Dot(diff, normalB);
            Vector3D projected = pointA - normalB * d;

            double x = Vector3D.Dot(projected - originB, rowB) / targetSeries.PixelSpacingX;
            double y = Vector3D.Dot(projected - originB, colB) / targetSeries.PixelSpacingY;

            double zoom = viewer == "Left" ? leftZoom : rightZoom;

            var vertical = new Line
            {
                X1 = x * zoom,
                Y1 = 0,
                X2 = x * zoom,
                Y2 = targetCanvas.ActualHeight * zoom,
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 2
            };


            targetCanvas.Children.Add(vertical);
        }

        private void UpdateFovOverlay()
        {
            OverlayCanvasRight.Children.Clear();
            OverlayCanvasLeft.Children.Clear();

            if (leftSeries != null && rightSeries != null)
            {
                DrawFOVBox(leftSeries, leftImageIndex, rightSeries, OverlayCanvasRight);
            }

        }

        private void ZoomImage(MatrixTransform transform, UIElement container, MouseWheelEventArgs e)
        {
            // Get current matrix
            Matrix matrix = transform.Matrix;

            // Determine zoom factor
            double zoom = e.Delta > 0 ? 1.2 : 1 / 1.2;

            // Mouse position relative to container
            Point mousePos = e.GetPosition(container);

            // Scale around mouse position
            matrix.ScaleAt(zoom, zoom, mousePos.X, mousePos.Y);

            // Apply updated matrix
            transform.Matrix = matrix;
        }

        public class SeriesThumbnailGroup : INotifyPropertyChanged
        {
            public string SeriesUID { get; set; }
            public string SeriesDescription { get; set; }
            public List<string> ImagePaths { get; set; }
            public ObservableCollection<BitmapImage> Thumbnails { get; set; }
            public List<double> SlicePositions { get; set; }
            public List<Vector3D> ImageOrigins { get; set; } = new();
            public Vector3D RowDirection { get; set; }
            public Vector3D ColDirection { get; set; }
            public Vector3D SliceNormal => Vector3D.Cross(RowDirection, ColDirection);
            public double PixelSpacingX { get; set; }
            public double PixelSpacingY { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }

            public double SliceThickness { get; set; }

            private bool isExpanded;
            public bool IsExpanded
            {
                get => isExpanded;
                set
                {
                    if (isExpanded != value)
                    {
                        isExpanded = value;
                        OnPropertyChanged();
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            protected void OnPropertyChanged([CallerMemberName] string name = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public struct Vector3D
        {
            public double X, Y, Z;

            public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }

            public static Vector3D operator -(Vector3D a, Vector3D b) =>
                new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

            public static Vector3D operator +(Vector3D a, Vector3D b) =>
                new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

            public static Vector3D operator *(Vector3D v, double scalar) =>
                new Vector3D(v.X * scalar, v.Y * scalar, v.Z * scalar);

            public static double Dot(Vector3D a, Vector3D b) =>
                a.X * b.X + a.Y * b.Y + a.Z * b.Z;

            public static Vector3D Cross(Vector3D a, Vector3D b) =>
                new Vector3D(
                    a.Y * b.Z - a.Z * b.Y,
                    a.Z * b.X - a.X * b.Z,
                    a.X * b.Y - a.Y * b.X
                );
        }
    }
}

