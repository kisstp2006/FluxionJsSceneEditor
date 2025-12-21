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

        private string? _engineFolderPath;
        private EngineInfo? _engineInfo;

        private sealed record EngineInfo(string EngineName, string Version, string Codename, string License, string VersionFilePath);

        private const string ExpectedEngineName = "Fluxion Web Engine";
        private const string ExpectedVersion = "1.0.0";
        private const string ExpectedCodename = "Fluxion-Js";
        private const string ExpectedLicense = "MIT/Apache-2.0";

        private static readonly string EngineFolderPathConfigFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluxionJsSceneEditor",
            "engineFolderPath.txt");

        private static string? TryLoadEngineFolderPath()
        {
            try
            {
                if (!File.Exists(EngineFolderPathConfigFile))
                    return null;

                var text = File.ReadAllText(EngineFolderPathConfigFile).Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                return null;
            }
        }

        private static void TrySaveEngineFolderPath(string? path)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(EngineFolderPathConfigFile);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(EngineFolderPathConfigFile, path ?? string.Empty);
            }
            catch
            {
            }
        }

        private static bool TryParsePyStringAssignment(string line, string expectedKey, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var trimmed = line.Trim();
            if (!trimmed.StartsWith(expectedKey + " ", StringComparison.Ordinal) &&
                !trimmed.StartsWith(expectedKey + "=", StringComparison.Ordinal))
                return false;

            var eq = trimmed.IndexOf('=');
            if (eq < 0)
                return false;

            var rhs = trimmed[(eq + 1)..].Trim();
            if (rhs.Length < 2)
                return false;

            var quote = rhs[0];
            if (quote != '"' && quote != '\'')
                return false;

            var end = rhs.IndexOf(quote, 1);
            if (end < 0)
                return false;

            value = rhs.Substring(1, end - 1);
            return true;
        }

        private static EngineInfo ValidateAndLoadEngineInfo(string engineFolderPath)
        {
            if (string.IsNullOrWhiteSpace(engineFolderPath) || !Directory.Exists(engineFolderPath))
                throw new InvalidOperationException("Engine folder does not exist.");

            var versionFile = System.IO.Path.Combine(engineFolderPath, "version.py");
            if (!File.Exists(versionFile))
                throw new InvalidOperationException("Engine folder is invalid: missing version.py.");

            var lines = File.ReadAllLines(versionFile);

            string? engineName = null;
            string? version = null;
            string? codename = null;
            string? license = null;

            foreach (var line in lines)
            {
                if (engineName == null && TryParsePyStringAssignment(line, "ENGINE_NAME", out var v1))
                    engineName = v1;
                else if (version == null && TryParsePyStringAssignment(line, "VERSION", out var v2))
                    version = v2;
                else if (codename == null && TryParsePyStringAssignment(line, "CODENAME", out var v3))
                    codename = v3;
                else if (license == null && TryParsePyStringAssignment(line, "LICENSE", out var v4))
                    license = v4;
            }

            if (!string.Equals(engineName, ExpectedEngineName, StringComparison.Ordinal) ||
                !string.Equals(version, ExpectedVersion, StringComparison.Ordinal) ||
                !string.Equals(codename, ExpectedCodename, StringComparison.Ordinal) ||
                !string.Equals(license, ExpectedLicense, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Engine folder is invalid: version.py does not match expected Fluxion engine metadata.");
            }

            return new EngineInfo(engineName!, version!, codename!, license!, versionFile);
        }

        private void ApplyEngineSelection(string? engineFolderPath)
        {
            if (string.IsNullOrWhiteSpace(engineFolderPath))
                throw new InvalidOperationException("No engine folder selected.");

            var info = ValidateAndLoadEngineInfo(engineFolderPath);

            _engineFolderPath = engineFolderPath;
            _engineInfo = info;

            TrySaveEngineFolderPath(_engineFolderPath);
            EngineFolderPathTextBlock.Text = _engineFolderPath;
        }

        private void ShowEngineInfo()
        {
            if (_engineInfo == null)
            {
                System.Windows.MessageBox.Show(this, "No engine selected.", "Engine Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var msg =
                $"ENGINE_NAME = \"{_engineInfo.EngineName}\"\n" +
                $"VERSION = \"{_engineInfo.Version}\"\n" +
                $"CODENAME = \"{_engineInfo.Codename}\"\n" +
                $"LICENSE = \"{_engineInfo.License}\"\n\n" +
                $"File: {_engineInfo.VersionFilePath}";

            System.Windows.MessageBox.Show(this, msg, "Engine Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private const double DefaultTargetWidth = 1920;
        private const double DefaultTargetHeight = 1080;

        private bool _showGrid = true;
        private double _gridSpacingPx = 64;

        // Editor view camera (used for pan/zoom in the editor)
        private double _viewCamX;
        private double _viewCamY;
        private double _viewCamZoom = 1;

        private enum GizmoHit { None, MoveXY, MoveX, MoveY }
        private GizmoHit _gizmoHit = GizmoHit.None;
        private bool _isGizmoDragging;
        private System.Windows.Point _gizmoDragStart;
        private double _gizmoStartX;
        private double _gizmoStartY;

        private bool _isStartingEngine;

        private static async System.Threading.Tasks.Task<int> RunProcessAsync(string fileName, string arguments, string workingDirectory, Action<string> onOutput)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    onOutput(e.Data);
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    onOutput(e.Data);
            };

            if (!proc.Start())
                throw new InvalidOperationException($"Failed to start process: {fileName}");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode;
        }

        private bool _showShortcuts = true;
        private string? _clipboardElementXml;

        private void ApplySceneHeaderFromUi()
        {
            _scene.Name = SceneNameTextBox.Text;
            _scene.Camera.X = ParseDoubleSafe(CameraXTextBox.Text);
            _scene.Camera.Y = ParseDoubleSafe(CameraYTextBox.Text);
            _scene.Camera.Zoom = ParseDoubleSafe(CameraZoomTextBox.Text);
        }

        private void ToggleShortcuts()
        {
            _showShortcuts = !_showShortcuts;
            if (ShortcutsPanel != null)
                ShortcutsPanel.Visibility = _showShortcuts ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CopySelectedElement()
        {
            if (_selected == null)
                return;

            // Serialize a single element using the existing scene serializer format.
            var temp = new SceneModel { Name = "Clipboard" };
            temp.Camera = new CameraModel { X = 0, Y = 0, Zoom = 1 };
            temp.Elements.Add(_selected);

            _clipboardElementXml = SceneSerializer.Serialize(temp);
            System.Windows.Clipboard.SetText(_clipboardElementXml);
            StatusTextBlock.Text = $"Copied {_selected.ElementType}";
        }

        private void PasteElement()
        {
            var xml = _clipboardElementXml;
            if (string.IsNullOrWhiteSpace(xml))
            {
                try
                {
                    xml = System.Windows.Clipboard.GetText();
                }
                catch
                {
                    xml = null;
                }
            }

            if (string.IsNullOrWhiteSpace(xml))
                return;

            SceneModel parsed;
            try
            {
                parsed = SceneSerializer.Deserialize(xml);
            }
            catch
            {
                return;
            }

            if (parsed.Elements.Count == 0)
                return;

            var el = parsed.Elements[0];

            // Offset pasted element slightly so it's visible.
            el.Name = el.Name + "_copy";
            el.X += 20;
            el.Y += 20;

            _scene.Elements.Add(el);
            SelectElement(el);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = $"Pasted {el.ElementType}";
        }

        private void RemoveSelectedElement()
        {
            if (_selected == null)
                return;

            _scene.Elements.Remove(_selected);
            _selected = null;
            SetPropertiesPanelVisibility(null);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Element removed";
        }

        private void OpenSceneDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Scene files (*.xml;*.xaml)|*.xml;*.xaml|All files (*.*)|*.*",
                Title = "Open Scene"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    LoadSceneFromFile(dlg.FileName);
                    StatusTextBlock.Text = $"Loaded scene: {System.IO.Path.GetFileName(dlg.FileName)}";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(this, ex.Message, "Open Scene Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveSceneDialog()
        {
            // If a scene was opened from disk, treat Ctrl+S / Save as "save back to the same file".
            if (!string.IsNullOrWhiteSpace(_loadedSceneFilePath))
            {
                try
                {
                    ApplySceneHeaderFromUi();
                    var xml = SceneSerializer.Serialize(_scene);
                    File.WriteAllText(_loadedSceneFilePath, xml);
                    StatusTextBlock.Text = $"Saved scene: {System.IO.Path.GetFileName(_loadedSceneFilePath)}";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(this, ex.Message, "Save Scene Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return;
            }

            // New scene: ask where to save.
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Scene files (*.xml;*.xaml)|*.xml;*.xaml|All files (*.*)|*.*",
                Title = "Save Scene",
                FileName = "scene.xml"
            };

            if (dlg.ShowDialog(this) == true)
            {
                ApplySceneHeaderFromUi();
                var xml = SceneSerializer.Serialize(_scene);
                File.WriteAllText(dlg.FileName, xml);
                _loadedSceneFilePath = dlg.FileName;
                StatusTextBlock.Text = $"Saved scene: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.N:
                        NewScene();
                        e.Handled = true;
                        return;
                    case Key.O:
                        OpenSceneDialog();
                        e.Handled = true;
                        return;
                    case Key.S:
                        SaveSceneDialog();
                        e.Handled = true;
                        return;
                    case Key.C:
                        CopySelectedElement();
                        e.Handled = true;
                        return;
                    case Key.V:
                        PasteElement();
                        e.Handled = true;
                        return;
                    case Key.Oem2: // '/'
                        ToggleShortcuts();
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.Delete)
            {
                RemoveSelectedElement();
                e.Handled = true;
                return;
            }
        }

        private void ApplyTheme(bool isDark)
        {
            // Update app-scoped theme brushes so existing DynamicResource bindings refresh automatically.
            var r = System.Windows.Application.Current?.Resources;
            if (r == null)
                return;

            static System.Windows.Media.SolidColorBrush B(byte r, byte g, byte b)
                => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

            if (isDark)
            {
                r["App.BackgroundBrush"] = B(0x1E, 0x1E, 0x1E);
                r["App.PanelBrush"] = B(0x2B, 0x2B, 0x2B);
                r["App.PanelAltBrush"] = B(0x2A, 0x2A, 0x2A);
                r["App.SurfaceBrush"] = B(0x33, 0x33, 0x33);
                r["App.BorderBrush"] = B(0x44, 0x44, 0x44);
                r["App.TextBrush"] = B(0xFF, 0xFF, 0xFF);
                r["App.SubtleTextBrush"] = B(0xDD, 0xDD, 0xDD);
                r["App.MutedTextBrush"] = B(0xBB, 0xBB, 0xBB);
                r["App.AccentBrush"] = B(0x4F, 0xC3, 0xF7);
                r["App.GridSplitterBrush"] = B(0x44, 0x44, 0x44);

                r["App.ControlBackgroundBrush"] = B(0x2F, 0x2F, 0x2F);
                r["App.ControlForegroundBrush"] = B(0xFF, 0xFF, 0xFF);
                r["App.ControlBorderBrush"] = B(0x55, 0x55, 0x55);
                r["App.ControlDisabledBrush"] = B(0x77, 0x77, 0x77);
                r["App.SelectionBrush"] = B(0x4F, 0xC3, 0xF7);
                r["App.SelectionTextBrush"] = B(0x00, 0x00, 0x00);
            }
            else
            {
                r["App.BackgroundBrush"] = B(0xF4, 0xF4, 0xF6);
                r["App.PanelBrush"] = B(0xFF, 0xFF, 0xFF);
                r["App.PanelAltBrush"] = B(0xF0, 0xF0, 0xF2);
                r["App.SurfaceBrush"] = B(0xFF, 0xFF, 0xFF);
                r["App.BorderBrush"] = B(0xD0, 0xD0, 0xD4);
                r["App.TextBrush"] = B(0x1E, 0x1E, 0x1E);
                r["App.SubtleTextBrush"] = B(0x33, 0x33, 0x33);
                r["App.MutedTextBrush"] = B(0x55, 0x55, 0x55);
                r["App.AccentBrush"] = B(0x00, 0x78, 0xD4);
                r["App.GridSplitterBrush"] = B(0xD0, 0xD0, 0xD4);

                r["App.ControlBackgroundBrush"] = B(0xFF, 0xFF, 0xFF);
                r["App.ControlForegroundBrush"] = B(0x1E, 0x1E, 0x1E);
                r["App.ControlBorderBrush"] = B(0xC8, 0xC8, 0xCC);
                r["App.ControlDisabledBrush"] = B(0xA0, 0xA0, 0xA0);
                r["App.SelectionBrush"] = B(0x00, 0x78, 0xD4);
                r["App.SelectionTextBrush"] = B(0xFF, 0xFF, 0xFF);
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeComboBox?.SelectedItem is ComboBoxItem cbi)
            {
                var theme = cbi.Content?.ToString();
                ApplyTheme(string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase));
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            KeyDown += Window_KeyDown;

            Loaded += (_, __) =>
            {
                ZoomSlider.ValueChanged += ZoomSlider_ValueChanged;
                StatusTextBlock.Text = "Ready";

                if (ShortcutsPanel != null)
                    ShortcutsPanel.Visibility = _showShortcuts ? Visibility.Visible : Visibility.Collapsed;

                // Apply initial theme based on UI (defaults to Dark).
                if (ThemeComboBox?.SelectedItem is ComboBoxItem cbi)
                    ApplyTheme(string.Equals(cbi.Content?.ToString(), "Dark", StringComparison.OrdinalIgnoreCase));

                // Restore engine folder (optional)
                var stored = TryLoadEngineFolderPath();
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    try
                    {
                        ApplyEngineSelection(stored);
                    }
                    catch
                    {
                        _engineFolderPath = null;
                        _engineInfo = null;
                        EngineFolderPathTextBlock.Text = "(none)";
                    }
                }
                else
                {
                    EngineFolderPathTextBlock.Text = "(none)";
                }

                NewScene(); // now ActualWidth/ActualHeight are valid
            };
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

        // Pixel-based world (top-left origin):
        // screenPx = viewportOrigin + ((worldPx - cameraPx) * cameraZoom) * viewportScale
        private System.Windows.Point WorldToCanvasPoint(double worldX, double worldY)
        {
            var (vx, vy, _, _, scale) = GetViewport();

            var localX = (worldX - _viewCamX) * _viewCamZoom;
            var localY = (worldY - _viewCamY) * _viewCamZoom;

            return new System.Windows.Point(vx + localX * scale, vy + localY * scale);
        }

        private System.Windows.Size WorldToCanvasSize(double worldW, double worldH)
        {
            var (_, _, _, _, scale) = GetViewport();
            var pxW = (worldW * _viewCamZoom) * scale;
            var pxH = (worldH * _viewCamZoom) * scale;
            return new System.Windows.Size(pxW, pxH);
        }

        private System.Windows.Point CanvasToWorldPoint(System.Windows.Point canvasPt)
        {
            var (vx, vy, _, _, scale) = GetViewport();

            var localX = (canvasPt.X - vx) / scale;
            var localY = (canvasPt.Y - vy) / scale;

            var worldX = _viewCamX + localX / _viewCamZoom;
            var worldY = _viewCamY + localY / _viewCamZoom;

            return new System.Windows.Point(worldX, worldY);
        }

        private (double worldDx, double worldDy) CanvasDeltaToWorldDelta(double dxPx, double dyPx)
        {
            var (_, _, _, _, scale) = GetViewport();
            var worldDx = (dxPx / scale) / _viewCamZoom;
            var worldDy = (dyPx / scale) / _viewCamZoom;
            return (worldDx, worldDy);
        }

        private static System.Windows.Media.Brush ParseColorBrush(string? color)
        {
            if (string.IsNullOrWhiteSpace(color))
                return System.Windows.Media.Brushes.White;

            try
            {
                var obj = System.Windows.Media.ColorConverter.ConvertFromString(color);
                if (obj is System.Windows.Media.Color c)
                    return new System.Windows.Media.SolidColorBrush(c);
            }
            catch
            {
            }

            return System.Windows.Media.Brushes.White;
        }

        private void ShowGridCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _showGrid = ShowGridCheckBox.IsChecked == true;
            RefreshOverlays();
        }

        private void GridSpacingTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var v = ParseDoubleSafe(GridSpacingTextBox.Text);
            if (v > 2)
                _gridSpacingPx = v;
            GridSpacingTextBox.Text = _gridSpacingPx.ToString(CultureInfo.InvariantCulture);
            RefreshOverlays();
        }

        private (double x, double y, double w, double h, double scale) GetViewportFor(double canvasW, double canvasH)
        {
            var (targetW, targetH) = GetTargetResolution();
            var scale = Math.Min(canvasW / targetW, canvasH / targetH);
            if (scale <= 0) scale = 1;
            var viewW = targetW * scale;
            var viewH = targetH * scale;
            var viewX = (canvasW - viewW) / 2.0;
            var viewY = (canvasH - viewH) / 2.0;
            return (viewX, viewY, viewW, viewH, scale);
        }

        private (double x, double y, double w, double h, double scale) GetSceneViewport()
        {
            return GetViewportFor(GizmoCanvas.ActualWidth, GizmoCanvas.ActualHeight);
        }

        private (double x, double y, double w, double h, double scale) GetViewport()
        {
            var (targetW, targetH) = GetTargetResolution();

            var canvasW = GizmoCanvas.ActualWidth;
            var canvasH = GizmoCanvas.ActualHeight;

            var scale = Math.Min(canvasW / targetW, canvasH / targetH);
            if (!double.IsFinite(scale) || scale <= 0)
                scale = 1;

            var viewW = targetW * scale;
            var viewH = targetH * scale;
            var viewX = (canvasW - viewW) / 2.0;
            var viewY = (canvasH - viewH) / 2.0;
            return (viewX, viewY, viewW, viewH, scale);
        }

        private System.Windows.Point WorldToScenePoint(double worldX, double worldY)
        {
            var (vx, vy, _, _, scale) = GetSceneViewport();

            var localX = (worldX - _viewCamX) * _viewCamZoom;
            var localY = (worldY - _viewCamY) * _viewCamZoom;

            return new System.Windows.Point(vx + localX * scale, vy + localY * scale);
        }

        private System.Windows.Size WorldToSceneSize(double worldW, double worldH)
        {
            var (_, _, _, _, scale) = GetSceneViewport();
            return new System.Windows.Size((worldW * _viewCamZoom) * scale, (worldH * _viewCamZoom) * scale);
        }

        private System.Windows.Point SceneToWorldPoint(System.Windows.Point scenePt)
        {
            var (vx, vy, _, _, scale) = GetSceneViewport();

            var localX = (scenePt.X - vx) / scale;
            var localY = (scenePt.Y - vy) / scale;

            var worldX = _viewCamX + localX / _viewCamZoom;
            var worldY = _viewCamY + localY / _viewCamZoom;

            return new System.Windows.Point(worldX, worldY);
        }

        private (double worldDx, double worldDy) SceneDeltaToWorldDelta(double dxPx, double dyPx)
        {
            var (_, _, _, _, scale) = GetSceneViewport();
            var worldDx = (dxPx / scale) / _viewCamZoom;
            var worldDy = (dyPx / scale) / _viewCamZoom;
            return (worldDx, worldDy);
        }

        private void RefreshOverlays()
        {
            DrawGrid();
            DrawRulers();
            DrawGizmo();
        }

        private void DrawGrid()
        {
            if (GridCanvas == null)
                return;

            GridCanvas.Children.Clear();
            if (!_showGrid)
                return;

            var canvasW = GizmoCanvas.ActualWidth;
            var canvasH = GizmoCanvas.ActualHeight;

            if (!double.IsFinite(canvasW) || !double.IsFinite(canvasH) || canvasW <= 0 || canvasH <= 0)
                return;

            // IMPORTANT: do not force canvas Width/Height, let layout stretch them.
            GridCanvas.ClearValue(FrameworkElement.WidthProperty);
            GridCanvas.ClearValue(FrameworkElement.HeightProperty);
            ContentCanvas.ClearValue(FrameworkElement.WidthProperty);
            ContentCanvas.ClearValue(FrameworkElement.HeightProperty);
            GizmoCanvas.ClearValue(FrameworkElement.WidthProperty);
            GizmoCanvas.ClearValue(FrameworkElement.HeightProperty);

            var (vx, vy, vw, vh, _) = GetSceneViewport();

            var topLeftWorld = SceneToWorldPoint(new System.Windows.Point(0, 0));
            var bottomRightWorld = SceneToWorldPoint(new System.Windows.Point(canvasW, canvasH));

            var left = Math.Floor(Math.Min(topLeftWorld.X, bottomRightWorld.X) / _gridSpacingPx) * _gridSpacingPx;
            var right = Math.Ceiling(Math.Max(topLeftWorld.X, bottomRightWorld.X) / _gridSpacingPx) * _gridSpacingPx;
            var top = Math.Floor(Math.Min(topLeftWorld.Y, bottomRightWorld.Y) / _gridSpacingPx) * _gridSpacingPx;
            var bottom = Math.Ceiling(Math.Max(topLeftWorld.Y, bottomRightWorld.Y) / _gridSpacingPx) * _gridSpacingPx;

            var minorStroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));
            var majorStroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 255, 255, 255));

            for (var x = left; x <= right; x += _gridSpacingPx)
            {
                var p1 = WorldToScenePoint(x, top);
                var p2 = WorldToScenePoint(x, bottom);
                var isMajor = ((int)(x / _gridSpacingPx) % 10) == 0;

                GridCanvas.Children.Add(new Line
                {
                    X1 = p1.X,
                    Y1 = p1.Y,
                    X2 = p2.X,
                    Y2 = p2.Y,
                    Stroke = isMajor ? majorStroke : minorStroke,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                });
            }

            for (var y = top; y <= bottom; y += _gridSpacingPx)
            {
                var p1 = WorldToScenePoint(left, y);
                var p2 = WorldToScenePoint(right, y);
                var isMajor = ((int)(y / _gridSpacingPx) % 10) == 0;

                GridCanvas.Children.Add(new Line
                {
                    X1 = p1.X,
                    Y1 = p1.Y,
                    X2 = p2.X,
                    Y2 = p2.Y,
                    Stroke = isMajor ? majorStroke : minorStroke,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                });
            }

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = vw,
                Height = vh,
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 0, 0, 0)),
                StrokeThickness = 1,
                Fill = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rect, vx);
            Canvas.SetTop(rect, vy);
            GridCanvas.Children.Add(rect);
        }

        private void DrawRulers()
        {
            if (RulerTopCanvas == null || RulerLeftCanvas == null)
                return;

            RulerTopCanvas.Children.Clear();
            RulerLeftCanvas.Children.Clear();

            var canvasW = GizmoCanvas.ActualWidth > 0 ? GizmoCanvas.ActualWidth : GizmoCanvas.Width;
            var canvasH = GizmoCanvas.ActualHeight > 0 ? GizmoCanvas.ActualHeight : GizmoCanvas.Height;

            if (!double.IsFinite(canvasW) || !double.IsFinite(canvasH) || canvasW <= 0 || canvasH <= 0)
                return;

            var (_, _, _, _, scale) = GetSceneViewport();

            // Use full visible canvas for ruler extents
            var topLeftWorld = SceneToWorldPoint(new System.Windows.Point(0, 0));
            var bottomRightWorld = SceneToWorldPoint(new System.Windows.Point(canvasW, canvasH));

            var tickWorld = _gridSpacingPx;
            var tickPx = tickWorld * _viewCamZoom * scale;
            if (tickPx < 25) tickWorld *= 2;
            if (tickPx > 150) tickWorld /= 2;

            var xStart = Math.Floor(Math.Min(topLeftWorld.X, bottomRightWorld.X) / tickWorld) * tickWorld;
            var xEnd = Math.Ceiling(Math.Max(topLeftWorld.X, bottomRightWorld.X) / tickWorld) * tickWorld;
            var yStart = Math.Floor(Math.Min(topLeftWorld.Y, bottomRightWorld.Y) / tickWorld) * tickWorld;
            var yEnd = Math.Ceiling(Math.Max(topLeftWorld.Y, bottomRightWorld.Y) / tickWorld) * tickWorld;

            for (var x = xStart; x <= xEnd; x += tickWorld)
            {
                var p = WorldToScenePoint(x, topLeftWorld.Y);
                var lx = p.X;

                var line = new Line { X1 = lx, Y1 = 24, X2 = lx, Y2 = 16, Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 1, Opacity = 0.6, IsHitTestVisible = false };
                RulerTopCanvas.Children.Add(line);

                var label = new TextBlock { Text = ((int)x).ToString(CultureInfo.InvariantCulture), Foreground = System.Windows.Media.Brushes.White, FontSize = 10, Opacity = 0.7, IsHitTestVisible = false };
                Canvas.SetLeft(label, lx + 2);
                Canvas.SetTop(label, 2);
                RulerTopCanvas.Children.Add(label);
            }

            for (var y = yStart; y <= yEnd; y += tickWorld)
            {
                var p = WorldToScenePoint(topLeftWorld.X, y);
                var ly = p.Y;

                var line = new Line { X1 = 24, Y1 = ly, X2 = 16, Y2 = ly, Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 1, Opacity = 0.6, IsHitTestVisible = false };
                RulerLeftCanvas.Children.Add(line);

                var label = new TextBlock { Text = ((int)y).ToString(CultureInfo.InvariantCulture), Foreground = System.Windows.Media.Brushes.White, FontSize = 10, Opacity = 0.7, IsHitTestVisible = false };
                Canvas.SetLeft(label, 2);
                Canvas.SetTop(label, ly + 2);
                RulerLeftCanvas.Children.Add(label);
            }
        }

        // Placeholder for gizmo drawing; implemented in later steps.
        private void DrawGizmo()
        {
            if (GizmoCanvas == null) return;
            GizmoCanvas.Children.Clear();

            // Origin crosshair (world 0,0)
            var origin = WorldToScenePoint(0, 0);
            GizmoCanvas.Children.Add(new Line { X1 = origin.X - 8, Y1 = origin.Y, X2 = origin.X + 8, Y2 = origin.Y, Stroke = System.Windows.Media.Brushes.LimeGreen, StrokeThickness = 1, Opacity = 0.8, IsHitTestVisible = false });
            GizmoCanvas.Children.Add(new Line { X1 = origin.X, Y1 = origin.Y - 8, X2 = origin.X, Y2 = origin.Y + 8, Stroke = System.Windows.Media.Brushes.LimeGreen, StrokeThickness = 1, Opacity = 0.8, IsHitTestVisible = false });

            // Camera view rectangle (what the SCENE camera sees)
            var camViewportW = _scene.Camera.Width > 0 ? _scene.Camera.Width : GetTargetResolution().targetW;
            var camViewportH = _scene.Camera.Height > 0 ? _scene.Camera.Height : GetTargetResolution().targetH;

            var sceneCamWorldW = camViewportW / _scene.Camera.Zoom;
            var sceneCamWorldH = camViewportH / _scene.Camera.Zoom;
            var sceneCamWorldLeft = _scene.Camera.X;
            var sceneCamWorldTop = _scene.Camera.Y;

            var camRectTl = WorldToScenePoint(sceneCamWorldLeft, sceneCamWorldTop);
            var camRectSize = WorldToSceneSize(sceneCamWorldW, sceneCamWorldH);

            var camRect = new System.Windows.Shapes.Rectangle
            {
                Width = camRectSize.Width,
                Height = camRectSize.Height,
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 180, 180, 255)),
                StrokeThickness = 1.5,
                Fill = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(camRect, camRectTl.X);
            Canvas.SetTop(camRect, camRectTl.Y);
            GizmoCanvas.Children.Add(camRect);

            // Camera indicator crosshair (scene camera top-left)
            var cam = WorldToScenePoint(_scene.Camera.X, _scene.Camera.Y);

            GizmoCanvas.Children.Add(new Line { X1 = cam.X - 10, Y1 = cam.Y, X2 = cam.X + 10, Y2 = cam.Y, Stroke = System.Windows.Media.Brushes.Yellow, StrokeThickness = 2, Opacity = 0.85, IsHitTestVisible = false });
            GizmoCanvas.Children.Add(new Line { X1 = cam.X, Y1 = cam.Y - 10, X2 = cam.X, Y2 = cam.Y + 10, Stroke = System.Windows.Media.Brushes.Yellow, StrokeThickness = 2, Opacity = 0.85, IsHitTestVisible = false });

            var camLabel = new TextBlock
            {
                Text = "CAM",
                Foreground = System.Windows.Media.Brushes.Yellow,
                FontSize = 10,
                Opacity = 0.9,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(camLabel, cam.X + 12);
            Canvas.SetTop(camLabel, cam.Y - 12);
            GizmoCanvas.Children.Add(camLabel);

            if (_selected == null)
                return;

            // Selection rect (if element has size)
            if (_selected is not TextElement && _selected is not AudioElement)
            {
                var size = WorldToSceneSize(_selected.Width, _selected.Height);
                var tl = WorldToScenePoint(_selected.X, _selected.Y);
                var sel = new System.Windows.Shapes.Rectangle
                {
                    Width = size.Width,
                    Height = size.Height,
                    Stroke = System.Windows.Media.Brushes.Orange,
                    StrokeThickness = 1.5,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(sel, tl.X);
                Canvas.SetTop(sel, tl.Y);
                GizmoCanvas.Children.Add(sel);
            }

            // Move gizmo at element position (top-left for now)
            var p = WorldToScenePoint(_selected.X, _selected.Y);
            const double axisLen = 40;

            var xAxis = new Line { X1 = p.X, Y1 = p.Y, X2 = p.X + axisLen, Y2 = p.Y, Stroke = System.Windows.Media.Brushes.Red, StrokeThickness = 2, Opacity = 0.9, Tag = GizmoHit.MoveX };
            var yAxis = new Line { X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y + axisLen, Stroke = System.Windows.Media.Brushes.DodgerBlue, StrokeThickness = 2, Opacity = 0.9, Tag = GizmoHit.MoveY };
            var center = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Fill = System.Windows.Media.Brushes.White, Opacity = 0.9, Tag = GizmoHit.MoveXY };
            Canvas.SetLeft(center, p.X - 5);
            Canvas.SetTop(center, p.Y - 5);

            GizmoCanvas.Children.Add(xAxis);
            GizmoCanvas.Children.Add(yAxis);
            GizmoCanvas.Children.Add(center);
        }

        private GizmoHit HitTestGizmo(System.Windows.Point p)
        {
            if (_selected == null) return GizmoHit.None;

            // brute hit test against tagged gizmo visuals
            foreach (var child in GizmoCanvas.Children)
            {
                if (child is FrameworkElement fe && fe.Tag is GizmoHit hit)
                {
                    var left = Canvas.GetLeft(fe);
                    var top = Canvas.GetTop(fe);

                    Rect bounds;
                    if (fe is Line ln)
                    {
                        // simple axis bounds
                        bounds = new Rect(Math.Min(ln.X1, ln.X2) - 4, Math.Min(ln.Y1, ln.Y2) - 4, Math.Abs(ln.X2 - ln.X1) + 8, Math.Abs(ln.Y2 - ln.Y1) + 8);
                    }
                    else
                    {
                        bounds = new Rect(left, top, fe.ActualWidth <= 0 ? fe.Width : fe.ActualWidth, fe.ActualHeight <= 0 ? fe.Height : fe.ActualHeight);
                    }

                    if (bounds.Contains(p))
                        return hit;
                }
            }

            return GizmoHit.None;
        }

        private void BeginGizmoDrag(GizmoHit hit, System.Windows.Point start)
        {
            _gizmoHit = hit;
            _isGizmoDragging = true;
            _gizmoDragStart = start;
            _gizmoStartX = _selected?.X ?? 0;
            _gizmoStartY = _selected?.Y ?? 0;
        }

        private void UpdateGizmoDrag(System.Windows.Point current)
        {
            if (!_isGizmoDragging || _selected == null) return;

            var dx = current.X - _gizmoDragStart.X;
            var dy = current.Y - _gizmoDragStart.Y;
            var (wx, wy) = SceneDeltaToWorldDelta(dx, dy);

            switch (_gizmoHit)
            {
                case GizmoHit.MoveX:
                    _selected.X = _gizmoStartX + wx;
                    _selected.Y = _gizmoStartY;
                    break;
                case GizmoHit.MoveY:
                    _selected.X = _gizmoStartX;
                    _selected.Y = _gizmoStartY + wy;
                    break;
                case GizmoHit.MoveXY:
                    _selected.X = _gizmoStartX + wx;
                    _selected.Y = _gizmoStartY + wy;
                    break;
            }

            RefreshCanvas();
            DrawGizmo();
        }

        private void EndGizmoDrag()
        {
            _isGizmoDragging = false;
            _gizmoHit = GizmoHit.None;
        }

        // ---- Canvas rendering and interaction ----

        private void RefreshCanvas()
        {
            if (ContentCanvas == null || _scene == null) return;

            ContentCanvas.Children.Clear();

            foreach (var el in _scene.Elements)
            {
                if (el is AudioElement)
                    continue; // not visual

                FrameworkElement visual;

                if (el is TextElement te)
                {
                    var (_, _, _, _, scale) = GetSceneViewport();
                    var baseFontSize = te.FontSize > 0 ? te.FontSize : 16;
                    var renderFontSize = baseFontSize / (_viewCamZoom * scale);
                    if (!double.IsFinite(renderFontSize) || renderFontSize <= 0)
                        renderFontSize = baseFontSize;

                    var tb = new TextBlock
                    {
                        Text = te.Text ?? string.Empty,
                        FontSize = renderFontSize,
                        Foreground = ParseColorBrush(te.Color),
                        SnapsToDevicePixels = true
                    };

                    if (!string.IsNullOrWhiteSpace(te.FontFamily))
                        tb.FontFamily = new System.Windows.Media.FontFamily(te.FontFamily);

                    visual = tb;

                    // Text uses its own measured size; no explicit Width/Height.
                    tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                }
                else
                {
                    var sizePx = WorldToSceneSize(el.Width, el.Height);

                    if (el is SpriteElement se && !string.IsNullOrWhiteSpace(se.ImageSrc))
                    {
                        System.Windows.Controls.Image? img = new System.Windows.Controls.Image();
                        try
                        {
                            var resolved = ResolveSpriteImagePath(se.ImageSrc);
                            if (!string.IsNullOrWhiteSpace(resolved))
                                img.Source = new BitmapImage(ToUri(resolved));
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
                            visual = new System.Windows.Shapes.Rectangle
                            {
                                Width = sizePx.Width,
                                Height = sizePx.Height,
                                Fill = System.Windows.Media.Brushes.SteelBlue,
                                Stroke = System.Windows.Media.Brushes.White,
                                StrokeThickness = 1
                            };
                        }
                    }
                    else
                    {
                        if (el is ClickableElement)
                        {
                            visual = new System.Windows.Shapes.Rectangle
                            {
                                Width = sizePx.Width,
                                Height = sizePx.Height,
                                Fill = System.Windows.Media.Brushes.Transparent,
                                Stroke = System.Windows.Media.Brushes.DeepSkyBlue,
                                StrokeThickness = 2,
                                Opacity = 0.9
                            };
                        }
                        else
                        {
                            visual = new System.Windows.Shapes.Rectangle
                            {
                                Width = sizePx.Width,
                                Height = sizePx.Height,
                                Fill = System.Windows.Media.Brushes.DimGray,
                                Stroke = System.Windows.Media.Brushes.White,
                                StrokeThickness = 1
                            };
                        }
                    }
                }

                visual.Tag = el;

                var topLeft = WorldToScenePoint(el.X, el.Y);
                Canvas.SetLeft(visual, topLeft.X);
                Canvas.SetTop(visual, topLeft.Y);
                ContentCanvas.Children.Add(visual);

                // If selected and not text, add highlight correctly on top.
                if (ReferenceEquals(_selected, el) && el is not TextElement)
                {
                    var sizePx = WorldToSceneSize(el.Width, el.Height);
                    var highlight = new System.Windows.Shapes.Rectangle
                    {
                        Width = sizePx.Width,
                        Height = sizePx.Height,
                        Stroke = System.Windows.Media.Brushes.Orange,
                        StrokeThickness = 2,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(highlight, topLeft.X);
                    Canvas.SetTop(highlight, topLeft.Y);
                    ContentCanvas.Children.Add(highlight);
                }
            }

            RefreshOverlays();
        }

        private void SyncCameraUiFromModel()
        {
            SceneNameTextBox.Text = _scene.Name;
            CameraXTextBox.Text = _scene.Camera.X.ToString(CultureInfo.InvariantCulture);
            CameraYTextBox.Text = _scene.Camera.Y.ToString(CultureInfo.InvariantCulture);
            CameraZoomTextBox.Text = _scene.Camera.Zoom.ToString(CultureInfo.InvariantCulture);

            // ZoomSlider controls the editor view zoom (_viewCamZoom), not the scene camera.
            ZoomSlider.Value = Math.Clamp(_viewCamZoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }

        private void EditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            GizmoCanvas.Focus();

            if (e.ChangedButton == MouseButton.Middle)
            {
                _isPanning = true;
                _panStartCanvasPoint = e.GetPosition(GizmoCanvas);
                GizmoCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                var point = e.GetPosition(GizmoCanvas);

                // Gizmo hit test has priority
                var hit = HitTestGizmo(point);
                if (hit != GizmoHit.None)
                {
                    BeginGizmoDrag(hit, point);
                    GizmoCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }

                foreach (FrameworkElement child in ContentCanvas.Children)
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
                        GizmoCanvas.CaptureMouse();
                        break;
                    }
                }

                DrawGizmo();
            }
        }

        private void EditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isPanning)
            {
                _isPanning = false;
                GizmoCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                if (_isGizmoDragging)
                {
                    EndGizmoDrag();
                    GizmoCanvas.ReleaseMouseCapture();
                    RefreshHierarchy();
                    RefreshCanvas();
                    DrawGizmo();
                    e.Handled = true;
                    return;
                }

                if (_isDragging)
                {
                    _isDragging = false;
                    _dragElement = null;
                    GizmoCanvas.ReleaseMouseCapture();
                    RefreshHierarchy();
                    RefreshCanvas();
                    DrawGizmo();
                }
            }
        }

        private void EditorCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            GizmoCanvas.Focus();

            var mouse = e.GetPosition(GizmoCanvas);
            var before = SceneToWorldPoint(mouse);

            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            var newZoom = Math.Clamp(_viewCamZoom * factor, ZoomSlider.Minimum, ZoomSlider.Maximum);
            if (Math.Abs(newZoom - _viewCamZoom) < 1e-9)
                return;

            _viewCamZoom = newZoom;

            var after = SceneToWorldPoint(mouse);
            _viewCamX += (before.X - after.X);
            _viewCamY += (before.Y - after.Y);

            // Scene camera is unchanged; zoom slider now reflects editor zoom
            ZoomSlider.Value = Math.Clamp(_viewCamZoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
            RefreshCanvas();
            RefreshOverlays();
            e.Handled = true;
        }

        private void EditorCanvas_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            const double stepPixels = 40;

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

            var (worldDx, worldDy) = SceneDeltaToWorldDelta(dxPx, dyPx);
            _viewCamX -= worldDx;
            _viewCamY -= worldDy;

            RefreshCanvas();
            RefreshOverlays();
            e.Handled = true;
        }

        private void CenterCameraButton_Click(object sender, RoutedEventArgs e)
        {
            _viewCamX = 0;
            _viewCamY = 0;
            RefreshCanvas();
            RefreshOverlays();
        }

        private void EditorCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isGizmoDragging)
            {
                UpdateGizmoDrag(e.GetPosition(GizmoCanvas));
                return;
            }

            if (_isPanning)
            {
                var p = e.GetPosition(GizmoCanvas);
                var dx = p.X - _panStartCanvasPoint.X;
                var dy = p.Y - _panStartCanvasPoint.Y;
                _panStartCanvasPoint = p;

                var (worldDx, worldDy) = SceneDeltaToWorldDelta(dx, dy);
                _viewCamX -= worldDx;
                _viewCamY -= worldDy;

                RefreshCanvas();
                RefreshOverlays();
                return;
            }

            if (!_isDragging || _dragElement == null)
                return;

            var p2 = e.GetPosition(GizmoCanvas);
            var dx2 = p2.X - _dragStartCanvasPoint.X;
            var dy2 = p2.Y - _dragStartCanvasPoint.Y;
            _dragStartCanvasPoint = p2;

            var (worldDx2, worldDy2) = SceneDeltaToWorldDelta(dx2, dy2);
            _dragElement.X += worldDx2;
            _dragElement.Y += worldDy2;
            RefreshCanvas();
            DrawGizmo();
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _viewCamZoom = e.NewValue;
            _zoom = e.NewValue;
            // Do not overwrite the scene camera zoom field; camera zoom should be set explicitly by the user.
            RefreshCanvas();
        }

        private (double targetW, double targetH) GetTargetResolution()
        {
            // Prefer the scene camera's declared viewport size when present.
            if (_scene?.Camera != null && _scene.Camera.Width > 0 && _scene.Camera.Height > 0)
                return (_scene.Camera.Width, _scene.Camera.Height);

            var w = ParseDoubleSafe(TargetWidthTextBox?.Text);
            var h = ParseDoubleSafe(TargetHeightTextBox?.Text);
            if (w <= 0) w = DefaultTargetWidth;
            if (h <= 0) h = DefaultTargetHeight;
            return (w, h);
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

            if (!string.IsNullOrWhiteSpace(_loadedSceneFilePath))
            {
                var baseDir = System.IO.Path.GetDirectoryName(_loadedSceneFilePath);
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, imageSrc));
                    if (File.Exists(combined))
                        return combined;

                    if (!string.IsNullOrWhiteSpace(_projectFolderPath))
                    {
                        var combinedProject = System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectFolderPath, imageSrc));
                        if (File.Exists(combinedProject))
                            return combinedProject;
                    }

                    return combined;
                }
            }

            if (!string.IsNullOrWhiteSpace(_projectFolderPath))
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectFolderPath, imageSrc));

            return imageSrc;
        }

        private void SetPropertiesPanelVisibility(BaseElement? el)
        {
            if (ElementPropsGroup == null) return;

            var hasSelection = el != null;

            ElementPropsGroup.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            TransformPropsGroup.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            PropsButtonsPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

            SpritePropsGroup.Visibility = el is SpriteElement ? Visibility.Visible : Visibility.Collapsed;
            TextPropsGroup.Visibility = el is TextElement ? Visibility.Visible : Visibility.Collapsed;
            AudioPropsGroup.Visibility = el is AudioElement ? Visibility.Visible : Visibility.Collapsed;
            ClickablePropsGroup.Visibility = el is ClickableElement ? Visibility.Visible : Visibility.Collapsed;

            // Text and Audio don't use transform in this editor (Text sizes itself, Audio is non-visual)
            /*
            if (el is TextElement || el is AudioElement)
                TransformPropsGroup.Visibility = Visibility.Collapsed;
            */
        }

        private void SelectElement(BaseElement el)
        {
            _selected = el;
            SetPropertiesPanelVisibility(el);

            PropNameTextBox.Text = el.Name;
            PropTypeTextBlock.Text = el.ElementType;
            PropXTextBox.Text = el.X.ToString(CultureInfo.InvariantCulture);
            PropYTextBox.Text = el.Y.ToString(CultureInfo.InvariantCulture);
            PropWidthTextBox.Text = el.Width.ToString(CultureInfo.InvariantCulture);
            PropHeightTextBox.Text = el.Height.ToString(CultureInfo.InvariantCulture);

            // Clear text props by default
            PropTextContentTextBox.Text = string.Empty;
            PropFontSizeTextBox.Text = string.Empty;
            PropFontFamilyTextBox.Text = string.Empty;
            PropTextColorTextBox.Text = string.Empty;

            if (el is SpriteElement se)
                PropImageSrcTextBox.Text = se.ImageSrc ?? string.Empty;
            else
                PropImageSrcTextBox.Text = string.Empty;

            if (el is TextElement te)
            {
                PropTextContentTextBox.Text = te.Text ?? string.Empty;
                PropFontSizeTextBox.Text = te.FontSize.ToString(CultureInfo.InvariantCulture);
                PropFontFamilyTextBox.Text = te.FontFamily ?? string.Empty;
                PropTextColorTextBox.Text = te.Color ?? string.Empty;
            }

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
            _selected.Width = ParseDoubleSafe(PropWidthTextBox.Text);
            _selected.Height = ParseDoubleSafe(PropHeightTextBox.Text);

            if (_selected is SpriteElement se)
                se.ImageSrc = PropImageSrcTextBox.Text;

            if (_selected is TextElement te)
            {
                te.Text = PropTextContentTextBox.Text;
                te.FontSize = ParseDoubleSafe(PropFontSizeTextBox.Text);
                te.FontFamily = PropFontFamilyTextBox.Text;
                te.Color = PropTextColorTextBox.Text;
                // Text nodes don't use width/height; keep them at 0.
                te.Width = 0;
                te.Height = 0;
            }

            if (_selected is AudioElement ae)
            {
                ae.Src = PropAudioSrcTextBox.Text;
                ae.Loop = PropAudioLoopCheckBox.IsChecked == true;
                ae.Autoplay = PropAudioAutoplayCheckBox.IsChecked == true;
            }

            if (_selected is ClickableElement ce)
                ce.HasClickableArea = PropHasClickableCheckBox.IsChecked == true;

            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Changes applied";
        }

        private void RemoveElementButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            _scene.Elements.Remove(_selected);
            _selected = null;
            SetPropertiesPanelVisibility(null);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Element removed";
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySceneHeaderFromUi();

            var xml = SceneSerializer.Serialize(_scene);
            System.Windows.Clipboard.SetText(xml);
            StatusTextBlock.Text = "Scene XML copied to clipboard";
        }

        // Fix Uri creation (some earlier edits caused Uri ctor overload issues in build results)
        private static Uri ToUri(string pathOrUri)
            => Uri.TryCreate(pathOrUri, UriKind.Absolute, out var abs) ? abs : new Uri(pathOrUri, UriKind.Relative);

        // ---- XAML event handler forwarders (must be inside MainWindow class) ----
        private void NewSceneButton_Click(object sender, RoutedEventArgs e) => NewScene();
        private void AddSpriteButton_Click(object sender, RoutedEventArgs e) => AddSprite();
        private void AddAudioButton_Click(object sender, RoutedEventArgs e) => AddAudio();
        private void AddClickableButton_Click(object sender, RoutedEventArgs e) => AddClickable();
        private void HierarchyTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => OnHierarchySelected();
        private void StartEngineButton_Click(object sender, RoutedEventArgs e) => StartEngine();

        private void AddSprite()
        {
            var sp = new SpriteElement { Name = "Sprite" + (_scene.Elements.Count + 1), X = 0, Y = 0, Width = 100, Height = 100, ImageSrc = "" };
            _scene.Elements.Add(sp);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Sprite added";
        }

        private void AddAudio()
        {
            var audio = new AudioElement { Name = "Audio" + (_scene.Elements.Count + 1), Src = "", Loop = false, Autoplay = false };
            _scene.Elements.Add(audio);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Audio added";
        }

        private void AddClickable()
        {
            var click = new ClickableElement { Name = "Clickable" + (_scene.Elements.Count + 1), X = 0, Y = 0, Width = 200, Height = 200, HasClickableArea = true };
            _scene.Elements.Add(click);
            RefreshHierarchy();
            RefreshCanvas();
            StatusTextBlock.Text = "Clickable added";
        }

        private void OnHierarchySelected()
        {
            var item = HierarchyTreeView.SelectedItem as TreeViewItem;
            var tag = item?.Tag;
            if (tag is BaseElement el)
                SelectElement(el);
        }

        private void LoadSceneFromFile(string filePath)
        {
            _loadedSceneFilePath = filePath;

            var text = File.ReadAllText(filePath);
            var loaded = SceneSerializer.Deserialize(text);

            _scene = loaded;
            _selected = null;
            SetPropertiesPanelVisibility(null);

            // Initialize editor view from scene camera
            _viewCamX = _scene.Camera.X;
            _viewCamY = _scene.Camera.Y;
            _viewCamZoom = _scene.Camera.Zoom;

            SyncCameraUiFromModel();
            RefreshHierarchy();
            RefreshCanvas();
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

            // Initialize editor view from scene camera
            _viewCamX = _scene.Camera.X;
            _viewCamY = _scene.Camera.Y;
            _viewCamZoom = _scene.Camera.Zoom;

            SyncCameraUiFromModel();

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
            }
        }

        private void OpenEngineFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Fluxion engine folder (where the engine is installed)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            System.Windows.Forms.Application.EnableVisualStyles();

            var result = dlg.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                try
                {
                    ApplyEngineSelection(dlg.SelectedPath);
                    StatusTextBlock.Text = $"Engine folder: {_engineFolderPath}";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(this, ex.Message, "Invalid Engine Folder", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusTextBlock.Text = "Invalid engine folder";
                }
            }
        }

        private void EngineInfoButton_Click(object sender, RoutedEventArgs e)
        {
            ShowEngineInfo();
        }

        private string? ResolveEnginePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return relativePath;

            if (string.IsNullOrWhiteSpace(_engineFolderPath))
                return null;

            return System.IO.Path.GetFullPath(System.IO.Path.Combine(_engineFolderPath, relativePath));
        }

        private async void StartEngine()
        {
            if (_isStartingEngine)
                return;

            if (string.IsNullOrWhiteSpace(_engineFolderPath) || !Directory.Exists(_engineFolderPath))
            {
                System.Windows.MessageBox.Show(this, "Select a valid engine folder first.", "Start Engine", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_engineInfo == null)
            {
                System.Windows.MessageBox.Show(this, "Engine folder is not validated. Select it again.", "Start Engine", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var versionDir = System.IO.Path.GetDirectoryName(_engineInfo.VersionFilePath);
            var npmStartDir = string.IsNullOrWhiteSpace(versionDir) ? null : System.IO.Path.GetDirectoryName(versionDir);
            if (string.IsNullOrWhiteSpace(npmStartDir) || !Directory.Exists(npmStartDir))
            {
                System.Windows.MessageBox.Show(this, "Cannot determine npm start directory (expected one level above version.py).", "Start Engine", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _isStartingEngine = true;
            if (StartEngineButton != null)
                StartEngineButton.IsEnabled = false;

            try
            {
                StatusTextBlock.Text = "Running npm install...";
                var sb = new System.Text.StringBuilder();

                void Log(string line)
                {
                    sb.AppendLine(line);
                }

                var installExit = await RunProcessAsync("cmd.exe", "/c npm install", _engineFolderPath, Log);
                if (installExit != 0)
                {
                    System.Windows.MessageBox.Show(this, sb.ToString(), $"npm install failed (exit {installExit})", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusTextBlock.Text = "npm install failed";
                    return;
                }

                StatusTextBlock.Text = "Starting engine (npm start)...";

                // Start npm start in a visible terminal window so the user can see logs; don't block the editor.
                var startPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k npm start",
                    WorkingDirectory = npmStartDir,
                    UseShellExecute = true
                };

                System.Diagnostics.Process.Start(startPsi);
                StatusTextBlock.Text = "Engine started";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "Start Engine Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "Failed to start engine";
            }
            finally
            {
                _isStartingEngine = false;
                if (StartEngineButton != null)
                    StartEngineButton.IsEnabled = true;
            }
        }
    }

    // Models remain unchanged...
}