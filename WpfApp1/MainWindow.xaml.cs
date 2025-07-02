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
        public ObservableCollection<SeriesThumbnailGroup> MriThumbnails { get; set; }
        public string SelectedViewer { get; set; } = "None";
        public ObservableCollection<string> DicomTags { get; set; } = new ObservableCollection<string>();

        private int leftImageIndex = 0;
        private int rightImageIndex = 0;
        private SeriesThumbnailGroup leftSeries;
        private SeriesThumbnailGroup rightSeries;

        public MainWindow()
        {
            InitializeComponent();
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
            MriThumbnails.Clear();
            var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(System.IO.Path.GetExtension(f))
)
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

                    // Read essential geometry
                    if (!dataset.TryGetValues(DicomTag.ImageOrientationPatient, out double[] iop) || iop.Length < 6) continue;
                    if (!dataset.TryGetValues(DicomTag.ImagePositionPatient, out double[] ipp) || ipp.Length < 3) continue;
                    if (!dataset.TryGetValues(DicomTag.PixelSpacing, out double[] spacing) || spacing.Length < 2) continue;

                    var rowDir = new Vector3D(iop[0], iop[1], iop[2]);
                    var colDir = new Vector3D(iop[3], iop[4], iop[5]);
                    var origin = new Vector3D(ipp[0], ipp[1], ipp[2]);

                    if (!seriesDict.TryGetValue(seriesUID, out var group))
                    {
                        var orientation = dicomFile.Dataset.GetValues<double>(DicomTag.ImageOrientationPatient);
                        var row = new Vector3D(orientation[0], orientation[1], orientation[2]);
                        var col = new Vector3D(orientation[3], orientation[4], orientation[5]);
                        var normal = Vector3D.Cross(row, col);

                        var spacingValues = dicomFile.Dataset.GetValues<double>(DicomTag.PixelSpacing);
                        double spacingX = spacingValues[0];
                        double spacingY = spacingValues[1];

                        int width = dicomFile.Dataset.GetSingleValue<int>(DicomTag.Columns);
                        int height = dicomFile.Dataset.GetSingleValue<int>(DicomTag.Rows);

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
                            PixelSpacingX = spacingX,
                            PixelSpacingY = spacingY,
                            Width = width,
                            Height = height
                        };

                        seriesDict[seriesUID] = group;
                    }

                    group.ImagePaths.Add(file);
                    group.ImageOrigins.Add(origin);
                    group.SlicePositions.Add(GetSliceLocation(dicomFile));
                }
                catch
                {
                    // Skip corrupt or unreadable files
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
                    catch
                    {
                        // Skip if thumbnail can't be created
                        continue;
                    }
                }

                MriThumbnails.Add(series);
            }
        }


        private static double GetSliceLocation(DicomFile file)
        {
            var pos = file.Dataset.GetValues<double>(DicomTag.ImagePositionPatient);
            return pos.Length >= 3 ? pos[2] : 0;
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
            if (sender is System.Windows.Controls.Image image &&
                image.Source is BitmapImage bitmap)
            {
                var clickedSeries = MriThumbnails
                    .SelectMany(group => group.Thumbnails
                        .Select((thumb, index) => new { group, thumb, index }))
                    .FirstOrDefault(x => x.thumb == bitmap);

                if (clickedSeries != null)
                {
                    if (SelectedViewer == "Left")
                    {
                        MainImageLeft.Source = bitmap;
                        leftImageIndex = clickedSeries.index;
                        leftSeries = clickedSeries.group;

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            UpdateCrossReferenceLines();
                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                    else if (SelectedViewer == "Right")
                    {
                        MainImageRight.Source = bitmap;
                        rightImageIndex = clickedSeries.index;
                        rightSeries = clickedSeries.group;

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            UpdateCrossReferenceLines();
                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                    }


                    string filePath = clickedSeries.group.ImagePaths[clickedSeries.index];
                    LoadDicomTags(filePath);
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
            double zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
            ZoomImage(ScaleTransformLeft, zoomDelta);
        }

        private void MainImageRight_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
            ZoomImage(ScaleTransformRight, zoomDelta);
        }

        private void ZoomImage(ScaleTransform transform, double delta)
        {
            double newScaleX = transform.ScaleX + delta;
            double newScaleY = transform.ScaleY + delta;

            newScaleX = Math.Max(0.1, Math.Min(5.0, newScaleX));
            newScaleY = Math.Max(0.1, Math.Min(5.0, newScaleY));

            transform.ScaleX = newScaleX;
            transform.ScaleY = newScaleY;
        }

        private bool AreVectorsOrthogonal(Vector3D a, Vector3D b)
        {
            double dot = Vector3D.Dot(a, b);
            return Math.Abs(dot) < 0.05; // Adjust threshold if needed
        }

        private void UpdateCrossReferenceLines()
        {
            OverlayCanvasLeft.Children.Clear();
            OverlayCanvasRight.Children.Clear();

            if (leftSeries == null || rightSeries == null) return;

            var normalLeft = leftSeries.SliceNormal;
            var normalRight = rightSeries.SliceNormal;

            // If planes are the same → skip
            if (AreVectorsApproximatelyEqual(normalLeft, normalRight))
                return;

            // If not orthogonal → skip
            if (!AreVectorsOrthogonal(normalLeft, normalRight))
                return;

            // Now draw cross-reference lines on BOTH viewers
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
            if (targetSeries.ImageOrigins.Count == 0) return;

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
            var spacingXB = targetSeries.PixelSpacingX;
            var spacingYB = targetSeries.PixelSpacingY;

            Vector3D diff = pointA - originB;
            double d = Vector3D.Dot(diff, normalB);
            Vector3D projected = pointA - normalB * d;

            double x = Vector3D.Dot(projected - originB, rowB) / spacingXB;
            double y = Vector3D.Dot(projected - originB, colB) / spacingYB;

            double clampedX = Math.Max(0, Math.Min(targetCanvas.ActualWidth, x));
            double clampedY = Math.Max(0, Math.Min(targetCanvas.ActualHeight, y));

            if (double.IsNaN(clampedX) || double.IsInfinity(clampedX) ||
                double.IsNaN(clampedY) || double.IsInfinity(clampedY))
                return;

            var vertical = new Line
            {
                X1 = clampedX,
                Y1 = 0,
                X2 = clampedX,
                Y2 = targetCanvas.ActualHeight,
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 2
            };

            
            targetCanvas.Children.Add(vertical);
        }


        private bool AreVectorsApproximatelyEqual(Vector3D a, Vector3D b, double tolerance = 0.01)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;

            var distanceSquared = dx * dx + dy * dy + dz * dz;
            return distanceSquared < tolerance * tolerance;
        }


        public class SeriesThumbnailGroup : INotifyPropertyChanged
        {
            public string SeriesUID { get; set; }
            public string SeriesDescription { get; set; }
            public List<string> ImagePaths { get; set; }
            public ObservableCollection<BitmapImage> Thumbnails { get; set; }
            public List<double> SlicePositions { get; set; }
            public Vector3D Orientation { get; set; }
            public bool IsAxial { get; set; }
            public List<Vector3D> ImageOrigins { get; set; } = new(); // ImagePositionPatient
            public Vector3D RowDirection { get; set; } // Orientation Row
            public Vector3D ColDirection { get; set; } // Orientation Column
            public Vector3D SliceNormal => Vector3D.Cross(RowDirection, ColDirection);
            public double PixelSpacingX { get; set; }
            public double PixelSpacingY { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }

            

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
