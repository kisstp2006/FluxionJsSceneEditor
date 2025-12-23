using System;
using System.ComponentModel;
using System.Globalization;

namespace FluxionJsSceneEditor.Controls
{
    public partial class ColorPickerFlyout : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        private byte _r;
        private byte _g;
        private byte _b;
        private string _hex = "#ffffff";
        private bool _updating;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<string>? ColorCommitted;

        public ColorPickerFlyout()
        {
            InitializeComponent();
            DataContext = this;
            SetFromHex(_hex);
        }

        public byte R
        {
            get => _r;
            set
            {
                if (_r == value) return;
                _r = value;
                OnRgbChanged();
                OnPropertyChanged(nameof(R));
            }
        }

        public byte G
        {
            get => _g;
            set
            {
                if (_g == value) return;
                _g = value;
                OnRgbChanged();
                OnPropertyChanged(nameof(G));
            }
        }

        public byte B
        {
            get => _b;
            set
            {
                if (_b == value) return;
                _b = value;
                OnRgbChanged();
                OnPropertyChanged(nameof(B));
            }
        }

        public string Hex
        {
            get => _hex;
            set
            {
                value ??= string.Empty;
                if (string.Equals(_hex, value, StringComparison.OrdinalIgnoreCase)) return;
                _hex = value;
                OnHexChanged();
                OnPropertyChanged(nameof(Hex));
            }
        }

        public System.Windows.Media.Brush PreviewBrush => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(R, G, B));

        public void SetColorString(string? color)
        {
            var c = (color ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(c))
                c = "#ffffff";

            SetFromHex(c);
        }

        private void OnRgbChanged()
        {
            if (_updating) return;
            _updating = true;
            try
            {
                _hex = ToHex(R, G, B);
                OnPropertyChanged(nameof(Hex));
                OnPropertyChanged(nameof(PreviewBrush));
                ColorCommitted?.Invoke(this, _hex);
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnHexChanged()
        {
            if (_updating) return;

            if (!TryParseHex(_hex, out var r, out var g, out var b))
                return;

            _updating = true;
            try
            {
                _r = r;
                _g = g;
                _b = b;
                OnPropertyChanged(nameof(R));
                OnPropertyChanged(nameof(G));
                OnPropertyChanged(nameof(B));
                OnPropertyChanged(nameof(PreviewBrush));
                ColorCommitted?.Invoke(this, ToHex(_r, _g, _b));
            }
            finally
            {
                _updating = false;
            }
        }

        private void SetFromHex(string text)
        {
            if (!TryParseHex(text, out var r, out var g, out var b))
            {
                r = 255;
                g = 255;
                b = 255;
            }

            _updating = true;
            try
            {
                _r = r;
                _g = g;
                _b = b;
                _hex = ToHex(_r, _g, _b);
            }
            finally
            {
                _updating = false;
            }

            OnPropertyChanged(nameof(R));
            OnPropertyChanged(nameof(G));
            OnPropertyChanged(nameof(B));
            OnPropertyChanged(nameof(Hex));
            OnPropertyChanged(nameof(PreviewBrush));
        }

        private static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}".ToLowerInvariant();

        private static bool TryParseHex(string text, out byte r, out byte g, out byte b)
        {
            r = g = b = 255;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var s = text.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
                s = s[1..];

            if (s.Length == 3)
            {
                // RGB shorthand
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            }

            if (s.Length != 6)
                return false;

            if (!byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return false;
            if (!byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return false;
            if (!byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return false;
            return true;
        }

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
