using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Interop.UIAutomationClient;

namespace Mova
{
    public class CaretEventArgs : EventArgs
    {
        public Rect CaretRect { get; set; }
        public string LanguageCode { get; set; } = "EN";
        public bool IsVisible { get; set; } = true;
    }

    public class CaretTracker : IDisposable, IUIAutomationFocusChangedEventHandler
    {
        private readonly CUIAutomation8 _automation;
        private readonly DispatcherTimer _fallbackTimer;
        private bool _disposed;
        
        public event EventHandler<CaretEventArgs>? CaretUpdated;

        public CaretTracker()
        {
            _automation = new CUIAutomation8();
            
            // Підписка на зміну фокусу
            _automation.AddFocusChangedEventHandler(null, this);

            // Таймер для постійного оновлення
            _fallbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100) // Більш часто для плавності
            };
            _fallbackTimer.Tick += OnTimerTick;
            _fallbackTimer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            try
            {
                // Спробуємо отримати елемент через UIA фокус
                var focused = _automation.GetFocusedElement();
                
                // Якщо UIA не дає фокусу, спробуємо отримати його через Win32 дескриптор
                if (focused == null)
                {
                    Win32.GUITHREADINFO gti = new Win32.GUITHREADINFO();
                    gti.cbSize = Marshal.SizeOf(typeof(Win32.GUITHREADINFO));
                    if (Win32.GetGUIThreadInfo(0, ref gti) && gti.hwndFocus != IntPtr.Zero)
                    {
                        focused = _automation.ElementFromHandle(gti.hwndFocus);
                    }
                }

                if (focused != null)
                {
                    UpdateCaretFromUIA(focused);
                }
                else
                {
                    UpdateCaretFallback();
                }
            }
            catch
            {
                UpdateCaretFallback();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fallbackTimer.Stop();
            try { _automation.RemoveAllEventHandlers(); } catch { }
        }

