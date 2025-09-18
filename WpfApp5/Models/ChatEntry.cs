using System;
using System.ComponentModel;

namespace KakaoPcLogger.Models
{
    public class ChatEntry : INotifyPropertyChanged
    {
        public IntPtr Hwnd { get; set; }
        public IntPtr ParentHwnd { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public int Pid { get; set; }

        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public string HwndHex => $"0x{Hwnd.ToInt64():X}";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
