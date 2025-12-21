using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FluxionJsSceneEditor
{
    public partial class MainWindow : Window
    {
        private SceneModel _scene = new SceneModel();
        private BaseElement? _selected;
        private double _zoom = 1.0;
        private bool _isDragging = false;
        private System.Windows.Point _dragStartCanvasPoint;
        private BaseElement? _dragElement;
        private string? _projectFolderPath;
        private string? _loadedSceneFilePath;
        private bool _isPanning;
        private System.Windows.Point _panStartCanvasPoint;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => ZoomSlider.ValueChanged += ZoomSlider_ValueChanged;
            StatusTextBlock.Text = "Ready";
            NewScene();
        }

        // ---- Project tree: lazy loading ----

        private const string PlaceholderTag = "__PLACEHOLDER__";

        private void RefreshProjectTree()
        {
            ProjectTreeView.Items.Clear();
            ProjectFolderPathTextBlock.Text = string.IsNullOrWhiteSpace(_projectFolderPath) ? "(none)" : _projectFolderPath;
            if (string.IsNullOrWhiteSpace(_projectFolderPath) || !Directory.Exists(_projectFolderPath))
                return;

            var rootDir = new DirectoryInfo(_projectFolderPath);
            var rootNode = CreateDirectoryNodeLazy(rootDir);
            rootNode.IsExpanded = true; // optional: expand root
            ProjectTreeView.Items.Add(rootNode);
        }

        private TreeViewItem CreateDirectoryNodeLazy(DirectoryInfo dir)
        {
            var node = new TreeViewItem { Header = dir.Name, Tag = dir.FullName };
            // Add a single placeholder to indicate expandable content
            node.Items.Add(new TreeViewItem { Header = "(loading...)", Tag = PlaceholderTag });
            node.Expanded += DirectoryNode_Expanded;
            return node;
        }

        private void DirectoryNode_Expanded(object? sender, RoutedEventArgs e)
        {
            var node = sender as TreeViewItem;
            if (node == null) return;

            // If first child is placeholder, load real children
            if (node.Items.Count == 1 && node.Items[0] is TreeViewItem ph && Equals(ph.Tag, PlaceholderTag))
            {
                node.Items.Clear();
                var path = node.Tag as string;
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

                try
                {
                    // Add subdirectories (with their own placeholders)
                    foreach (var subDir in SafeGetDirectories(path))
                        node.Items.Add(CreateDirectoryNodeLazy(subDir));

                    // Add files
                    foreach (var file in SafeGetFiles(path))
                        node.Items.Add(new TreeViewItem { Header = file.Name, Tag = file.FullName });
                }
                catch (Exception ex)
                {
                    node.Items.Add(new TreeViewItem { Header = $"(error: {ex.Message})", Tag = null });
                }
            }
        }

        private static IEnumerable<DirectoryInfo> SafeGetDirectories(string path)
        {
            try { return new DirectoryInfo(path).GetDirectories(); }
            catch { return Array.Empty<DirectoryInfo>(); }
        }

        private static IEnumerable<FileInfo> SafeGetFiles(string path)
        {
            try { return new DirectoryInfo(path).GetFiles(); }
            catch { return Array.Empty<FileInfo>(); }
        }

        private void RefreshProjectButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshProjectTree();
        }

        private void ProjectTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            TryOpenSelectedProjectTreeFile();
        }

        private void ProjectTreeView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TryOpenSelectedProjectTreeFile();
                e.Handled = true;
            }
        }

        private void TryOpenSelectedProjectTreeFile()
        {
            if (ProjectTreeView.SelectedItem is not TreeViewItem item)
                return;

            if (item.Tag is not string path)
                return;

            if (!File.Exists(path))
                return;

            var ext = System.IO.Path.GetExtension(path);
            if (!string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase))
            {
                StatusTextBlock.Text = $"Not a scene file: {System.IO.Path.GetFileName(path)}";
                return;
            }

            try
            {
                LoadSceneFromFile(path);
                StatusTextBlock.Text = $"Loaded scene: {System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Failed to load scene: {ex.Message}";
            }
        }

        private void LoadSceneFromFile(string filePath)
        {
            _loadedSceneFilePath = filePath;

            var text = File.ReadAllText(filePath);
            var loaded = SceneSerializer.Deserialize(text);

            _scene = loaded;
            _selected = null;

            SceneNameTextBox.Text = _scene.Name;
            CameraXTextBox.Text = _scene.Camera.X.ToString(CultureInfo.InvariantCulture);
            CameraYTextBox.Text = _scene.Camera.Y.ToString(CultureInfo.InvariantCulture);
            CameraZoomTextBox.Text = _scene.Camera.Zoom.ToString(CultureInfo.InvariantCulture);

            RefreshHierarchy();
            RefreshCanvas();
        }

        private string? ResolveSpriteImagePath(string? imageSrc)
        {
            if (string.IsNullOrWhiteSpace(imageSrc))
                return imageSrc;

            // Absolute path or URL
            if (Uri.TryCreate(imageSrc, UriKind.Absolute, out var absUri))
            {
                if (absUri.IsFile)
                    return absUri.LocalPath;

                // http(s) etc - let WPF handle it
                return imageSrc;
            }

            // Resolve relative paths against the loaded scene file directory first
            if (!string.IsNullOrWhiteSpace(_loadedSceneFilePath))
            {
                var baseDir = System.IO.Path.GetDirectoryName(_loadedSceneFilePath);
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, imageSrc));
                    if (File.Exists(combined))
                        return combined;

                    // Also try relative to selected project folder (common for engine projects)
                    if (!string.IsNullOrWhiteSpace(_projectFolderPath))
                    {
                        var combinedProject = System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectFolderPath, imageSrc));
                        if (File.Exists(combinedProject))
                            return combinedProject;
                    }

                    return combined;
                }
            }

            // Fallback: relative to project folder if we have one
            if (!string.IsNullOrWhiteSpace(_projectFolderPath))
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectFolderPath, imageSrc));

            return imageSrc;
        }

        private double GetCanvasAspect()
        {
            var w = EditorCanvas?.ActualWidth;
            var h = EditorCanvas?.ActualHeight;
            if (w is null || h is null || w <= 0 || h <= 0)
                return EditorCanvas?.Width > 0 && EditorCanvas?.Height > 0
                    ? (EditorCanvas.Width / EditorCanvas.Height)
                    : 1.0;
            return w.Value / h.Value;
        }

        // Engine-style mapping:
        // NDC x = ((world.x - cam.x) * cam.zoom) / aspect
        // NDC y = ((world.y - cam.y) * cam.zoom)
        // Canvas: x_px = (ndc.x * 0.5 + 0.5) * canvasWidth
        //         y_px = (1 - (ndc.y * 0.5 + 0.5)) * canvasHeight (flip Y so +Y is up)
        private System.Windows.Point WorldToCanvasPoint(double worldX, double worldY)
        {
            var canvasW = EditorCanvas.ActualWidth > 0 ? EditorCanvas.ActualWidth : EditorCanvas.Width;
            var canvasH = EditorCanvas.ActualHeight > 0 ? EditorCanvas.ActualHeight : EditorCanvas.Height;
            var aspect = GetCanvasAspect();

            var ndcX = ((worldX - _scene.Camera.X) * _scene.Camera.Zoom) / aspect;
            var ndcY = ((worldY - _scene.Camera.Y) * _scene.Camera.Zoom);

            var xPx = (ndcX * 0.5 + 0.5) * canvasW;
            var yPx = (1.0 - (ndcY * 0.5 + 0.5)) * canvasH;
            return new System.Windows.Point(xPx, yPx);
        }

        private System.Windows.Size WorldToCanvasSize(double worldW, double worldH)
        {
            var canvasW = EditorCanvas.ActualWidth > 0 ? EditorCanvas.ActualWidth : EditorCanvas.Width;
            var canvasH = EditorCanvas.ActualHeight > 0 ? EditorCanvas.ActualHeight : EditorCanvas.Height;
            var aspect = GetCanvasAspect();

            // Convert a delta in world units to NDC delta, then to pixels.
            var ndcW = (worldW * _scene.Camera.Zoom) / aspect;
            var ndcH = (worldH * _scene.Camera.Zoom);

            var pxW = ndcW * 0.5 * canvasW;
            var pxH = ndcH * 0.5 * canvasH;
            return new System.Windows.Size(pxW, pxH);
        }

        private (double worldDx, double worldDy) CanvasDeltaToWorldDelta(double dxPx, double dyPx)
        {
            var canvasW = EditorCanvas.ActualWidth > 0 ? EditorCanvas.ActualWidth : EditorCanvas.Width;
            var canvasH = EditorCanvas.ActualHeight > 0 ? EditorCanvas.ActualHeight : EditorCanvas.Height;
            var aspect = GetCanvasAspect();

            // canvas dx -> ndc dx
            var ndcDx = (dxPx / canvasW) * 2.0;
            // canvas dy is down-positive; ndc dy is up-positive
            var ndcDy = -(dyPx / canvasH) * 2.0;

            var worldDx = (ndcDx * aspect) / _scene.Camera.Zoom;
            var worldDy = (ndcDy) / _scene.Camera.Zoom;
            return (worldDx, worldDy);
        }

        private void RefreshCanvas()
        {
            if (EditorCanvas == null || _scene == null) return;

            EditorCanvas.Children.Clear();

            foreach (var el in _scene.Elements)
            {
                if (el is AudioElement)
                    continue; // not visual

                FrameworkElement visual;
                var sizePx = WorldToCanvasSize(el.Width, el.Height);

                if (el is SpriteElement se && !string.IsNullOrWhiteSpace(se.ImageSrc))
                {
                    System.Windows.Controls.Image? img = new System.Windows.Controls.Image();
                    try
                    {
                        var resolved = ResolveSpriteImagePath(se.ImageSrc);
                        if (!string.IsNullOrWhiteSpace(resolved))
                            img.Source = new BitmapImage(new Uri(resolved, UriKind.RelativeOrAbsolute));
                    }
                    catch
                    {
                        img = null;
                    }
                    if (img != null)
                    {
                        img.Width = sizePx.Width;
                        img.Height = sizePx.Height;
                        visual = img;
                    }
                    else
                    {
                        var r = new System.Windows.Shapes.Rectangle
                        {
                            Width = sizePx.Width,
                            Height = sizePx.Height,
                            Fill = System.Windows.Media.Brushes.SteelBlue,
                            Stroke = System.Windows.Media.Brushes.White,
                            StrokeThickness = 1
                        };
                        visual = r;
                    }
                }
                else
                {
                    var r = new System.Windows.Shapes.Rectangle
                    {
                        Width = sizePx.Width,
                        Height = sizePx.Height,
                        Fill = System.Windows.Media.Brushes.DimGray,
                        Stroke = System.Windows.Media.Brushes.White,
                        StrokeThickness = 1
                    };
                    visual = r;
                }

                visual.Tag = el;

                // Engine places quads from (x,y) to (x+width, y+height)
                // Our WorldToCanvasPoint returns the canvas pixel for the world point (x,y).
                var topLeft = WorldToCanvasPoint(el.X, el.Y + el.Height);

                Canvas.SetLeft(visual, topLeft.X);
                Canvas.SetTop(visual, topLeft.Y);

                EditorCanvas.Children.Add(visual);

                if (ReferenceEquals(_selected, el))
                {
                    var highlight = new System.Windows.Shapes.Rectangle
                    {
                        Width = sizePx.Width,
                        Height = sizePx.Height,
                        Stroke = System.Windows.Media.Brushes.Orange,
                        StrokeThickness = 2
                    };
                    Canvas.SetLeft(highlight, topLeft.X);
                    Canvas.SetTop(highlight, topLeft.Y);
                    EditorCanvas.Children.Add(highlight);
                }
            }
        }

        // Convert canvas pixel to world coordinate at current camera/zoom/aspect.
        private System.Windows.Point CanvasToWorldPoint(System.Windows.Point canvasPt)
        {
            var canvasW = EditorCanvas.ActualWidth > 0 ? EditorCanvas.ActualWidth : EditorCanvas.Width;
            var canvasH = EditorCanvas.ActualHeight > 0 ? EditorCanvas.ActualHeight : EditorCanvas.Height;
            var aspect = GetCanvasAspect();

            var ndcX = (canvasPt.X / canvasW) * 2.0 - 1.0;
            var ndcY = (1.0 - (canvasPt.Y / canvasH)) * 2.0 - 1.0;

            var worldX = _scene.Camera.X + (ndcX * aspect) / _scene.Camera.Zoom;
            var worldY = _scene.Camera.Y + (ndcY) / _scene.Camera.Zoom;
            return new System.Windows.Point(worldX, worldY);
        }

        private void SyncCameraUiFromModel()
        {
            CameraXTextBox.Text = _scene.Camera.X.ToString(CultureInfo.InvariantCulture);
            CameraYTextBox.Text = _scene.Camera.Y.ToString(CultureInfo.InvariantCulture);
            CameraZoomTextBox.Text = _scene.Camera.Zoom.ToString(CultureInfo.InvariantCulture);

            // Keep slider aligned but avoid recursion issues if your handlers update camera.
            ZoomSlider.Value = Math.Clamp(_scene.Camera.Zoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }

        private void EditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            EditorCanvas.Focus();

            if (e.ChangedButton == MouseButton.Middle)
            {
                _isPanning = true;
                _panStartCanvasPoint = e.GetPosition(EditorCanvas);
                EditorCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                EditorCanvas_MouseLeftButtonDown(sender, e);
            }
        }

        private void EditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isPanning)
            {
                _isPanning = false;
                EditorCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                EditorCanvas_MouseLeftButtonUp(sender, e);
            }
        }

        private void EditorCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            EditorCanvas.Focus();

            // Zoom around cursor: keep the world point under the mouse stable.
            var mouseCanvas = e.GetPosition(EditorCanvas);
            var before = CanvasToWorldPoint(mouseCanvas);

            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            var newZoom = Math.Clamp(_scene.Camera.Zoom * factor, ZoomSlider.Minimum, ZoomSlider.Maximum);
            if (Math.Abs(newZoom - _scene.Camera.Zoom) < 1e-9)
                return;

            _scene.Camera.Zoom = newZoom;

            var after = CanvasToWorldPoint(mouseCanvas);

            // Move camera so that 'before' maps back under cursor.
            _scene.Camera.X += (before.X - after.X);
            _scene.Camera.Y += (before.Y - after.Y);

            SyncCameraUiFromModel();
            RefreshCanvas();
            e.Handled = true;
        }

        private void EditorCanvas_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Screen-space panning in world units: pan speed scales inversely with zoom.
            // (Faster when zoomed out, slower when zoomed in)
            const double stepPixels = 40;

            if (e.Key == Key.Space)
            {
                // reserved for future (e.g. Space+LMB pan)
                return;
            }

            var dxPx = 0.0;
            var dyPx = 0.0;

            switch (e.Key)
            {
                case Key.Left:
                case Key.A:
                    dxPx = -stepPixels;
                    break;
                case Key.Right:
                case Key.D:
                    dxPx = stepPixels;
                    break;
                case Key.Up:
                case Key.W:
                    dyPx = -stepPixels;
                    break;
                case Key.Down:
                case Key.S:
                    dyPx = stepPixels;
                    break;
                default:
                    return;
            }

            var (worldDx, worldDy) = CanvasDeltaToWorldDelta(dxPx, dyPx);

            // Panning the view: moving camera opposite to the intended screen motion
            _scene.Camera.X -= worldDx;
            _scene.Camera.Y -= worldDy;

            SyncCameraUiFromModel();
            RefreshCanvas();
            e.Handled = true;
        }

        private void EditorCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isPanning)
            {
                var point = e.GetPosition(EditorCanvas);
                var dx = point.X - _panStartCanvasPoint.X;
                var dy = point.Y - _panStartCanvasPoint.Y;
                _panStartCanvasPoint = point;

                var (worldDx, worldDy) = CanvasDeltaToWorldDelta(dx, dy);

                // Dragging the view: move camera opposite to mouse motion
                _scene.Camera.X -= worldDx;
                _scene.Camera.Y -= worldDy;

                SyncCameraUiFromModel();
                RefreshCanvas();
                return;
            }

            if (!_isDragging || _dragElement == null) return;
            var point2 = e.GetPosition(EditorCanvas);
            var dx2 = point2.X - _dragStartCanvasPoint.X;
            var dy2 = point2.Y - _dragStartCanvasPoint.Y;
            _dragStartCanvasPoint = point2;

            var (worldDx2, worldDy2) = CanvasDeltaToWorldDelta(dx2, dy2);
            _dragElement.X += worldDx2;
            _dragElement.Y += worldDy2;
            RefreshCanvas();
        }

        private void NewSceneButton_Click(object sender, RoutedEventArgs e) => NewScene();

        private void AddSpriteButton_Click(object sender, RoutedEventArgs e)
        {
            var sp = new SpriteElement
            {
                Name = "Sprite" + (_scene.Elements.Count + 1),
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1,
                ImageSrc = ""
            };
            _scene.Elements.Add(sp);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Sprite added";
        }

        private void AddAudioButton_Click(object sender, RoutedEventArgs e)
        {
            var audio = new AudioElement
            {
                Name = "Audio" + (_scene.Elements.Count + 1),
                Src = "",
                Loop = false,
                Autoplay = false
            };
            _scene.Elements.Add(audio);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Audio added";
        }

        private void AddClickableButton_Click(object sender, RoutedEventArgs e)
        {
            var click = new ClickableElement
            {
                Name = "Clickable" + (_scene.Elements.Count + 1),
                X = 0,
                Y = 0,
                Width = 0.2,
                Height = 0.2,
                HasClickableArea = true
            };
            _scene.Elements.Add(click);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Clickable added";
        }

        private void HierarchyTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var item = HierarchyTreeView.SelectedItem as TreeViewItem;
            var tag = item?.Tag;
            if (tag is BaseElement el)
            {
                SelectElement(el);
            }
        }

        private void SelectElement(BaseElement el)
        {
            _selected = el;
            PropNameTextBox.Text = el.Name;
            PropTypeTextBlock.Text = el.ElementType;
            PropXTextBox.Text = el.X.ToString(CultureInfo.InvariantCulture);
            PropYTextBox.Text = el.Y.ToString(CultureInfo.InvariantCulture);
            PropWidthTextBox.Text = el.Width.ToString(CultureInfo.InvariantCulture);
            PropHeightTextBox.Text = el.Height.ToString(CultureInfo.InvariantCulture);

            if (el is SpriteElement se)
                PropImageSrcTextBox.Text = se.ImageSrc ?? string.Empty;
            else
                PropImageSrcTextBox.Text = string.Empty;

            if (el is AudioElement ae)
            {
                PropAudioSrcTextBox.Text = ae.Src ?? string.Empty;
                PropAudioLoopCheckBox.IsChecked = ae.Loop;
                PropAudioAutoplayCheckBox.IsChecked = ae.Autoplay;
            }
            else
            {
                PropAudioSrcTextBox.Text = string.Empty;
                PropAudioLoopCheckBox.IsChecked = false;
                PropAudioAutoplayCheckBox.IsChecked = false;
            }

            if (el is ClickableElement ce)
                PropHasClickableCheckBox.IsChecked = ce.HasClickableArea;
            else
                PropHasClickableCheckBox.IsChecked = false;

            RefreshCanvas();
        }

        private void ApplyChangesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            _selected.Name = PropNameTextBox.Text;
            _selected.X = ParseDoubleSafe(PropXTextBox.Text);
            _selected.Y = ParseDoubleSafe(PropYTextBox.Text);

            // Fix: Only assign Width/Height if _selected is not null and has those properties
            if (_selected is BaseElement baseEl)
            {
                baseEl.Width = ParseDoubleSafe(PropWidthTextBox.Text);
                baseEl.Height = ParseDoubleSafe(PropHeightTextBox.Text);
            }

            if (_selected is SpriteElement se)
            {
                se.ImageSrc = PropImageSrcTextBox.Text;
            }
            if (_selected is AudioElement ae)
            {
                ae.Src = PropAudioSrcTextBox.Text;
                ae.Loop = PropAudioLoopCheckBox.IsChecked == true;
                ae.Autoplay = PropAudioAutoplayCheckBox.IsChecked == true;
            }
            if (_selected is ClickableElement ce)
            {
                ce.HasClickableArea = PropHasClickableCheckBox.IsChecked == true;
            }

            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Changes applied";
        }

        private void RemoveElementButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            _scene.Elements.Remove(_selected);
            _selected = null;
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Element removed";
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            _scene.Name = SceneNameTextBox.Text;
            _scene.Camera.X = ParseDoubleSafe(CameraXTextBox.Text);
            _scene.Camera.Y = ParseDoubleSafe(CameraYTextBox.Text);
            _scene.Camera.Zoom = ParseDoubleSafe(CameraZoomTextBox.Text);

            var xaml = SceneSerializer.Serialize(_scene);
            System.Windows.Clipboard.SetText(xaml);
            StatusTextBlock.Text = "Scene XAML copied to clipboard";
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _scene.Camera.Zoom = e.NewValue;
            _zoom = e.NewValue;
            CameraZoomTextBox.Text = _scene.Camera.Zoom.ToString(CultureInfo.InvariantCulture);
            RefreshCanvas();
        }

        private void CenterCameraButton_Click(object sender, RoutedEventArgs e)
        {
            _scene.Camera.X = 0;
            _scene.Camera.Y = 0;
            SyncCameraUiFromModel();
            RefreshCanvas();
        }

        private void OpenProjectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select engine project folder (contains game.js, index.html, scene.xml, style.css)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            System.Windows.Forms.Application.EnableVisualStyles();

            var result = dlg.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                _projectFolderPath = dlg.SelectedPath;
                StatusTextBlock.Text = $"Project folder: {_projectFolderPath}";
                RefreshProjectTree();
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _projectFolderPath,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = $"Failed to open folder: {ex.Message}";
                }
            }
        }

        private void NewScene()
        {
            _loadedSceneFilePath = null;
            _scene = new SceneModel
            {
                Name = "NewScene",
                Camera = new CameraModel { X = 0, Y = 0, Zoom = 1 }
            };
            SceneNameTextBox.Text = _scene.Name;
            CameraXTextBox.Text = _scene.Camera.X.ToString(CultureInfo.InvariantCulture);
            CameraYTextBox.Text = _scene.Camera.Y.ToString(CultureInfo.InvariantCulture);
            CameraZoomTextBox.Text = _scene.Camera.Zoom.ToString(CultureInfo.InvariantCulture);

            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "New scene created";
        }

        private void RefreshHierarchy()
        {
            HierarchyTreeView.Items.Clear();
            var sceneNode = new TreeViewItem { Header = $"Scene: {_scene.Name}", Tag = _scene };
            var cameraNode = new TreeViewItem { Header = $"Camera: MainCamera", Tag = _scene.Camera };
            sceneNode.Items.Add(cameraNode);
            var elementsRoot = new TreeViewItem { Header = "Elements", IsExpanded = true };
            foreach (var el in _scene.Elements)
            {
                var node = new TreeViewItem { Header = el.DisplayName, Tag = el };
                elementsRoot.Items.Add(node);
            }
            sceneNode.Items.Add(elementsRoot);
            HierarchyTreeView.Items.Add(sceneNode);
            sceneNode.IsExpanded = true;
        }

        private static double ParseDoubleSafe(string? s)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            return 0;
        }

        private void EditorCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var point = e.GetPosition(EditorCanvas);
            foreach (FrameworkElement child in EditorCanvas.Children)
            {
                var el = child.Tag as BaseElement;
                if (el == null) continue;
                var left = Canvas.GetLeft(child);
                var top = Canvas.GetTop(child);
                var rect = new System.Windows.Rect(left, top, child.ActualWidth, child.ActualHeight);
                if (rect.Contains(point))
                {
                    SelectElement(el);
                    _isDragging = true;
                    _dragElement = el;
                    _dragStartCanvasPoint = point;
                    EditorCanvas.CaptureMouse();
                    break;
                }
            }
        }

        private void EditorCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                _dragElement = null;
                EditorCanvas.ReleaseMouseCapture();
                RefreshHierarchy();
                RefreshCanvas();
            }
        }
    }

    // Models remain unchanged...
}