        public void HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            Task.Run(() => UpdateCaretFromUIA(sender));
        }

        private void UpdateCaretFromUIA(IUIAutomationElement element)
        {
            try
            {
                if (element == null) return;

                // Фільтрація: ігноруємо елементи, які точно не є полями вводу
                // UIA_ButtonControlTypeId = 50000, UIA_ImageControlTypeId = 50006, UIA_MenuControlTypeId = 50009
                // UIA_ListItemControlTypeId = 50007, UIA_TreeItemControlTypeId = 50024, UIA_GroupControlTypeId = 50026
                int controlType = element.CurrentControlType;
                if (controlType == 50000 || controlType == 50006 || controlType == 50009 || 
                    controlType == 50007 || controlType == 50011 || controlType == 50024 || 
                    controlType == 50026 || controlType == 50033 || controlType == 50012)
                {
                    NotifyUpdate(Rect.Empty, false);
                    return;
                }

                // Перевіряємо, чи підтримує елемент TextPattern або TextPattern2
                // Це головна ознака того, що перед нами поле вводу або текст
                var pattern = element.GetCurrentPattern(10014) as IUIAutomationTextPattern;
                
                if (pattern == null)
                {
                    // Для Gmail та деяких Chromium-додатків фокус може бути на дочірньому елементі,
                    // який не має TextPattern, але він є у батька.
                    try
                    {
                        var walker = _automation.ControlViewWalker;
                        var parent = walker.GetParentElement(element);
                        if (parent != null)
                        {
                            pattern = parent.GetCurrentPattern(10014) as IUIAutomationTextPattern;
                        }
                    }
                    catch { }

                    if (pattern == null)
                    {
                        NotifyUpdate(Rect.Empty, false);
                        return;
                    }
                }

                if (pattern != null)
                {
                    IUIAutomationTextRange? range = null;
                    
                    // Chromium (Opera) та Gmail часто краще працюють з GetCaretRange або GetSelection
                    var pattern2 = pattern as IUIAutomationTextPattern2;
                    if (pattern2 != null)
                    {
                        try { range = pattern2.GetCaretRange(out _); } catch { }
                    }

                    // Якщо каретку не знайдено, пробуємо виділення (Selection)
                    // У Chromium порожня каретка часто представлена як Selection з нульовою довжиною
                    if (range == null)
                    {
                        try
                        {
                            var selection = pattern.GetSelection();
                            if (selection != null && selection.Length > 0)
                            {
                                range = selection.GetElement(0);
                            }
                        }
                        catch { }
                    }

                    if (range != null)
                    {
                        // Спробуємо отримати координати
                        var boundingRects = range.GetBoundingRectangles();
                        
                        // ХАК для Chromium/Opera: якщо координати "зависли" або порожні
                        if (boundingRects == null || boundingRects.Length == 0)
                        {
                            try
                            {
                                // Клонуємо і розширюємо діапазон, щоб змусити UIA оновити координати
                                var clone = range.Clone();
                                clone.ExpandToEnclosingUnit(TextUnit.TextUnit_Character);
                                boundingRects = clone.GetBoundingRectangles();
                                
                                // Якщо після розширення ми отримали координати "кінця" символу (Chromium іноді так робить),
                                // то беремо першу точку, а не останню.
                            }
                            catch { }
                        }

                        if (boundingRects != null && boundingRects.Length >= 4)
                        {
                            // Для Chromium у порожніх полях:
                            // Якщо range порожній, UIA іноді повертає координати всього текстового поля.
                            // Ми намагаємось взяти початок (перші 4 значення), а не кінець.
                            double x = (double)boundingRects.GetValue(0)!;
                            double y = (double)boundingRects.GetValue(1)!;
                            double w = (double)boundingRects.GetValue(2)!;
                            double h = (double)boundingRects.GetValue(3)!;

                            // НОВИЙ ХАК: Якщо ширина прямокутника занадто велика (більше 50 пікселів),
                            // це означає, що Chromium видав нам координати всього рядка вводу замість каретки.
                            // У такому разі ми ЗАВЖДИ беремо початок (лівий край).
                            if (w > 50 && boundingRects.Length == 4)
                            {
                                // Беремо початок, ширину встановлюємо в 1 (імітація каретки)
                                w = 1;
                            }
                            // Але якщо це не порожнє поле, і ми реально маємо декілька прямокутників (наприклад, перенос рядка),
                            // тоді краще брати останній (де каретка).
                            else if (boundingRects.Length > 4)
                            {
                                int lastIdx = boundingRects.Length - 4;
                                x = (double)boundingRects.GetValue(lastIdx)!;
                                y = (double)boundingRects.GetValue(lastIdx + 1)!;
                                w = (double)boundingRects.GetValue(lastIdx + 2)!;
                                h = (double)boundingRects.GetValue(lastIdx + 3)!;
                            }

                            NotifyUpdate(new Rect(x, y, w, h), true);
                            return;
                        }
                    }
                }

                // Якщо це невеликий елемент (наприклад, адресний рядок браузера), використовуємо його координати
                var bounds = element.CurrentBoundingRectangle;
                int width = bounds.right - bounds.left;
                int height = bounds.bottom - bounds.top;
                if (width > 0 && width < 800 && height > 0 && height < 100)
                {
                     // Для адресних рядків і подібного беремо праву частину або початок
                     NotifyUpdate(new Rect(bounds.left, bounds.top, width, height), true);
                     return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UIA Error: {ex.Message}");
            }

            UpdateCaretFallback();
        }

        private void UpdateCaretFallback()
        {
            var guiInfo = new Win32.GUITHREADINFO();
            guiInfo.cbSize = Marshal.SizeOf(guiInfo);

            IntPtr foregroundWnd = Win32.GetForegroundWindow();
            uint threadId = Win32.GetWindowThreadProcessId(foregroundWnd, out _);
            
            if (Win32.GetGUIThreadInfo(threadId, ref guiInfo))
            {
                var caretRect = guiInfo.rcCaret;
                
                // Якщо координати порожні, приховуємо
                if (caretRect.left == 0 && caretRect.top == 0 && caretRect.right == 0 && caretRect.bottom == 0)
                {
                    NotifyUpdate(Rect.Empty, false);
                    return;
                }

                var clientPoint = new Win32.POINT { x = caretRect.left, y = caretRect.top };
                Win32.ClientToScreen(guiInfo.hwndCaret != IntPtr.Zero ? guiInfo.hwndCaret : guiInfo.hwndFocus, ref clientPoint);

                NotifyUpdate(new Rect(clientPoint.x, clientPoint.y, caretRect.right - caretRect.left, caretRect.bottom - caretRect.top), true);
            }
            else
            {
                // Якщо не вдалося отримати інфо, приховуємо
                NotifyUpdate(Rect.Empty, false);
            }
        }

        private void NotifyUpdate(Rect caretRect, bool isVisible)
        {
            // Більш м'яка перевірка: ігноруємо тільки якщо і X, і Y дорівнюють 0
            if (isVisible && caretRect.X == 0 && caretRect.Y == 0) return;

            string lang = GetCurrentLanguage();
            CaretUpdated?.Invoke(this, new CaretEventArgs 
            { 
                CaretRect = caretRect,
                LanguageCode = lang,
                IsVisible = isVisible
            });
        }

        private string GetCurrentLanguage()
        {
            try
            {
                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "EN";

                // Отримуємо потік активного вікна
                uint threadId = Win32.GetWindowThreadProcessId(hwnd, out _);
                
                // Спробуємо уточнити потік, якщо є фокус на конкретному елементі
                Win32.GUITHREADINFO gti = new Win32.GUITHREADINFO();
                gti.cbSize = Marshal.SizeOf(typeof(Win32.GUITHREADINFO));
                if (Win32.GetGUIThreadInfo(0, ref gti) && gti.hwndFocus != IntPtr.Zero)
                {
                    uint focusThreadId = Win32.GetWindowThreadProcessId(gti.hwndFocus, out _);
                    if (focusThreadId != 0) threadId = focusThreadId;
                }

                IntPtr layout = Win32.GetKeyboardLayout(threadId);
                ushort langId = (ushort)((long)layout & 0xFFFF);
                ushort primaryLangId = (ushort)(langId & 0x3FF);
                ushort subLangId = (ushort)(langId >> 10);
                
                string langCode = "UNKNOWN";
                if (primaryLangId == 0x22) langCode = "UA"; // Ukrainian
                else if (primaryLangId == 0x09) langCode = "EN"; // English
                else if (primaryLangId == 0x0A) langCode = "ES"; // Spanish
                else if (primaryLangId == 0x03) langCode = "ES"; // Catalan (pointing to Spanish)
                else if (primaryLangId == 0x2D) langCode = "ES"; // Basque (pointing to Spanish)
                else if (primaryLangId == 0x56) langCode = "ES"; // Galician (pointing to Spanish)
                else if (primaryLangId == 0x07) langCode = "DE"; // German
                else if (primaryLangId == 0x11) langCode = "JA"; // Japanese
                else if (primaryLangId == 0x05) langCode = "CS"; // Czech
                else if (primaryLangId == 0x0C) langCode = "FR"; // French
                else if (primaryLangId == 0x10) langCode = "IT"; // Italian
                else if (primaryLangId == 0x15) langCode = "PL"; // Polish
                else if (primaryLangId == 0x16) // Portuguese
                {
                    langCode = (subLangId == 0x01) ? "BR" : "PT"; // Brazil vs Portugal
                }
                else if (primaryLangId == 0x04) langCode = "ZH"; // Chinese
                else if (primaryLangId == 0x12) langCode = "KO"; // Korean
                else if (primaryLangId == 0x39) langCode = "HI"; // Hindi
                else if (primaryLangId == 0x01) langCode = "AR"; // Arabic
                else if (primaryLangId == 0x13) langCode = "NL"; // Dutch
                else if (primaryLangId == 0x1D) langCode = "SV"; // Swedish
                else if (primaryLangId == 0x0B) langCode = "FI"; // Finnish
                else if (primaryLangId == 0x06) langCode = "DA"; // Danish
                else if (primaryLangId == 0x14) langCode = "NO"; // Norwegian
                else if (primaryLangId == 0x1B) langCode = "SK"; // Slovak
                else if (primaryLangId == 0x0E) langCode = "HU"; // Hungarian
                else if (primaryLangId == 0x1F) langCode = "TR"; // Turkish
                else if (primaryLangId == 0x1E) langCode = "TH"; // Thai
                else if (primaryLangId == 0x2A) langCode = "VI"; // Vietnamese
                else if (primaryLangId == 0x21) langCode = "ID"; // Indonesian
                else if (primaryLangId == 0x08) langCode = "EL"; // Greek
                else if (primaryLangId == 0x0D) langCode = "HE"; // Hebrew
                else if (primaryLangId == 0x18) langCode = "RO"; // Romanian
                else if (primaryLangId == 0x02) langCode = "BG"; // Bulgarian
                else if (primaryLangId == 0x37) langCode = "KA"; // Georgian
                else if (primaryLangId == 0x26) langCode = "LV"; // Latvian
                else if (primaryLangId == 0x27) langCode = "LT"; // Lithuanian
                else if (primaryLangId == 0x25) langCode = "ET"; // Estonian
                else if (primaryLangId == 0x44) langCode = "CRH"; // Crimean Tatar
                else if (primaryLangId == 0x3F) langCode = "KZ"; // Kazakh
                else if (primaryLangId == 0x2B) langCode = "HY"; // Armenian
                else if (primaryLangId == 0x2C) langCode = "AZ"; // Azerbaijani
                else if (primaryLangId == 0x43) langCode = "UZ"; // Uzbek
                else if (primaryLangId == 0x1A) langCode = "HR"; // Croatian
                else if (primaryLangId == 0x24) langCode = "SL"; // Slovenian
                else if (primaryLangId == 0x0F) langCode = "IS"; // Icelandic
                else if (primaryLangId == 0x1C) langCode = "SQ"; // Albanian
                else if (primaryLangId == 0x40) langCode = "KY"; // Kyrgyz
                else if (primaryLangId == 0x28) langCode = "TG"; // Tajik
                else if (primaryLangId == 0x42) langCode = "TK"; // Turkmen
                else if (primaryLangId == 0x41) langCode = "SW"; // Swahili
                else if (primaryLangId == 0x4E) langCode = "PA"; // Punjabi
                else if (primaryLangId == 0x4A) langCode = "TE"; // Telugu
                else if (primaryLangId == 0x49) langCode = "TA"; // Tamil
                else if (primaryLangId == 0x4B) langCode = "KN"; // Kannada
                else if (primaryLangId == 0x4C) langCode = "ML"; // Malayalam
                else if (primaryLangId == 0x51) langCode = "TS"; // Tibetan
                else if (primaryLangId == 0x50) langCode = "MN"; // Mongolian
                
                return langCode;
            }
            catch
            {
                return "EN";
            }
        }
    }
}
