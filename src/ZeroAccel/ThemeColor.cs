using System.ComponentModel;
using System.Windows.Media;

namespace ZeroAccel;

// A stable source for each shared theme brush. Explicit notifications update
// existing drawings even when their controls were hidden during a theme change.
public sealed class ThemeColor : INotifyPropertyChanged
{
    private Color value;
    public Color Value
    {
        get => value;
        set
        {
            if (this.value == value) return;
            this.value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
