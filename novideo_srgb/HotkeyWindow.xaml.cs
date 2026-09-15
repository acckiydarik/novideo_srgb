using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using MessageBox = System.Windows.Forms.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;

namespace novideo_srgb
{
    public partial class HotkeyWindow : Window
    {
        public ModifierKeys ResultModifiers { get; private set; }
        public Key ResultKey { get; private set; }

        private readonly ModifierKeys _originalModifiers;
        private readonly Key _originalKey;
        private bool _skipClosingPrompt;

        public HotkeyWindow(ModifierKeys currentModifiers, Key currentKey)
        {
            InitializeComponent();
            _originalModifiers = currentModifiers;
            _originalKey = currentKey;
            ResultModifiers = currentModifiers;
            ResultKey = currentKey;
            UpdateHotkeyBoxText();

            Closing += HotkeyWindow_Closing;
        }

        private void HotkeyWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_skipClosingPrompt) return;
            if (ResultModifiers == _originalModifiers && ResultKey == _originalKey) return;

            var result = MessageBox.Show("Save changes to the hotkey?", "Unsaved changes",
                MessageBoxButtons.YesNoCancel);

            switch (result)
            {
                case WinFormsDialogResult.Yes:
                    _skipClosingPrompt = true;
                    DialogResult = true;
                    break;
                case WinFormsDialogResult.No:
                    _skipClosingPrompt = true;
                    DialogResult = false;
                    break;
                case WinFormsDialogResult.Cancel:
                    e.Cancel = true;
                    break;
            }
        }

        private void UpdateHotkeyBoxText()
        {
            HotkeyBox.Text = MainWindow.FormatHotkey(ResultModifiers, ResultKey);
        }

        private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
        {
            HotkeyBox.Text = "Press keys...";
        }

        private void HotkeyBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            HotkeyBox.Text = "Press keys...";
        }

        private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateHotkeyBoxText();
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None) return;

            ResultModifiers = Keyboard.Modifiers;
            ResultKey = key;
            UpdateHotkeyBoxText();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ResultModifiers = ModifierKeys.None;
            ResultKey = Key.None;
            UpdateHotkeyBoxText();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _skipClosingPrompt = true;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _skipClosingPrompt = true;
            DialogResult = false;
        }
    }
}
