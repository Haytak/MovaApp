using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using Microsoft.Win32;

namespace Mova
{
    public partial class MainWindow : Window
    {
        private readonly CaretTracker _tracker;
        private readonly NotifyIcon _notifyIcon;
        private readonly Storyboard _fadeIn;
        private readonly Storyboard _fadeOut;
        private DateTime _lastUpdate = DateTime.MinValue;
        private readonly string[] _blacklist = { "vlc", "mpc-hc", "mpv" }; // Приклади програм для ігнорування
        private readonly System.Collections.Generic.Dictionary<string, FrameworkElement> _flags = new();
        private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "MovaApp";

        public MainWindow()
        {
            InitializeComponent();
            
            _tracker = new CaretTracker();
            _tracker.CaretUpdated += OnCaretUpdated;

            _fadeIn = (Storyboard)Resources["FadeInStoryboard"];
            _fadeOut = (Storyboard)Resources["FadeOutStoryboard"];

            // Кешуємо прапори для швидкодії та надійності
            foreach (var child in FlagContainer.Children)
            {
                if (child is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                {
                    _flags[fe.Name] = fe;
                }
            }

            _notifyIcon = new NotifyIcon
            {
                Icon = GetAppIcon(),
                Visible = true,
                Text = "Mova App"
            };
            
            UpdateContextMenu();
        }

        private System.Drawing.Icon GetAppIcon()
        {
            try
            {
                // Спробуємо отримати іконку з самого EXE файлу (працює і для Single File)
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                {
                    return System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Shield;
                }
            }
            catch { }
            return System.Drawing.SystemIcons.Shield;
        }

        private void UpdateContextMenu()
        {
            var menu = new ContextMenuStrip();
            
            // Пункт автозапуску
            var startupItem = new ToolStripMenuItem("Запускати разом з Windows");
            startupItem.CheckOnClick = true;
            startupItem.Checked = IsInStartup();
            startupItem.Click += (s, e) => ToggleStartup(startupItem.Checked);
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());
            
            _notifyIcon.ContextMenuStrip = menu;
        }

        private bool IsInStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private void ToggleStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Помилка при зміні автозапуску: {ex.Message}");
            }
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, extendedStyle | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        }

        private void OnCaretUpdated(object? sender, CaretEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Якщо сигнал про невидимість — ховаємо і виходимо
                if (!e.IsVisible)
                {
                    FlagBorder.Opacity = 0;
                    return;
                }

                if (string.IsNullOrEmpty(e.LanguageCode)) return;

                // Перевірка часу останнього оновлення, щоб уникнути мерехтіння
                if ((DateTime.Now - _lastUpdate).TotalMilliseconds < 50) return;

                // Перевірка чорного списку
                try
                {
                    IntPtr foregroundWnd = Win32.GetForegroundWindow();
                    Win32.GetWindowThreadProcessId(foregroundWnd, out uint pid);
                    using var process = Process.GetProcessById((int)pid);
                    if (_blacklist.Any(b => process.ProcessName.Contains(b, StringComparison.OrdinalIgnoreCase)))
                    {
                        FlagBorder.Opacity = 0;
                        return;
                    }
                }
                catch { }

                // Приховуємо всі прапори спочатку
                foreach (var flag in _flags.Values)
                {
                    flag.Visibility = Visibility.Collapsed;
                }

                // Показуємо потрібний прапор
                string targetName = e.LanguageCode + "Flag";
                if (_flags.TryGetValue(targetName, out var targetFlag))
                {
                    targetFlag.Visibility = Visibility.Visible;
                }
                else
                {
                    // Показуємо прапор 404 для непідтримуваних мов
                    if (_flags.TryGetValue("NotFoundFlag", out var fallbackFlag))
                    {
                        fallbackFlag.Visibility = Visibility.Visible;
                    }
                }

                // Конвертація пікселів у логічні одиниці WPF (врахування DPI)
                var visualDpi = VisualTreeHelper.GetDpi(this);
                double dpiX = visualDpi.DpiScaleX;
                double dpiY = visualDpi.DpiScaleY;

                // Встановлюємо позицію
                // x / dpiX перетворює фізичні пікселі в логічні одиниці WPF
                double newLeft = (e.CaretRect.Right / dpiX) + (2 / dpiX);
                double newTop = (e.CaretRect.Top / dpiY) - (16 / dpiY);

                // Плавне переміщення, щоб уникнути "стрибків"
                if (Math.Abs(this.Left - newLeft) > 1 || Math.Abs(this.Top - newTop) > 1)
                {
                    this.Left = newLeft;
                    this.Top = newTop;
                }

                // Завжди показуємо прапорець та оновлюємо стан для Dexpot
                if (FlagBorder.Opacity < 1) FlagBorder.Opacity = 1;
                
                // Хай для підтримки віртуальних робочих столів: вікно має бути на всіх столах
                // Для WPF це зазвичай автоматично, якщо вікно має стилі TOOLWINDOW і NOACTIVATE,
                // але Dexpot іноді потребує перевстановлення власника або статусу.
                
                _lastUpdate = DateTime.Now;
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _tracker.Dispose();
            _notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}
